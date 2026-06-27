using System.Diagnostics;

namespace SteamPhantom;

/// <summary>
/// Single source of truth for external links shown in the UI.
/// Update <see cref="DiscordInviteUrl"/> once with your real invite.
/// </summary>
public static class AppLinks
{
    public const string DiscordInviteUrl = "https://discord.gg/3x3";
    public const string AuthorTagline   = "brought to you by ~NULL · discord.gg/3x3";

    // Replace with wherever you host the update manifest JSON. Format:
    // { "version": "0.2.0", "downloadUrl": "https://.../SteamPhantom.exe", "releaseNotes": "..." }
    // GitHub Releases / raw.githubusercontent.com / any HTTPS static host works.
    public const string UpdateManifestUrl = "https://raw.githubusercontent.com/TheOriginalNULL/SteamPhantom/refs/heads/main/manifest.json";

    public const string SteamApiKeyUrl = "https://steamcommunity.com/dev/apikey";

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenDiscord() => Open(DiscordInviteUrl);
}
