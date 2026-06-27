namespace SteamPhantom.Models;

public enum AppTheme
{
    Dark,
    Light
}

public class AppSettings
{
    public string SteamWebApiKey { get; set; } = string.Empty;
    public string SteamId64 { get; set; } = string.Empty;
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>App ids of games that were idling when the app last exited.
    /// Re-launched automatically once the Library finishes loading.</summary>
    public List<uint> IdleAppIds { get; set; } = new();

    /// <summary>App ids that were in the card-farm queue at last exit.</summary>
    public List<uint> CardFarmAppIds { get; set; } = new();

    /// <summary>Stop any idle session that exceeds this many hours. 0 = disabled.</summary>
    public int IdleAutoStopHours { get; set; } = 0;

    // --- Window memory ---
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 680;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    // --- Startup behavior ---
    /// <summary>Launch straight to the tray (no window shown) on every start.</summary>
    public bool LaunchMinimized { get; set; }

    /// <summary>Register a Windows Run key so the app autostarts at login.</summary>
    public bool RunAtWindowsStartup { get; set; }
}
