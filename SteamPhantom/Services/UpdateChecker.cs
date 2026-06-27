using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamPhantom.Services;

public record UpdateInfo(string Version, string DownloadUrl, string? ReleaseNotes);

/// <summary>
/// Pulls a small JSON manifest from a remote URL and compares its version
/// to the running assembly's. Designed to fail silently — anything wrong
/// (offline, malformed JSON, missing file) just returns null and the rest
/// of the app keeps running.
/// </summary>
public class UpdateChecker
{
    private readonly HttpClient _http;

    public UpdateChecker(HttpClient http) => _http = http;

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;

        try
        {
            using var resp = await _http.GetAsync(manifestUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<Manifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version)) return null;

            if (!TryParseVersion(CurrentVersion, out var current)) return null;
            if (!TryParseVersion(manifest.Version, out var latest)) return null;

            return latest > current
                ? new UpdateInfo(manifest.Version, manifest.DownloadUrl ?? "", manifest.ReleaseNotes)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseVersion(string s, out Version v)
    {
        // Accept "0.2", "0.2.0", or "0.2.0.0".
        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4) { v = new Version(0, 0); return false; }
        try { v = new Version(s.Count(c => c == '.') < 3 ? $"{s}.0" : s); return true; }
        catch { v = new Version(0, 0); return false; }
    }

    private class Manifest
    {
        [JsonPropertyName("version")]      public string  Version      { get; set; } = "";
        [JsonPropertyName("downloadUrl")]  public string? DownloadUrl  { get; set; }
        [JsonPropertyName("releaseNotes")] public string? ReleaseNotes { get; set; }
    }
}
