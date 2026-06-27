using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Steamworks;

namespace SteamPhantom.Worker;

internal static class Program
{
    private static int _quit;
    private static uint _appId;
    private static TaskCompletionSource<EResult>? _statsTcs;
    private static Callback<UserStatsReceived_t>? _statsCallback;
    private static Callback<UserAchievementIconFetched_t>? _iconCallback;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length < 1 || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out _appId))
        {
            Console.Error.WriteLine("usage: SteamPhantom.Worker <appid>");
            return 2;
        }

        try
        {
            File.WriteAllText("steam_appid.txt", _appId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"couldn't write steam_appid.txt: {ex.Message}");
            return 5;
        }

        bool initOk;
        try { initOk = SteamAPI.Init(); }
        catch (Exception ex) { Console.Error.WriteLine($"SteamAPI.Init threw: {ex.Message}"); return 4; }

        if (!initOk)
        {
            Console.Error.WriteLine("SteamAPI.Init returned false. Steam isn't running, or you don't own this app.");
            return 3;
        }

        // Hold refs so the callbacks aren't GC'd.
        _statsCallback = Callback<UserStatsReceived_t>.Create(OnStatsReceived);
        _iconCallback  = Callback<UserAchievementIconFetched_t>.Create(_ => { /* polled in HandleList */ });

        var pumpThread = new Thread(CallbackPump) { IsBackground = true, Name = "SteamCallbackPump" };
        pumpThread.Start();

        Console.Out.WriteLine("READY");
        Console.Out.Flush();

        while (Volatile.Read(ref _quit) == 0)
        {
            string? line;
            try { line = Console.In.ReadLine(); }
            catch { break; }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            try { await HandleCommand(line); }
            catch (Exception ex) { WriteError($"unhandled: {ex.Message}"); }
        }

        Interlocked.Exchange(ref _quit, 1);
        pumpThread.Join(500);
        try { SteamAPI.Shutdown(); } catch { }
        return 0;
    }

    private static void CallbackPump()
    {
        while (Volatile.Read(ref _quit) == 0)
        {
            try { SteamAPI.RunCallbacks(); } catch { }
            Thread.Sleep(50);
        }
    }

    private static async Task HandleCommand(string line)
    {
        Command? cmd;
        try { cmd = JsonSerializer.Deserialize<Command>(line); }
        catch (JsonException ex) { WriteError($"malformed JSON: {ex.Message}"); return; }

        if (cmd is null || string.IsNullOrEmpty(cmd.Cmd))
        {
            WriteError("missing 'cmd' field");
            return;
        }

        switch (cmd.Cmd.ToLowerInvariant())
        {
            case "idle":
                WriteOk();
                break;

            case "list-achievements":
                await HandleList();
                break;

            case "set":
                if (string.IsNullOrEmpty(cmd.Apiname)) { WriteError("apiname required"); break; }
                if (SteamUserStats.SetAchievement(cmd.Apiname)) WriteOk();
                else WriteError($"'{cmd.Apiname}': Steam refused SetAchievement. Likely a stat-driven achievement (auto-awarded when in-game counters hit a threshold) — not directly settable from outside the game.");
                break;

            case "clear":
                if (string.IsNullOrEmpty(cmd.Apiname)) { WriteError("apiname required"); break; }
                if (SteamUserStats.ClearAchievement(cmd.Apiname)) WriteOk();
                else WriteError($"'{cmd.Apiname}': Steam refused ClearAchievement.");
                break;

            case "store":
                if (SteamUserStats.StoreStats()) WriteOk();
                else WriteError("Steam refused StoreStats (no changes to commit, or stats not loaded).");
                break;

            case "apply-batch":
                HandleBatch(cmd);
                break;

            case "get-stats":
                await HandleGetStats(cmd);
                break;

            case "apply-stats-batch":
                HandleApplyStatsBatch(cmd);
                break;

            case "quit":
                WriteOk();
                Interlocked.Exchange(ref _quit, 1);
                break;

            default:
                WriteError($"unknown command '{cmd.Cmd}'");
                break;
        }
    }

    private static async Task HandleList()
    {
        // 1) Make sure stats are loaded
        var tcs = new TaskCompletionSource<EResult>();
        _statsTcs = tcs;
        if (!SteamUserStats.RequestCurrentStats())
        {
            _statsTcs = null;
            WriteError("RequestCurrentStats returned false; not signed into Steam?");
            return;
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (done != tcs.Task)
        {
            _statsTcs = null;
            WriteError("timed out waiting for achievement stats");
            return;
        }
        var result = await tcs.Task;
        if (result != EResult.k_EResultOK)
        {
            WriteError($"RequestCurrentStats failed: {result}");
            return;
        }

        // 2) Kick off icon fetches for every achievement
        var count = SteamUserStats.GetNumAchievements();
        var apinames = new List<string>((int)count);
        var iconHandles = new Dictionary<string, int>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var name = SteamUserStats.GetAchievementName(i);
            if (string.IsNullOrEmpty(name)) continue;
            apinames.Add(name);
            iconHandles[name] = SteamUserStats.GetAchievementIcon(name);
        }

        // 3) Poll for late icon arrivals (Steam fetches them async on first call)
        var waited = 0;
        while (waited < 3000 && iconHandles.Values.Any(v => v == 0))
        {
            await Task.Delay(150);
            waited += 150;
            foreach (var n in iconHandles.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList())
                iconHandles[n] = SteamUserStats.GetAchievementIcon(n);
        }

        // 4) Save each icon to disk; build response
        var iconDir = Path.Combine(Path.GetTempPath(), "SteamPhantom", "icons", _appId.ToString());
        Directory.CreateDirectory(iconDir);

        var list = new List<AchievementDto>(apinames.Count);
        foreach (var apiname in apinames)
        {
            SteamUserStats.GetAchievementAndUnlockTime(apiname, out var achieved, out var unlockTime);
            var name = SteamUserStats.GetAchievementDisplayAttribute(apiname, "name") ?? "";
            var desc = SteamUserStats.GetAchievementDisplayAttribute(apiname, "desc") ?? "";
            var hidden = SteamUserStats.GetAchievementDisplayAttribute(apiname, "hidden") == "1";

            string? iconPath = null;
            uint iw = 0, ih = 0;
            var handle = iconHandles[apiname];
            if (handle != 0 && SteamUtils.GetImageSize(handle, out var w, out var h) && w > 0 && h > 0)
            {
                var bufSize = (int)(w * h * 4);
                var buf = new byte[bufSize];
                if (SteamUtils.GetImageRGBA(handle, buf, bufSize))
                {
                    iconPath = Path.Combine(iconDir, $"{Sanitize(apiname)}.bin");
                    try
                    {
                        var hdr = new byte[8];
                        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), w);
                        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), h);
                        using var fs = File.Create(iconPath);
                        fs.Write(hdr, 0, hdr.Length);
                        fs.Write(buf, 0, buf.Length);
                        iw = w; ih = h;
                    }
                    catch { iconPath = null; }
                }
            }

            list.Add(new AchievementDto
            {
                Apiname = apiname,
                Name = string.IsNullOrEmpty(name) ? apiname : name,
                Desc = desc,
                Achieved = achieved,
                UnlockTime = unlockTime,
                Hidden = hidden,
                IconPath = iconPath,
                IconW = iw,
                IconH = ih,
            });
        }

        WriteOk(list);
    }

    private static void HandleBatch(Command cmd)
    {
        if (cmd.Changes is null || cmd.Changes.Length == 0)
        {
            WriteOk(Array.Empty<BatchFailure>());
            return;
        }

        var failures = new List<BatchFailure>();
        foreach (var ch in cmd.Changes)
        {
            if (string.IsNullOrEmpty(ch.Apiname)) continue;
            try
            {
                var ok = ch.Achieved
                    ? SteamUserStats.SetAchievement(ch.Apiname)
                    : SteamUserStats.ClearAchievement(ch.Apiname);
                if (!ok)
                {
                    failures.Add(new BatchFailure
                    {
                        Apiname = ch.Apiname,
                        Error = ch.Achieved
                            ? "Steam refused SetAchievement (likely stat-driven)."
                            : "Steam refused ClearAchievement.",
                    });
                }
            }
            catch (Exception ex)
            {
                failures.Add(new BatchFailure { Apiname = ch.Apiname, Error = ex.Message });
            }
        }

        // Single StoreStats commits everything that did get set/cleared.
        var doStore = cmd.Store ?? true;
        if (doStore && !SteamUserStats.StoreStats())
        {
            failures.Add(new BatchFailure { Apiname = "", Error = "StoreStats returned false." });
        }

        WriteOk(failures);
    }

    private static async Task HandleGetStats(Command cmd)
    {
        // Make sure stats are loaded before probing.
        if (!await EnsureStatsLoaded())
        {
            WriteError("Stats aren't loaded.");
            return;
        }

        var apinames = cmd.Stats?.Select(s => s.Apiname).Where(a => !string.IsNullOrEmpty(a)).ToList()
                       ?? new List<string>();
        var result = new List<StatValueDto>(apinames.Count);
        foreach (var apiname in apinames)
        {
            // Probe int first, fall back to float. Steam exposes one or the other per stat.
            if (SteamUserStats.GetStat(apiname, out int intVal))
            {
                result.Add(new StatValueDto { Apiname = apiname, Type = "int", Value = intVal });
            }
            else if (SteamUserStats.GetStat(apiname, out float floatVal))
            {
                result.Add(new StatValueDto { Apiname = apiname, Type = "float", Value = floatVal });
            }
            else
            {
                result.Add(new StatValueDto { Apiname = apiname, Type = "", Value = 0, Error = "Couldn't read" });
            }
        }
        WriteOk(result);
    }

    private static void HandleApplyStatsBatch(Command cmd)
    {
        if (cmd.Stats is null || cmd.Stats.Length == 0)
        {
            WriteOk(Array.Empty<BatchFailure>());
            return;
        }

        var failures = new List<BatchFailure>();
        foreach (var s in cmd.Stats)
        {
            if (string.IsNullOrEmpty(s.Apiname)) continue;
            try
            {
                var ok = s.Type?.ToLowerInvariant() switch
                {
                    "int"   => SteamUserStats.SetStat(s.Apiname, (int)s.Value),
                    "float" => SteamUserStats.SetStat(s.Apiname, (float)s.Value),
                    _       => false,
                };
                if (!ok)
                {
                    failures.Add(new BatchFailure
                    {
                        Apiname = s.Apiname,
                        Error = $"Steam refused SetStat (type '{s.Type}', value {s.Value}).",
                    });
                }
            }
            catch (Exception ex)
            {
                failures.Add(new BatchFailure { Apiname = s.Apiname, Error = ex.Message });
            }
        }

        if ((cmd.Store ?? true) && !SteamUserStats.StoreStats())
        {
            failures.Add(new BatchFailure { Apiname = "", Error = "StoreStats returned false." });
        }

        WriteOk(failures);
    }

    private static async Task<bool> EnsureStatsLoaded()
    {
        // Cheap probe: if Steam has stats cached, GetStat works on any apiname
        // and just returns false for unknown ones. If stats aren't loaded yet,
        // RequestCurrentStats kicks off the load and we wait on the callback.
        var tcs = new TaskCompletionSource<EResult>();
        _statsTcs = tcs;
        if (!SteamUserStats.RequestCurrentStats()) { _statsTcs = null; return false; }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        if (done != tcs.Task) { _statsTcs = null; return false; }
        return (await tcs.Task) == EResult.k_EResultOK;
    }

    private static void OnStatsReceived(UserStatsReceived_t result)
    {
        if (result.m_nGameID != (ulong)_appId) return;
        var tcs = Interlocked.Exchange(ref _statsTcs, null);
        tcs?.TrySetResult(result.m_eResult);
    }

    private static string Sanitize(string s)
    {
        var buf = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_';
        }
        return new string(buf);
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void WriteOk(object? data = null)
    {
        var json = data is null
            ? "{\"ok\":true}"
            : JsonSerializer.Serialize(new { ok = true, data }, _jsonOpts);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    private static void WriteError(string msg)
    {
        var json = JsonSerializer.Serialize(new { ok = false, error = msg }, _jsonOpts);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    private class Command
    {
        [JsonPropertyName("cmd")]     public string? Cmd { get; set; }
        [JsonPropertyName("apiname")] public string? Apiname { get; set; }
        [JsonPropertyName("changes")] public Change[]? Changes { get; set; }
        [JsonPropertyName("stats")]   public StatChange[]? Stats { get; set; }
        [JsonPropertyName("store")]   public bool? Store { get; set; }
    }

    private class Change
    {
        [JsonPropertyName("apiname")]  public string Apiname { get; set; } = "";
        [JsonPropertyName("achieved")] public bool Achieved { get; set; }
    }

    private class StatChange
    {
        [JsonPropertyName("apiname")] public string Apiname { get; set; } = "";
        [JsonPropertyName("type")]    public string? Type { get; set; }
        [JsonPropertyName("value")]   public double Value { get; set; }
    }

    private class StatValueDto
    {
        [JsonPropertyName("apiname")] public string Apiname { get; set; } = "";
        [JsonPropertyName("type")]    public string Type { get; set; } = "";
        [JsonPropertyName("value")]   public double Value { get; set; }
        [JsonPropertyName("error")]   public string? Error { get; set; }
    }

    private class BatchFailure
    {
        [JsonPropertyName("apiname")] public string Apiname { get; set; } = "";
        [JsonPropertyName("error")]   public string Error { get; set; } = "";
    }

    private class AchievementDto
    {
        [JsonPropertyName("apiname")]    public string Apiname { get; set; } = "";
        [JsonPropertyName("name")]       public string Name { get; set; } = "";
        [JsonPropertyName("desc")]       public string Desc { get; set; } = "";
        [JsonPropertyName("achieved")]   public bool Achieved { get; set; }
        [JsonPropertyName("unlockTime")] public uint UnlockTime { get; set; }
        [JsonPropertyName("hidden")]     public bool Hidden { get; set; }
        [JsonPropertyName("iconPath")]   public string? IconPath { get; set; }
        [JsonPropertyName("iconW")]      public uint IconW { get; set; }
        [JsonPropertyName("iconH")]      public uint IconH { get; set; }
    }
}
