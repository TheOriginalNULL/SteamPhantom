using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Models;
using SteamPhantom.Services;
using SteamPhantom.Views;

namespace SteamPhantom.ViewModels;

public enum LibraryState
{
    NeedsSetup,
    Loading,
    Error,
    Empty,
    Loaded
}

public partial class LibraryViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamGameCatalog _catalog;
    private readonly IdleManager _idleManager;
    private readonly AchievementManager _achievementManager;
    private readonly SignInCoordinator _signIn;
    private readonly CardFarmManager _cardFarmManager;
    private bool _hasAttemptedLoad;
    private int _notificationToken;

    public ObservableCollection<OwnedGame> Games { get; } = new();
    public ICollectionView GamesView { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private LibraryState _state = LibraryState.NeedsSetup;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _notification = string.Empty;
    [ObservableProperty] private bool _notificationIsError;
    [ObservableProperty] private string _silentLoginMessage = string.Empty;
    [ObservableProperty] private bool _silentLoginIsError;

    public LibraryViewModel(
        SettingsService settings,
        SteamGameCatalog catalog,
        IdleManager idleManager,
        AchievementManager achievementManager,
        SignInCoordinator signIn,
        CardFarmManager cardFarmManager)
    {
        _settings = settings;
        _catalog = catalog;
        _idleManager = idleManager;
        _achievementManager = achievementManager;
        _signIn = signIn;
        _cardFarmManager = cardFarmManager;
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = FilterGame;
        ((INotifyCollectionChanged)GamesView).CollectionChanged += (_, _) => RecountVisible();
        Games.CollectionChanged += OnGamesChanged;
    }

    /// <summary>Public wrapper around the private RefreshAsync, used by silent-login startup.</summary>
    public Task RefreshAsyncPublic() => RefreshAsync();

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (_signIn.ShowSignInDialog())
        {
            _hasAttemptedLoad = true;
            await LoadAsync().ConfigureAwait(false);
        }
    }

    private void OnGamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (OwnedGame g in e.NewItems) g.PropertyChanged += OnGamePropertyChanged;
        if (e.OldItems is not null)
            foreach (OwnedGame g in e.OldItems) g.PropertyChanged -= OnGamePropertyChanged;
        RecountSelected();
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OwnedGame.IsSelected))
            RecountSelected();
    }

    private void RecountSelected() => SelectedCount = Games.Count(g => g.IsSelected);

    public async Task EnsureLoadedAsync()
    {
        if (_hasAttemptedLoad) return;
        _hasAttemptedLoad = true;
        await LoadAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _hasAttemptedLoad = true;
        await LoadAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task StartIdleAsync(OwnedGame? game)
    {
        if (game is null) return;
        try
        {
            await _idleManager.StartAsync(game).ConfigureAwait(true);
            await ShowNotificationAsync($"Started idling {game.Name}.", false);
        }
        catch (Exception ex)
        {
            await ShowNotificationAsync(ex.Message, true);
        }
    }

    [RelayCommand]
    private async Task StartIdleSelectedAsync()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0) return;

        var started = 0;
        var failures = new List<string>();
        foreach (var g in selected)
        {
            try
            {
                await _idleManager.StartAsync(g).ConfigureAwait(true);
                g.IsSelected = false;
                started++;
            }
            catch (Exception ex)
            {
                failures.Add($"{g.Name}: {ex.Message}");
            }
        }

        if (failures.Count == 0)
            await ShowNotificationAsync($"Started idling {started} game{(started == 1 ? "" : "s")}.", false);
        else if (started == 0)
            await ShowNotificationAsync($"Couldn't start any. First error: {failures[0]}", true);
        else
            await ShowNotificationAsync($"Started {started}, {failures.Count} failed. First: {failures[0]}", true);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var g in Games) g.IsSelected = false;
    }

    [RelayCommand]
    private async Task AddSelectedToCardFarmAsync()
    {
        var selected = Games.Where(g => g.IsSelected).ToList();
        if (selected.Count == 0) return;

        var added = 0;
        var alreadyIdling = 0;
        foreach (var g in selected)
        {
            if (_cardFarmManager.IsAppIdInRunningIdle(g.AppId))
            {
                alreadyIdling++;
                continue;
            }
            _cardFarmManager.Enqueue(g);
            g.IsSelected = false;
            added++;
        }

        var msg = $"Queued {added} for card farm.";
        if (alreadyIdling > 0)
            msg += $" Skipped {alreadyIdling} (already running in Idle tab).";
        await ShowNotificationAsync(msg, isError: alreadyIdling > 0 && added == 0);
    }

    [RelayCommand]
    private void OpenAchievements(OwnedGame? game)
    {
        if (game is null) return;
        var vm = new AchievementsWindowViewModel(_achievementManager, game);
        var window = new AchievementsWindow
        {
            DataContext = vm,
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.Show();
    }

    private async Task LoadAsync()
    {
        var steamId = _settings.Current.SteamId64;
        if (string.IsNullOrWhiteSpace(steamId))
            steamId = SteamIdResolver.TryDetectActiveSteamId64() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(steamId))
        {
            State = LibraryState.NeedsSetup;
            ErrorMessage = string.Empty;
            return;
        }

        State = LibraryState.Loading;
        ErrorMessage = string.Empty;

        try
        {
            var games = await _catalog.GetOwnedGamesAsync(
                steamId,
                _settings.Current.SteamWebApiKey).ConfigureAwait(true);

            Games.Clear();
            foreach (var g in games) Games.Add(g);

            State = Games.Count == 0 ? LibraryState.Empty : LibraryState.Loaded;
            RecountVisible();
        }
        catch (SteamWebApiException ex)
        {
            ErrorMessage = ex.Message;
            State = ex.Failure == SteamWebApiFailure.MissingCredentials
                ? LibraryState.NeedsSetup
                : LibraryState.Error;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = LibraryState.Error;
        }
    }

    private async Task ShowNotificationAsync(string msg, bool isError)
    {
        Notification = msg;
        NotificationIsError = isError;
        var token = ++_notificationToken;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (token == _notificationToken) Notification = string.Empty;
    }

    partial void OnSearchTextChanged(string value)
    {
        GamesView.Refresh();
        RecountVisible();
    }

    private bool FilterGame(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not OwnedGame g) return false;
        return g.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RecountVisible()
    {
        var c = 0;
        foreach (var _ in GamesView) c++;
        VisibleCount = c;
    }
}
