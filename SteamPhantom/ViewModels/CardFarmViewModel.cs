using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Models;
using SteamPhantom.Services;

namespace SteamPhantom.ViewModels;

public partial class CardFarmViewModel : ObservableObject
{
    public CardFarmManager Manager { get; }

    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _queuedCount;
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _totalEarnedThisSession;
    [ObservableProperty] private int _totalDropsRemaining;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _maxConcurrentText;
    [ObservableProperty] private string _rotationMinutesText;
    [ObservableProperty] private string _queueSizeText = "30";
    [ObservableProperty] private string _autoFillStatus = string.Empty;

    public CardFarmViewModel(CardFarmManager manager)
    {
        Manager = manager;
        _maxConcurrentText = manager.MaxConcurrent.ToString();
        _rotationMinutesText = manager.RotationInterval.TotalMinutes.ToString("0");
        manager.Entries.CollectionChanged += (_, _) => Recount();
        manager.StateChanged += Recount;
        Recount();
    }

    [RelayCommand]
    private async Task AutoFarmAsync()
    {
        ApplyTunables();
        var size = ParseIntOr(QueueSizeText, 30);
        var added = Manager.AutoFillQueue(size);
        if (added == 0)
        {
            AutoFillStatus = "No eligible games — load your library first, " +
                             "or every owned game is already in the queue / idling.";
            return;
        }
        AutoFillStatus = $"Queued {added} random games for card farming.";
        await Manager.StartAsync();
    }

    [RelayCommand]
    private void RefillQueue()
    {
        var size = ParseIntOr(QueueSizeText, 30);
        var added = Manager.AutoFillQueue(size);
        AutoFillStatus = added == 0
            ? "Nothing left to queue — all eligible games are already in the farm or idling."
            : $"Queued {added} more games.";
    }

    private void ApplyTunables()
    {
        if (int.TryParse(MaxConcurrentText, out var m) && m > 0) Manager.MaxConcurrent = Math.Min(m, 32);
        if (int.TryParse(RotationMinutesText, out var r) && r > 0)
            Manager.RotationInterval = TimeSpan.FromMinutes(r);
    }

    private static int ParseIntOr(string s, int fallback)
        => int.TryParse(s, out var v) && v > 0 ? v : fallback;

    private void Recount()
    {
        ActiveCount  = Manager.Entries.Count(e => e.Status == CardFarmStatus.Active);
        QueuedCount  = Manager.Entries.Count(e => e.Status == CardFarmStatus.Queued);
        DoneCount    = Manager.Entries.Count(e => e.Status == CardFarmStatus.Done);
        SkippedCount = Manager.Entries.Count(e => e.Status == CardFarmStatus.Skipped);
        IsRunning    = Manager.IsRunning;
        CanStart     = !Manager.IsRunning && QueuedCount > 0;
        CanPause     = Manager.IsRunning;
        CanStop      = Manager.Entries.Count > 0;
        TotalEarnedThisSession = Manager.Entries.Sum(e => e.CardsEarnedThisSession);
        TotalDropsRemaining    = Manager.Entries
            .Where(e => e.Status is CardFarmStatus.Active or CardFarmStatus.Queued)
            .Sum(e => e.CardsRemaining ?? 0);
    }

    [RelayCommand]
    private async Task RefreshCardDropsAsync() => await Manager.RefreshCardDropsAsync();

    [RelayCommand]
    private async Task StartAsync()
    {
        ApplyTunables();
        await Manager.StartAsync();
    }

    [RelayCommand]
    private void Pause() => Manager.Pause();

    [RelayCommand]
    private void Stop() => Manager.Stop();

    [RelayCommand]
    private void RemoveEntry(CardFarmEntry? entry)
    {
        if (entry is not null) Manager.Remove(entry);
    }

    [RelayCommand]
    private void ClearDone() => Manager.ClearDone();
}
