using System.Globalization;
using System.IO;
using Microsoft.Win32;
using ValveKeyValue;

namespace SteamPhantom.Services;

public record StatSchemaEntry(string Apiname, string DisplayName, double? DefaultValue);

/// <summary>
/// Reads Steam's locally cached achievement / stats schema files at
/// &lt;Steam&gt;/appcache/stats/UserGameStatsSchema_&lt;appid&gt;.bin. Steam writes
/// these once a game has been launched and stats have been requested at least
/// once, so anything in the user's recently-played list will be present.
/// Same source SAM uses.
/// </summary>
public static class SteamSchemaReader
{
    public static IReadOnlyList<StatSchemaEntry> ReadStats(uint appId)
    {
        var schemaPath = TryFindSchemaPath(appId);
        if (schemaPath is null) return Array.Empty<StatSchemaEntry>();

        try
        {
            var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Binary);
            using var stream = File.OpenRead(schemaPath);
            var root = serializer.Deserialize(stream);

            // The root KVObject's name is "<appid>" and its children include "stats".
            var statsNode = root.Children?.FirstOrDefault(c =>
                string.Equals(c.Name, "stats", StringComparison.OrdinalIgnoreCase));
            if (statsNode is null) return Array.Empty<StatSchemaEntry>();

            var result = new List<StatSchemaEntry>();
            foreach (var entry in statsNode.Children ?? Array.Empty<KVObject>())
            {
                // Each numbered child has "name", optional "default", optional "display.name".
                var apiname = GetStringChild(entry, "name");
                if (string.IsNullOrEmpty(apiname)) continue;

                var displayName = GetStringChild(entry, "display", "name") ?? apiname;
                var defaultStr = GetStringChild(entry, "default");
                double? defaultValue = null;
                if (!string.IsNullOrEmpty(defaultStr) &&
                    double.TryParse(defaultStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    defaultValue = d;
                }

                result.Add(new StatSchemaEntry(apiname, displayName, defaultValue));
            }

            return result
                .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<StatSchemaEntry>();
        }
    }

    public static string? TryFindSchemaPath(uint appId)
    {
        var steamPath = GetSteamPath();
        if (steamPath is null) return null;
        var path = Path.Combine(steamPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
        return File.Exists(path) ? path : null;
    }

    private static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringChild(KVObject parent, params string[] path)
    {
        var current = parent;
        foreach (var segment in path)
        {
            current = current.Children?.FirstOrDefault(c =>
                string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (current is null) return null;
        }
        return current.Value?.ToString();
    }
}
