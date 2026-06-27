using Microsoft.Win32;

namespace SteamPhantom.Services;

/// <summary>
/// Registers / unregisters the app in the per-user
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run key so Windows launches
/// it (minimized to tray) at login.
/// </summary>
public static class WindowsStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "SteamPhantom";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static void Apply(bool register)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (register)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;
                key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort — no admin rights needed for HKCU */ }
    }
}
