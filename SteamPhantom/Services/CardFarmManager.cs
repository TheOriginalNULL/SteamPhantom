using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Rotates a queue of games through a small pool of idle workers so each gets
/// playtime credit toward card drops, then moves on to the next batch.
///
/// Independent of <see cref="IdleManager"/>: worker processes here are
/// tracked separately. Safeguard: any appid already in IdleManager.Sessions
/// is marked Skipped so we never spawn a competing worker for it.
/// </summary>
public class CardFarmManager
{
    private static readonly TimeSpan CardCheckInterval = TimeSpan.FromMinutes(2);

    private readonly IdleManager _idleManager;
    private readonly CardDropChecker? _dropChecker;
    private readonly Func<string?>? _steamIdProvider;
    private readonly DispatcherTimer _tick;
    private readonly Random _rng = new();
    private Func<IEnumerable<OwnedGame>>? _libraryProvider;
    private DateTime _lastCardCheck = DateTime.MinValue;
    private bool _cardCheckInFlight;

    public ObservableCollection<CardFarmEntry> Entries { get; } = new();

    public int MaxConcurrent { get; set; } = 3;
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromMinutes(30);

    public bool IsRunning { get; private set; }
    public DateTime? StartedAt { get; private set; }

    public event Action? StateChanged;

    public CardFarmManager(
        IdleManager idleManager,
        CardDropChecker? dropChecker = null,
        Func<string?>? steamIdProvider = null)
    {
        _idleManager = idleManager;
        _dropChecker = dropChecker;
        _steamIdProvider = steamIdProvider;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// LibraryViewModel registers a getter for its Games collection so the
    /// farm can pick random eligible games without a hard dependency on the VM.
    /// </summary>
    public void RegisterLibraryProvider(Func<IEnumerable<OwnedGame>> provider)
        => _libraryProvider = provider;

    /// <summary>
    /// Picks up to <paramref name="targetCount"/> random games from the library
    /// that aren't already queued, active, finished, or running in the Idle tab,
    /// and appends them to the queue. Returns the count actually added.
    /// </summary>
    public int AutoFillQueue(int targetCount)
    {
        if (_libraryProvider is null) return 0;
        var library = _libraryProvider().ToList();
        if (library.Count == 0) return 0;

        var excluded = new HashSet<uint>();
        foreach (var s in _idleManager.Sessions) excluded.Add(s.AppId);   // safeguard
        foreach (var e in Entries) excluded.Add(e.AppId);                  // already queued/active/done

        var eligible = library.Where(g => !excluded.Contains(g.AppId)).ToList();
        if (eligible.Count == 0) return 0;

        // Fisher–Yates shuffle on the eligible list, take first N.
        for (var i = eligible.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
        }

        var added = 0;
        foreach (var g in eligible)
        {
            if (added >= targetCount) break;
            Entries.Add(new CardFarmEntry(g));
            added++;
        }
        if (added > 0) Raise();
        return added;
    }

    public void Enqueue(OwnedGame game)
    {
        if (Entries.Any(e => e.AppId == game.AppId)) return;
        Entries.Add(new CardFarmEntry(game));
        Raise();
    }

    public void Remove(CardFarmEntry entry)
    {
        if (entry.Status == CardFarmStatus.Active) StopWorker(entry);
        Entries.Remove(entry);
        Raise();
    }

    public void ClearDone()
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
            if (Entries[i].Status is CardFarmStatus.Done or CardFarmStatus.Skipped)
                Entries.RemoveAt(i);
        Raise();
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        if (Entries.All(e => e.Status != CardFarmStatus.Queued)) return;
        IsRunning = true;
        StartedAt ??= DateTime.UtcNow;
        if (!_tick.IsEnabled) _tick.Start();
        await RotateAsync().ConfigureAwait(true);
        Raise();
        _ = CheckCardDropsAsync(); // initial snapshot, fire-and-forget
    }

    public void Pause()
    {
        IsRunning = false;
        foreach (var e in Entries.Where(x => x.Status == CardFarmStatus.Active).ToList())
        {
            StopWorker(e);
            // Active → back to Queued so resume picks it up.
            e.Status = CardFarmStatus.Queued;
            e.StartedAt = null;
            e.Elapsed = TimeSpan.Zero;
        }
        Raise();
    }

    public void Stop()
    {
        IsRunning = false;
        StartedAt = null;
        foreach (var e in Entries.Where(x => x.Status == CardFarmStatus.Active).ToList())
            StopWorker(e);
        Entries.Clear();
        _tick.Stop();
        Raise();
    }

    public bool IsAppIdInRunningIdle(uint appId) =>
        _idleManager.Sessions.Any(s => s.AppId == appId);

