using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

public class SteamWebApiClient
{
    private const string GetOwnedGamesUrl =
        "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
        "?key={0}&steamid={1}&include_appinfo=1&include_played_free_games=1&format=json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;

    public SteamWebApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<OwnedGame>> GetOwnedGamesAsync(
        string apiKey, string steamId64, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
        {
            throw new SteamWebApiException(SteamWebApiFailure.MissingCredentials,
                "Steam Web API key and Steam64 ID are required. Set them in Settings.");
        }

        var url = string.Format(GetOwnedGamesUrl, Uri.EscapeDataString(apiKey),
                                                  Uri.EscapeDataString(steamId64));

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Network,
                "Couldn't reach Steam. Check your internet connection.", ex);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SteamWebApiException(SteamWebApiFailure.InvalidApiKey,
                "Steam rejected the Web API key. Double-check it in Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Unknown,
                $"Steam returned HTTP {(int)response.StatusCode}.");
        }

        GetOwnedGamesEnvelope? envelope;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            envelope = await JsonSerializer.DeserializeAsync<GetOwnedGamesEnvelope>(
                stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SteamWebApiException(SteamWebApiFailure.Unknown,
                "Couldn't parse Steam's response.", ex);
        }

        var inner = envelope?.Response;

        // Steam returns { "response": {} } for non-existent IDs and for private
        // profiles. Distinguish by game_count: missing key = bad ID, 0 = private.
        if (inner is null || (inner.GameCount is null && (inner.Games is null || inner.Games.Count == 0)))
        {
            throw new SteamWebApiException(SteamWebApiFailure.ProfileNotFound,
                "No profile found for that Steam64 ID.");
        }

        if (inner.Games is null || inner.Games.Count == 0)
        {
            throw new SteamWebApiException(SteamWebApiFailure.PrivateProfile,
                "This profile's game details are private. " +
                "Set Game Details to Public in Steam privacy settings.");
        }

        return inner.Games
            .Select(g => new OwnedGame
            {
                AppId = g.AppId,
                Name = g.Name ?? $"App {g.AppId}",
                PlaytimeMinutes = g.PlaytimeForever,
                IconHash = g.ImgIconUrl ?? string.Empty
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- DTOs ----

    private class GetOwnedGamesEnvelope
    {
        [JsonPropertyName("response")] public GetOwnedGamesResponse? Response { get; set; }
    }

    private class GetOwnedGamesResponse
    {
        [JsonPropertyName("game_count")] public int? GameCount { get; set; }
        [JsonPropertyName("games")]      public List<OwnedGameDto>? Games { get; set; }
    }

    private class OwnedGameDto
    {
        [JsonPropertyName("appid")]            public uint AppId { get; set; }
        [JsonPropertyName("name")]             public string? Name { get; set; }
        [JsonPropertyName("playtime_forever")] public int PlaytimeForever { get; set; }
        [JsonPropertyName("img_icon_url")]     public string? ImgIconUrl { get; set; }
    }
}
