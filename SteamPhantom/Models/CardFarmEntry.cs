using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPhantom.Models;

public enum CardFarmStatus
{
    Queued,
    Active,
    Done,
    Skipped
}

public partial class CardFarmEntry : ObservableObject
{
    public uint AppId { get; }
    public string Name { get; }
    public string HeaderImageUrl { get; }

    [ObservableProperty] private CardFarmStatus _status = CardFarmStatus.Queued;
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private TimeSpan _elapsed;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int? _cardsRemaining;
    [ObservableProperty] private int _cardsEarnedThisSession;

    /// <summary>The "card drops remaining" value the first time we checked it.</summary>
    public int? InitialCardsRemaining { get; set; }

    public Process? Process { get; set; }

    public string ElapsedDisplay =>
        $"{(int)Elapsed.TotalHours:D2}:{Elapsed.Minutes:D2}:{Elapsed.Seconds:D2}";

    public string CardsDisplay
    {
        get
        {
            if (CardsRemaining is null) return "Drops: —";
            if (CardsRemaining == 0)     return "✓ All drops earned";
            var s = CardsRemaining == 1 ? "" : "s";
            return CardsEarnedThisSession > 0
                ? $"{CardsRemaining} drop{s} left · +{CardsEarnedThisSession} this session"
                : $"{CardsRemaining} drop{s} left";
        }
    }

    public CardFarmEntry(OwnedGame game)
    {
        AppId = game.AppId;
        Name = game.Name;
        HeaderImageUrl = game.HeaderImageUrl;
    }

    partial void OnElapsedChanged(TimeSpan value) => OnPropertyChanged(nameof(ElapsedDisplay));
    partial void OnCardsRemainingChanged(int? value) => OnPropertyChanged(nameof(CardsDisplay));
    partial void OnCardsEarnedThisSessionChanged(int value) => OnPropertyChanged(nameof(CardsDisplay));
}