    private async Task TickAsync()
    {
        var now = DateTime.UtcNow;
        var rotated = false;

        // Update elapsed; rotate expired or dead workers.
        foreach (var e in Entries.Where(x => x.Status == CardFarmStatus.Active).ToList())
        {
            if (e.Process is null || e.Process.HasExited)
            {
                e.Process = null;
                e.Status = CardFarmStatus.Done;
                e.StatusMessage = "Worker exited";
                rotated = true;
                continue;
            }
            e.Elapsed = now - (e.StartedAt ?? now);
            if (e.Elapsed >= RotationInterval)
            {
                StopWorker(e);
                e.Status = CardFarmStatus.Done;
                e.StatusMessage = "Rotation complete";
                rotated = true;
            }
        }

        // Top up the active set from the queue if we're running.
        if (IsRunning && (rotated || Entries.Count(x => x.Status == CardFarmStatus.Active) < MaxConcurrent))
            await RotateAsync().ConfigureAwait(true);

        // Periodic card-drop check (every 5 min).
        if (IsRunning && DateTime.UtcNow - _lastCardCheck >= CardCheckInterval)
            _ = CheckCardDropsAsync();

        // Auto-stop when nothing left to do.
        if (IsRunning
            && !Entries.Any(e => e.Status == CardFarmStatus.Queued)
            && !Entries.Any(e => e.Status == CardFarmStatus.Active))
        {
            IsRunning = false;
            _tick.Stop();
            Raise();
        }
    }

    public async Task RefreshCardDropsAsync() => await CheckCardDropsAsync().ConfigureAwait(true);

    private async Task CheckCardDropsAsync()
    {
        if (_dropChecker is null || _steamIdProvider is null) return;
        if (_cardCheckInFlight) return;
        var steamId = _steamIdProvider();
        if (string.IsNullOrWhiteSpace(steamId)) return;

        _cardCheckInFlight = true;
        try
        {
            // Snapshot to avoid mutation-during-iteration.
            var targets = Entries
                .Where(e => e.Status is CardFarmStatus.Active or CardFarmStatus.Queued or CardFarmStatus.Done)
                .ToList();

            foreach (var entry in targets)
            {
                var remaining = await _dropChecker.GetRemainingAsync(steamId, entry.AppId).ConfigureAwait(true);
                if (remaining is null) continue;

                if (entry.InitialCardsRemaining is null)
                    entry.InitialCardsRemaining = remaining;
                else
                    entry.CardsEarnedThisSession = Math.Max(0, entry.InitialCardsRemaining.Value - remaining.Value);

                entry.CardsRemaining = remaining;
            }
            _lastCardCheck = DateTime.UtcNow;
            Raise();
        }
        finally
        {
            _cardCheckInFlight = false;
        }
    }

    private async Task RotateAsync()
    {
        var activeCount = Entries.Count(x => x.Status == CardFarmStatus.Active);
        var slots = MaxConcurrent - activeCount;
        if (slots <= 0) return;

        var queued = Entries.Where(x => x.Status == CardFarmStatus.Queued).ToList();
        foreach (var entry in queued)
        {
            if (slots <= 0) break;

            // Safeguard: never duplicate an appid that's already in IdleManager.
            if (IsAppIdInRunningIdle(entry.AppId))
            {
                entry.Status = CardFarmStatus.Skipped;
                entry.StatusMessage = "Skipped — already running in Idle tab";
                continue;
            }

            try
            {
                entry.Process = await SpawnWorkerAsync(entry.AppId).ConfigureAwait(true);
                entry.StartedAt = DateTime.UtcNow;
                entry.Elapsed = TimeSpan.Zero;
                entry.Status = CardFarmStatus.Active;
                entry.StatusMessage = string.Empty;
                slots--;
            }
            catch (Exception ex)
            {
                entry.Status = CardFarmStatus.Skipped;
                entry.StatusMessage = ex.Message;
            }
        }
    }

    private static async Task<Process> SpawnWorkerAsync(uint appId)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SteamPhantom.Worker.exe");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException($"Worker exe not found at {workerPath}");

        var workDir = Path.Combine(Path.GetTempPath(), "SteamPhantom", "cards", appId.ToString());
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = workerPath,
            Arguments = appId.ToString(),
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        var readyTask = process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        if (winner != readyTask)
        {
            TryKill(process);
            throw new TimeoutException("Worker didn't initialize within 5 seconds.");
        }
        var first = (await readyTask.ConfigureAwait(true))?.Trim();
        if (first != "READY")
        {
            string err = string.Empty;
            try { err = (await process.StandardError.ReadToEndAsync().ConfigureAwait(true)).Trim(); }
            catch { }
            TryKill(process);
            throw new InvalidOperationException(
                string.IsNullOrEmpty(err) ? "Worker failed to start." : err);
        }
        return process;
    }

    private static void StopWorker(CardFarmEntry entry)
    {
        if (entry.Process is null) return;
        try
        {
            if (!entry.Process.HasExited)
            {
                try { entry.Process.StandardInput.Close(); } catch { }
                if (!entry.Process.WaitForExit(2000))
                    TryKill(entry.Process);
            }
        }
        catch { }
        entry.Process = null;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public void StopAll()
    {
        foreach (var e in Entries.Where(x => x.Status == CardFarmStatus.Active).ToList())
            StopWorker(e);
        _tick.Stop();
        IsRunning = false;
    }

    private void Raise() => StateChanged?.Invoke();
}
