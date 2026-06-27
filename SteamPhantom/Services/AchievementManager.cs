using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Spawns a worker per editing session and drives it over the JSON command
/// protocol (idle/list-achievements/set/clear/store/quit). One handle per
/// open achievements window; dispose closes the worker.
/// </summary>
public class AchievementManager
{
    public async Task<AchievementsHandle> OpenAsync(OwnedGame game)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SteamPhantom.Worker.exe");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException($"Worker exe not found at {workerPath}");

        var workDir = Path.Combine(Path.GetTempPath(), "SteamPhantom", "ach", game.AppId.ToString());
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = game.AppId.ToString(),
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        try
        {
            var readyTask = process.StandardOutput.ReadLineAsync();
            var won = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (won != readyTask)
            {
                TryKill(process);
                throw new TimeoutException("Worker didn't initialize within 5s. Is Steam running?");
            }
            var first = (await readyTask.ConfigureAwait(false))?.Trim();
            if (first != "READY")
            {
                var err = (await process.StandardError.ReadToEndAsync().ConfigureAwait(false)).Trim();
                TryKill(process);
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(err) ? "Worker failed to start." : $"Worker failed to start: {err}");
            }
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new AchievementsHandle(process);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

public sealed class AchievementsHandle : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    internal AchievementsHandle(Process process)
    {
        _process = process;
    }

    public async Task<IReadOnlyList<AchievementInfo>> ListAsync(CancellationToken ct = default)
    {
        var response = await SendAsync("""{"cmd":"list-achievements"}""", ct).ConfigureAwait(false);
        if (response.Data is not JsonElement arr || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<AchievementInfo>();

        var result = new List<AchievementInfo>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            var dto = el.Deserialize<AchievementDto>();
            if (dto is null) continue;
            result.Add(new AchievementInfo(
                apiname: dto.Apiname,
                name: dto.Name,
                description: dto.Desc,
                achieved: dto.Achieved,
                unlockTimeUnix: dto.UnlockTime,
                hidden: dto.Hidden,
                iconPath: dto.IconPath,
                iconW: dto.IconW,
                iconH: dto.IconH));
        }
        return result;
    }

    public async Task SetAsync(string apiname, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { cmd = "set", apiname });
        await SendAsync(json, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(string apiname, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { cmd = "clear", apiname });
        await SendAsync(json, ct).ConfigureAwait(false);
    }

    public async Task StoreAsync(CancellationToken ct = default)
    {
        await SendAsync("""{"cmd":"store"}""", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StatRead>> GetStatsAsync(
        IEnumerable<string> apinames, CancellationToken ct = default)
    {
        var payload = new
        {
            cmd = "get-stats",
            stats = apinames.Select(a => new { apiname = a }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        var response = await SendAsync(json, ct).ConfigureAwait(false);

        var result = new List<StatRead>();
        if (response.Data is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var apiname = el.TryGetProperty("apiname", out var ap) ? ap.GetString() ?? "" : "";
                var type    = el.TryGetProperty("type", out var t)    ? t.GetString() ?? "" : "";
                double value = 0;
                if (el.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) value = v.GetDouble();
                var error = el.TryGetProperty("error", out var er) ? er.GetString() : null;
                result.Add(new StatRead(apiname, type, value, error));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<(string Apiname, string Error)>> ApplyStatsBatchAsync(
        IEnumerable<(string Apiname, string Type, double Value)> stats,
        bool store = true,
        CancellationToken ct = default)
    {
        var payload = new
        {
            cmd = "apply-stats-batch",
            store,
            stats = stats.Select(s => new { apiname = s.Apiname, type = s.Type, value = s.Value }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        var response = await SendAsync(json, ct).ConfigureAwait(false);

        var failures = new List<(string, string)>();
        if (response.Data is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var apiname = el.TryGetProperty("apiname", out var ap) ? ap.GetString() ?? "" : "";
                var error   = el.TryGetProperty("error",   out var er) ? er.GetString() ?? "" : "";
                failures.Add((apiname, error));
            }
        }
        return failures;
    }

    public async Task<IReadOnlyList<(string Apiname, string Error)>> ApplyBatchAsync(
        IEnumerable<(string Apiname, bool Achieved)> changes,
        bool store = true,
        CancellationToken ct = default)
    {
        var payload = new
        {
            cmd = "apply-batch",
            store,
            changes = changes.Select(c => new { apiname = c.Apiname, achieved = c.Achieved }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        var response = await SendAsync(json, ct).ConfigureAwait(false);

        var failures = new List<(string, string)>();
        if (response.Data is JsonElement arr && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var apiname = el.TryGetProperty("apiname", out var ap) ? ap.GetString() ?? "" : "";
                var error   = el.TryGetProperty("error",   out var er) ? er.GetString() ?? "" : "";
                failures.Add((apiname, error));
            }
        }
        return failures;
    }

    private async Task<WorkerResponse> SendAsync(string command, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AchievementsHandle));
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            var line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException("Worker closed stdout unexpectedly.");

            WorkerResponse? response;
            try { response = JsonSerializer.Deserialize<WorkerResponse>(line); }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Couldn't parse worker response: {line}", ex);
            }

            if (response is null) throw new InvalidOperationException("Empty worker response.");
            if (!response.Ok) throw new InvalidOperationException(response.Error ?? "Worker reported an error.");
            return response;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Best-effort graceful quit
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("""{"cmd":"quit"}""").ConfigureAwait(false); } catch { }
                try { await _process.StandardInput.FlushAsync().ConfigureAwait(false); } catch { }
                try { _process.StandardInput.Close(); } catch { }
                if (!_process.WaitForExit(1500))
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch { }
        _ioLock.Dispose();
    }

    private class WorkerResponse
    {
        [JsonPropertyName("ok")]    public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("data")]  public JsonElement? Data { get; set; }
    }

    public record StatRead(string Apiname, string Type, double Value, string? Error);

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
