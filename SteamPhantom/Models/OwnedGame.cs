using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPhantom.Models;

public partial class OwnedGame : ObservableObject
{
    public uint AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int PlaytimeMinutes { get; set; }
    public string IconHash { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public string HeaderImageUrl =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

    public string IconImageUrl => string.IsNullOrEmpty(IconHash)
        ? string.Empty
        : $"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg";

    public string PlaytimeDisplay
    {
        get
        {
            if (PlaytimeMinutes <= 0) return "Never played";
            var hours = PlaytimeMinutes / 60.0;
            return hours < 1
                ? $"{PlaytimeMinutes} min"
                : $"{hours:0.#} hrs";
        }
    }
}
