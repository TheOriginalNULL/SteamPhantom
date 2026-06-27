using System.Net.Http;
using System.Text.RegularExpressions;

namespace SteamPhantom.Services;

/// <summary>
/// Scrapes Steam's public game-cards page for "N card drops remaining".
/// Public-profile only — same constraint as the gamecards page itself.
/// </summary>
public class CardDropChecker
{
    private static readonly Regex DropsRegex = new(
        @"(\d+)\s+card\s+drops?\s+remaining",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public CardDropChecker(HttpClient http) => _http = http;

    /// <summary>
    /// Returns null when we can't tell (private profile, request failed, or
    /// the game has no card set). Returns 0 when all drops have already been
    /// earned.
    /// </summary>
    public async Task<int?> GetRemainingAsync(string steamId64, uint appId, CancellationToken ct = default)
    {
        var url = $"https://steamcommunity.com/profiles/{steamId64}/gamecards/{appId}/";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (html.Contains("No card drops remaining", StringComparison.OrdinalIgnoreCase))
                return 0;

            var m = DropsRegex.Match(html);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
        }
        catch
        {
            return null;
        }
    }
}
