using Microsoft.Win32;

namespace SteamPhantom.Services;

public static class SteamIdResolver
{
    // Steam64 = AccountID + 76561197960265728 (Steam's "individual" base).
    private const long SteamId64IndividualBase = 76561197960265728L;

    /// <summary>
    /// Returns the Steam64 ID of the currently signed-in Steam user, or null
    /// if Steam is not running (ActiveUser is 0) or the registry key is missing.
    /// </summary>
    public static string? TryDetectActiveSteamId64()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is not int accountId || accountId == 0)
                return null;
            return ((long)(uint)accountId + SteamId64IndividualBase).ToString();
        }
        catch
        {
            return null;
        }
    }
}
