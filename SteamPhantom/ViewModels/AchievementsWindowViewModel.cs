using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Models;
using SteamPhantom.Services;

namespace SteamPhantom.ViewModels;

public enum AchievementsState
{
    Loading,
    Error,
    Empty,
    Loaded,
    Saving
}

public enum AchievementSortMode
{
    Default,
    NameAZ,
    UnlockDateRecent,
    UnlockedFirst,
    LockedFirst,
    DirtyFirst
}

public enum AchievementStatusFilter
{
    All,
    Unlocked,
    Locked,
    Hidden,
    Dirty
}

public record SortOption(AchievementSortMode Mode, string Label);

public partial class AchievementsWindowViewModel : ObservableObject
{
    private readonly AchievementManager _manager;
    private readonly OwnedGame _game;
    private AchievementsHandle? _handle;
    private static readonly Random Rng = new();

    public string GameName => _game.Name;
    public string HeaderImageUrl => _game.HeaderImageUrl;

    public ObservableCollection<AchievementInfo> Achievements { get; } = new();
    public ICollectionView AchievementsView { get; }
    public ObservableCollection<StatInfo> Stats { get; } = new();
    public ICollectionView StatsView { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private AchievementsState _state = AchievementsState.Loading;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private AchievementsState _statsState = AchievementsState.Loading;
    [ObservableProperty] private string _statsErrorMessage = string.Empty;
    [ObservableProperty] private string _activeTab = "Achievements";
    [ObservableProperty] private bool _dripFeed; // SAM-fast batch path by default
    [ObservableProperty] private string _saveStatus = string.Empty;
    [ObservableProperty] private string _statusBanner = string.Empty;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private AchievementStatusFilter _statusFilter = AchievementStatusFilter.All;
    [ObservableProperty] private SortOption _selectedSort;

    public IReadOnlyList<SortOption> SortOptions { get; } = new[]
    {
        new SortOption(AchievementSortMode.Default,         "Default order"),
        new SortOption(AchievementSortMode.NameAZ,          "Name (A–Z)"),
        new SortOption(AchievementSortMode.UnlockDateRecent,"Recently unlocked"),
        new SortOption(AchievementSortMode.UnlockedFirst,   "Unlocked first"),
        new SortOption(AchievementSortMode.LockedFirst,     "Locked first"),
        new SortOption(AchievementSortMode.DirtyFirst,      "Pending changes first"),
    };

    public int TotalCount    => Achievements.Count;
    public int UnlockedCount => Achievements.Count(a => a.Achieved);
    public int DirtyCount    => Achievements.Count(a => a.IsDirty);

    public int StatCount         => Stats.Count;
    public int DirtyStatCount    => Stats.Count(s => s.IsDirty);
    public bool HasDirtyChanges  => IsOnStatsTab ? DirtyStatCount > 0 : DirtyCount > 0;
    public bool IsOnStatsTab     => string.Equals(ActiveTab, "Stats", StringComparison.Ordinal);

    public AchievementsWindowViewModel(AchievementManager manager, OwnedGame game)
    {
        _manager = manager;
        _game = game;
        _selectedSort = SortOptions[0];
        AchievementsView = CollectionViewSource.GetDefaultView(Achievements);
        AchievementsView.Filter = Filter;
        StatsView = CollectionViewSource.GetDefaultView(Stats);
        StatsView.Filter = FilterStat;
    }

    public async Task LoadAsync()
    {
        State = AchievementsState.Loading;
        ErrorMessage = string.Empty;
        try
        {
            _handle = await _manager.OpenAsync(_game).ConfigureAwait(true);
            var list = await _handle.ListAsync().ConfigureAwait(true);

            foreach (var a in Achievements) a.PropertyChanged -= OnAchievementChanged;
            Achievements.Clear();
            foreach (var a in list)
            {
                a.PropertyChanged += OnAchievementChanged;
                Achievements.Add(a);
            }

            State = Achievements.Count == 0 ? AchievementsState.Empty : AchievementsState.Loaded;
            RaiseCounts();

            // Stats use the same handle. Schema comes from Steam's local cache.
            await LoadStatsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = AchievementsState.Error;
        }
    }

    private async Task LoadStatsAsync()
    {
        if (_handle is null) { StatsState = AchievementsState.Empty; return; }

        var schema = SteamSchemaReader.ReadStats(_game.AppId);
        if (schema.Count == 0)
        {
            StatsState = AchievementsState.Empty;
            return;
        }

        StatsState = AchievementsState.Loading;
        try
        {
            var reads = await _handle.GetStatsAsync(schema.Select(s => s.Apiname)).ConfigureAwait(true);
            var byApiname = reads.ToDictionary(r => r.Apiname, StringComparer.Ordinal);

            foreach (var s in Stats) s.PropertyChanged -= OnStatChanged;
            Stats.Clear();

            foreach (var entry in schema)
            {
                if (!byApiname.TryGetValue(entry.Apiname, out var read)) continue;
                if (!string.IsNullOrEmpty(read.Error)) continue;

                var type = read.Type == "int" ? StatType.Int : StatType.Float;
                var info = new StatInfo(
                    apiname: entry.Apiname,
                    displayName: entry.DisplayName,
                    type: type,
                    originalValue: read.Value,
                    defaultValue: entry.DefaultValue ?? 0);
                info.PropertyChanged += OnStatChanged;
                Stats.Add(info);
            }

            StatsState = Stats.Count == 0 ? AchievementsState.Empty : AchievementsState.Loaded;
            RaiseStatCounts();
        }
        catch (Exception ex)
        {
            StatsErrorMessage = ex.Message;
            StatsState = AchievementsState.Error;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_handle is null) return;
        StatusBanner = string.Empty;

        if (IsOnStatsTab)
        {
            await SaveStatsAsync().ConfigureAwait(true);
            return;
        }

        var dirty = Achievements.Where(a => a.IsDirty).ToList();
        if (dirty.Count == 0) return;

        State = AchievementsState.Saving;
        ErrorMessage = string.Empty;

        if (DripFeed)
            await SaveDripAsync(dirty);
        else
            await SaveBatchAsync(dirty);
    }

    private async Task SaveStatsAsync()
    {
        if (_handle is null) return;
        var dirty = Stats.Where(s => s.IsDirty).ToList();
        if (dirty.Count == 0) return;

        StatsState = AchievementsState.Saving;
        SaveStatus = $"Applying {dirty.Count} stat change{(dirty.Count == 1 ? "" : "s")}...";

        IReadOnlyList<(string Apiname, string Error)> failures;
        try
        {
            failures = await _handle.ApplyStatsBatchAsync(
                dirty.Select(s => (s.Apiname, s.TypeLabel, s.ParsedValue)),
                store: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatsState = AchievementsState.Loaded;
            SaveStatus = string.Empty;
            StatusBanner = $"Save failed: {ex.Message}";
            StatusIsError = true;
            return;
        }

        var failedNames = failures.Select(f => f.Apiname).ToHashSet(StringComparer.Ordinal);
        var applied = dirty.Where(s => !failedNames.Contains(s.Apiname)).ToList();
        foreach (var s in applied) s.CommitOriginal();
        RaiseStatCounts();
        StatsState = AchievementsState.Loaded;
        SaveStatus = string.Empty;

        ReportOutcome(dirty.Count, applied.Count, failures.Select(f =>
            (dirty.FirstOrDefault(s => s.Apiname == f.Apiname)?.DisplayName ?? f.Apiname, f.Error)).ToList(),
            "stat change");
    }

    /// <summary>
    /// SAM-fast path: one IPC round trip applies everything.
    /// </summary>
    private async Task SaveBatchAsync(List<AchievementInfo> dirty)
    {
        SaveStatus = $"Applying {dirty.Count} change{(dirty.Count == 1 ? "" : "s")}...";

        IReadOnlyList<(string Apiname, string Error)> failures;
        try
        {
            failures = await _handle!.ApplyBatchAsync(
                dirty.Select(a => (a.Apiname, a.Achieved)),
                store: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            State = AchievementsState.Loaded;
            SaveStatus = string.Empty;
            StatusBanner = $"Save failed: {ex.Message}";
            StatusIsError = true;
            return;
        }

        var failedNames = failures.Select(f => f.Apiname).ToHashSet(StringComparer.Ordinal);
        var applied = dirty.Where(a => !failedNames.Contains(a.Apiname)).ToList();
        var unlocked = applied.Where(a => a.Achieved).Select(a => a.Name).ToList();
        var cleared  = applied.Where(a => !a.Achieved).Select(a => a.Name).ToList();
        foreach (var a in applied) a.CommitOriginal();
        RaiseCounts();
        State = AchievementsState.Loaded;
        SaveStatus = string.Empty;

        ReportAchievementOutcome(dirty.Count, unlocked, cleared, failures.Select(f =>
            (dirty.FirstOrDefault(a => a.Apiname == f.Apiname)?.Name ?? f.Apiname, f.Error)).ToList());
    }

    /// <summary>
    /// Spaced path: one-by-one with a randomized 0.5–2.5s gap so popups don't
    /// pile up. Slower but lets you watch them tick by; also looks less robotic
    /// to anyone glancing at your profile activity feed.
    /// </summary>
    private async Task SaveDripAsync(List<AchievementInfo> dirty)
    {
        var applied = new List<AchievementInfo>();
        var failures = new List<(string Name, string Error)>();

        for (var i = 0; i < dirty.Count; i++)
        {
            var a = dirty[i];
            SaveStatus = $"Applying {i + 1} of {dirty.Count}: {a.Name}";
            try
            {
                var result = await _handle!.ApplyBatchAsync(
                    new[] { (a.Apiname, a.Achieved) },
                    store: true).ConfigureAwait(true);
                if (result.Count == 0)
                {
                    applied.Add(a);
                }
                else
                {
                    foreach (var f in result) failures.Add((a.Name, f.Error));
                }
            }
            catch (Exception ex)
            {
                failures.Add((a.Name, ex.Message));
            }

            if (i < dirty.Count - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(Rng.Next(500, 2500))).ConfigureAwait(true);
        }

        var unlocked = applied.Where(a => a.Achieved).Select(a => a.Name).ToList();
        var cleared  = applied.Where(a => !a.Achieved).Select(a => a.Name).ToList();
        foreach (var a in applied) a.CommitOriginal();
        RaiseCounts();
        State = AchievementsState.Loaded;
        SaveStatus = string.Empty;

        ReportAchievementOutcome(dirty.Count, unlocked, cleared, failures);
    }

    private void ReportAchievementOutcome(
        int total,
        List<string> unlocked,
        List<string> cleared,
        List<(string Name, string Error)> failures)
    {
        var appliedCount = unlocked.Count + cleared.Count;

        if (failures.Count == 0)
        {
            var parts = new List<string>();
            if (unlocked.Count > 0)
            {
                parts.Add(unlocked.Count <= 3
                    ? $"Unlocked: {string.Join(", ", unlocked)}"
                    : $"Unlocked {unlocked.Count} achievement(s)");
            }
            if (cleared.Count > 0)
            {
                parts.Add(cleared.Count <= 3
                    ? $"Cleared: {string.Join(", ", cleared)}"
                    : $"Cleared {cleared.Count} achievement(s)");
            }
            StatusBanner = parts.Count > 0
                ? string.Join("  ·  ", parts)
                : "No changes saved.";
            StatusIsError = false;
            return;
        }

        var lines = new List<string>
        {
            appliedCount > 0
                ? $"Saved {appliedCount} of {total}. {failures.Count} couldn't be applied:"
                : $"Couldn't apply any of {total} changes:"
        };
        foreach (var f in failures.Take(4))
            lines.Add($"  • {f.Name} — {f.Error}");
        if (failures.Count > 4)
            lines.Add($"  ...and {failures.Count - 4} more.");

        StatusBanner = string.Join("\n", lines);
        StatusIsError = true;
    }

    private void ReportOutcome(int total, int appliedCount, List<(string Name, string Error)> failures, string noun)
    {
        // Kept for the Stats save path which doesn't distinguish unlock/clear.
        if (failures.Count == 0)
        {
            StatusBanner = $"Saved {appliedCount} {noun}{(appliedCount == 1 ? "" : "s")}.";
            StatusIsError = false;
            return;
        }
        var lines = new List<string>
        {
            appliedCount > 0
                ? $"Saved {appliedCount} of {total}. {failures.Count} couldn't be applied:"
                : $"Couldn't apply any of {total} {noun}s:"
        };
        foreach (var f in failures.Take(4))
            lines.Add($"  • {f.Name} — {f.Error}");
        if (failures.Count > 4)
            lines.Add($"  ...and {failures.Count - 4} more.");
        StatusBanner = string.Join("\n", lines);
        StatusIsError = true;
    }

    [RelayCommand]
    private void DismissBanner() => StatusBanner = string.Empty;

    [RelayCommand]
    private void UnlockAllVisible() => SetAllVisible(true);

    [RelayCommand]
    private void LockAllVisible() => SetAllVisible(false);

    private void SetAllVisible(bool achieved)
    {
        foreach (var item in AchievementsView)
            if (item is AchievementInfo a) a.Achieved = achieved;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var a in Achievements) a.PropertyChanged -= OnAchievementChanged;
        foreach (var s in Stats) s.PropertyChanged -= OnStatChanged;
        if (_handle is not null)
        {
            try { await _handle.DisposeAsync().ConfigureAwait(false); } catch { }
            _handle = null;
        }
    }

    [RelayCommand]
    private void ResetStatToDefault(StatInfo? stat) => stat?.ResetToDefault();

    [RelayCommand]
    private void SetActiveTab(string? tab)
    {
        if (!string.IsNullOrEmpty(tab)) ActiveTab = tab;
    }

    partial void OnSearchTextChanged(string value)
    {
        AchievementsView.Refresh();
        StatsView.Refresh();
    }
    partial void OnStatusFilterChanged(AchievementStatusFilter value) => AchievementsView.Refresh();
    partial void OnSelectedSortChanged(SortOption value) => ApplySort();
    partial void OnActiveTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOnStatsTab));
        OnPropertyChanged(nameof(HasDirtyChanges));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void ApplySort()
    {
        AchievementsView.SortDescriptions.Clear();
        switch (SelectedSort.Mode)
        {
            case AchievementSortMode.NameAZ:
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Name), ListSortDirection.Ascending));
                break;
            case AchievementSortMode.UnlockDateRecent:
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.OriginallyUnlockedAt), ListSortDirection.Descending));
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Name), ListSortDirection.Ascending));
                break;
            case AchievementSortMode.UnlockedFirst:
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Achieved), ListSortDirection.Descending));
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Name), ListSortDirection.Ascending));
                break;
            case AchievementSortMode.LockedFirst:
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Achieved), ListSortDirection.Ascending));
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Name), ListSortDirection.Ascending));
                break;
            case AchievementSortMode.DirtyFirst:
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.IsDirty), ListSortDirection.Descending));
                AchievementsView.SortDescriptions.Add(new SortDescription(nameof(AchievementInfo.Name), ListSortDirection.Ascending));
                break;
            // Default: no sort descriptions = original worker order
        }
    }

    [RelayCommand]
    private void SetStatusFilter(string? filter)
    {
        if (Enum.TryParse<AchievementStatusFilter>(filter, ignoreCase: true, out var parsed))
            StatusFilter = parsed;
    }

    private void OnAchievementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AchievementInfo.Achieved) or nameof(AchievementInfo.IsDirty))
            RaiseCounts();
    }

    private void OnStatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatInfo.IsDirty))
            RaiseStatCounts();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(UnlockedCount));
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(HasDirtyChanges));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RaiseStatCounts()
    {
        OnPropertyChanged(nameof(StatCount));
        OnPropertyChanged(nameof(DirtyStatCount));
        OnPropertyChanged(nameof(HasDirtyChanges));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool FilterStat(object obj)
    {
        if (obj is not StatInfo s) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Apiname.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private bool Filter(object obj)
    {
        if (obj is not AchievementInfo a) return false;

        // Status filter
        var pass = StatusFilter switch
        {
            AchievementStatusFilter.Unlocked => a.Achieved,
            AchievementStatusFilter.Locked   => !a.Achieved,
            AchievementStatusFilter.Hidden   => a.Hidden,
            AchievementStatusFilter.Dirty    => a.IsDirty,
            _                                => true,
        };
        if (!pass) return false;

        // Text filter
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Apiname.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
