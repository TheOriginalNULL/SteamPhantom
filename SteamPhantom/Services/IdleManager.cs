using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

public class IdleManager
{
    public const int MaxConcurrent = 32;

    /// <summary>Stop any session whose elapsed time exceeds this many hours.
    /// Zero (default) disables auto-stop.</summary>
    public int AutoStopHours { get; set; }

    private readonly DispatcherTimer _timer;

    public ObservableCollection<IdleSession> Sessions { get; } = new();

    public IdleManager()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public async Task<IdleSession> StartAsync(OwnedGame game)
    {
        if (Sessions.Any(s => s.AppId == game.AppId))
            throw new InvalidOperationException($"Already idling {game.Name}.");
        if (Sessions.Count >= MaxConcurrent)
            throw new InvalidOperationException(
                $"Steam allows up to {MaxConcurrent} concurrent in-game apps; stop one before starting another.");

        var workerPath = Path.Combine(AppContext.BaseDirectory, "SteamPhantom.Worker.exe");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException($"Worker exe not found at {workerPath}");

        // steam_appid.txt is per-cwd, so each session gets a private dir.
        var workDir = Path.Combine(Path.GetTempPath(), "SteamPhantom", "workers", game.AppId.ToString());
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
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");

        // Wait up to 5s for the worker to print READY (= SteamAPI_Init succeeded).
        var readyTask = process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        if (winner != readyTask)
        {
            TryKill(process);
            throw new TimeoutException("Worker didn't initialize within 5 seconds. Is Steam running?");
        }

        var first = (await readyTask.ConfigureAwait(true))?.Trim();
        if (first != "READY")
        {
            string err = string.Empty;
            try { err = (await process.StandardError.ReadToEndAsync().ConfigureAwait(true)).Trim(); }
            catch { }
            TryKill(process);
            throw new InvalidOperationException(
                string.IsNullOrEmpty(err)
                    ? "Worker failed to start."
                    : $"Worker failed to start: {err}");
        }

        var session = new IdleSession(game, process);
        Sessions.Add(session);
        if (!_timer.IsEnabled) _timer.Start();
        return session;
    }

    public void Stop(IdleSession session)
    {
        try
        {
            if (!session.Process.HasExited)
            {
                try { session.Process.StandardInput.Close(); } catch { }
                if (!session.Process.WaitForExit(2000))
                    TryKill(session.Process);
            }
        }
        catch { /* shutting down, best-effort */ }
        Sessions.Remove(session);
        if (Sessions.Count == 0) _timer.Stop();
    }

    public void StopAll()
    {
        foreach (var s in Sessions.ToList()) Stop(s);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var limit = AutoStopHours > 0 ? TimeSpan.FromHours(AutoStopHours) : TimeSpan.Zero;

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            var s = Sessions[i];
            if (s.Process.HasExited)
            {
                Sessions.RemoveAt(i);
                continue;
            }
            s.Elapsed = now - s.StartedAt;

            if (limit > TimeSpan.Zero && s.Elapsed >= limit)
            {
                Stop(s);
            }
        }
        if (Sessions.Count == 0) _timer.Stop();
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
