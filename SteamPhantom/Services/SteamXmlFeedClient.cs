using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Reads the user's owned games from the Steam community page (no API key).
/// Tries XML first; falls back to scraping the rgGames JSON embedded in the
/// regular HTML page because Steam now often ignores ?xml=1.
/// </summary>
public class SteamXmlFeedClient
{
    private readonly HttpClient _http;

    public SteamXmlFeedClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(
        string steamId64, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(steamId64))
        {
            throw new SteamWebApiException(SteamWebApiFailure.MissingCredentials,
                "Steam64 ID is required.");
        }

        var url = $"https://steamcommunity.com/profiles/{Uri.EscapeDataString(steamId64)}/games/?tab=all&xml=1";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Network,
                "Couldn't reach Steam community.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Unknown,
                $"Steam community returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Path A: proper XML response (older accounts / edge cases).
        var head = body.Substring(0, Math.Min(body.Length, 600));
        if (head.IndexOf("<gamesList", StringComparison.OrdinalIgnoreCase) >= 0
            || head.IndexOf("<response", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return ParseXml(body, steamId64);
        }

        // Path B: HTML page with rgGames JSON embedded.
        var rgGamesJson = ExtractRgGames(body);
        if (rgGamesJson is not null)
        {
            return ParseRgGames(rgGamesJson);
        }

        // Path C: last-ditch DOM scrape. Public games tab usually has
        // <div ... id="game_<appid>"> rows even when rgGames is absent.
        var domGames = ScrapeGameRows(body);
        if (domGames.Count > 0) return domGames;

        // Steam isn't giving us a parseable game list. The Web API is reliable.
        throw new SteamWebApiException(SteamWebApiFailure.PrivateProfile,
            "Steam served the profile page but without a game list we can parse. " +
            "This typically means Steam requires authentication for catalog reads now. " +
            "Add a Steam Web API key in Settings (steamcommunity.com/dev/apikey — " +
            "fill in any domain like 'localhost', takes 30 seconds) and refresh.");
    }

    // ---- XML path ----

    private static IReadOnlyList<OwnedGame> ParseXml(string body, string steamId64)
    {
        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (Exception ex)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Unknown,
                "Couldn't parse Steam community XML.", ex);
        }

        if (doc.Root?.Name.LocalName == "response")
        {
            var error = doc.Root.Element("error")?.Value ?? "Unknown error.";
            var failure = error.Contains("private", StringComparison.OrdinalIgnoreCase)
                ? SteamWebApiFailure.PrivateProfile
                : SteamWebApiFailure.ProfileNotFound;
            throw new SteamWebApiException(failure, error);
        }

        var games = (doc.Root?.Element("games")?.Elements("game") ?? Enumerable.Empty<XElement>())
            .Select(el =>
            {
                var appId = uint.TryParse(el.Element("appID")?.Value, out var id) ? id : 0u;
                var hoursRaw = el.Element("hoursOnRecord")?.Value?.Replace(",", "") ?? string.Empty;
                var hours = decimal.TryParse(hoursRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) ? h : 0m;
                return new OwnedGame
                {
                    AppId = appId,
                    Name = el.Element("name")?.Value?.Trim() ?? $"App {appId}",
                    PlaytimeMinutes = (int)(hours * 60m),
                    IconHash = string.Empty
                };
            })
            .Where(g => g.AppId != 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (games.Count == 0)
        {
            throw new SteamWebApiException(SteamWebApiFailure.PrivateProfile,
                $"Steam returned an empty game list for {steamId64}. Profile or game details may be private.");
        }
        return games;
    }

    // ---- HTML/rgGames path ----

    /// <summary>
    /// Extracts the JSON array that follows "var rgGames = " in the HTML page.
    /// Walks brackets so embedded strings and objects don't fool a naive regex.
    /// </summary>
    private static string? ExtractRgGames(string html)
    {
        const string marker = "var rgGames = ";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + marker.Length;
        if (start >= html.Length || html[start] != '[') return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return html.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static IReadOnlyList<OwnedGame> ParseRgGames(string json)
    {
        List<RgGame>? rg;
        try
        {
            rg = JsonSerializer.Deserialize<List<RgGame>>(json);
        }
        catch (JsonException ex)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Unknown,
                "Couldn't parse rgGames JSON from Steam community page.", ex);
        }

        if (rg is null || rg.Count == 0)
        {
            throw new SteamWebApiException(SteamWebApiFailure.PrivateProfile,
                "The profile page loaded but contained no games.");
        }

        return rg
            .Select(g => new OwnedGame
            {
                AppId = g.AppId,
                Name = (g.Name ?? $"App {g.AppId}").Trim(),
                PlaytimeMinutes = ParseHoursToMinutes(g.HoursForever),
                IconHash = string.Empty
            })
            .Where(g => g.AppId != 0)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ParseHoursToMinutes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var cleaned = raw.Replace(",", "");
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var h)
            ? (int)(h * 60m)
            : 0;
    }

    private class RgGame
    {
        [JsonPropertyName("appid")]         public uint AppId { get; set; }
        [JsonPropertyName("name")]          public string? Name { get; set; }
        [JsonPropertyName("hours_forever")] public string? HoursForever { get; set; }
    }

    // ---- DOM scrape path ----

    private static readonly System.Text.RegularExpressions.Regex GameRowRegex =
        new(@"id=""game_(?<appid>\d+)""[^>]*>(?<row>.*?)</div>\s*</div>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly System.Text.RegularExpressions.Regex GameNameRegex =
        new(@"<h5[^>]*>\s*(?:<a[^>]*>)?(?<name>[^<]+?)(?:</a>)?\s*</h5>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    private static IReadOnlyList<OwnedGame> ScrapeGameRows(string html)
    {
        var matches = GameRowRegex.Matches(html);
        if (matches.Count == 0) return Array.Empty<OwnedGame>();

        var seen = new HashSet<uint>();
        var games = new List<OwnedGame>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (!uint.TryParse(m.Groups["appid"].Value, out var appid) || !seen.Add(appid))
                continue;
            var row = m.Groups["row"].Value;
            var nameMatch = GameNameRegex.Match(row);
            var name = nameMatch.Success
                ? System.Net.WebUtility.HtmlDecode(nameMatch.Groups["name"].Value.Trim())
                : $"App {appid}";
            games.Add(new OwnedGame { AppId = appid, Name = name });
        }
        return games
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
