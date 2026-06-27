using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Catalog orchestrator. Prefers the signed-in SteamKit2 session (no Web
/// API key needed) and falls back to the Web API only when a key is
/// configured and SteamKit isn't usable.
/// </summary>
public class SteamGameCatalog
{
    private readonly SteamKitClient _kit;
    private readonly SteamWebApiClient _webApi;

    public SteamGameCatalog(SteamKitClient kit, SteamWebApiClient webApi)
    {
        _kit = kit;
        _webApi = webApi;
    }

    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(
        string? steamId64, string? apiKey, CancellationToken ct = default)
    {
        // Primary path: signed-in SteamKit2 session.
        if (_kit.IsLoggedOn)
        {
            try
            {
                return await _kit.GetOwnedGamesAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // If SteamKit hiccupped mid-session, try the key path before giving up.
            }
        }

        // Fallback: Web API key (requires both key and Steam64).
        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(steamId64))
        {
            return await _webApi.GetOwnedGamesAsync(apiKey, steamId64, ct).ConfigureAwait(false);
        }

        throw new SteamWebApiException(SteamWebApiFailure.MissingCredentials,
            "Sign in to Steam, or set a Steam Web API key in Settings.");
    }
}
