using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using SteamPhantom.Models;
using SteamPhantom.Services;
using SteamPhantom.ViewModels;

namespace SteamPhantom;

public partial class App : Application
{
    private IdleManager? _idleManager;
    private CardFarmManager? _cardFarmManager;
    private SteamKitClient? _steamKit;
    private SettingsService? _settings;
    private TrayIconHost? _tray;
    private bool _suppressIdleSave;
    private bool _suppressCardSave;
    private bool _idleSessionsRestored;
    private bool _cardQueueRestored;

    public IdleManager? IdleManager => _idleManager;
    public SettingsService? SettingsService => _settings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = new SettingsService();
        _settings.Load();
        var settings = _settings;

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamPhantom/0.1 (+desktop)");

        _steamKit = new SteamKitClient();
        var authStore = new SteamAuthStore();
        var signIn = new SignInCoordinator(_steamKit, authStore);

        var catalog = new SteamGameCatalog(_steamKit, new SteamWebApiClient(http));

        _idleManager = new IdleManager { AutoStopHours = settings.Current.IdleAutoStopHours };
        var achievementManager = new AchievementManager();
        var cardChecker = new CardDropChecker(http);
        _cardFarmManager = new CardFarmManager(
            _idleManager,
            cardChecker,
            () => _steamKit?.IsLoggedOn == true
                  ? _steamKit.SteamId64.ToString()
                  : settings.Current.SteamId64);
        var cardFarmManager = _cardFarmManager;

        var libraryVm  = new LibraryViewModel(settings, catalog, _idleManager, achievementManager, signIn, cardFarmManager);
        cardFarmManager.RegisterLibraryProvider(() => libraryVm.Games);
        var idleVm     = new IdleViewModel(_idleManager);
        var cardsVm    = new CardFarmViewModel(cardFarmManager);
        var settingsVm = new SettingsViewModel(settings, signIn);

        var shell  = new ShellViewModel(libraryVm, idleVm, cardsVm, settingsVm);
        var window = new MainWindow { DataContext = shell };
        RestoreWindowGeometry(window, settings.Current);
        window.Closing += (_, __) => SaveWindowGeometry(window, settings);

        // Always register MainWindow so the tray can find it, even when we
        // never call Show() (launch-minimized path).
        MainWindow = window;

        var launchMinimized = settings.Current.LaunchMinimized
            || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        if (!launchMinimized) window.Show();

        _tray = new TrayIconHost(_idleManager, () => MainWindow);

        // Persist idle session list on every change (skip during shutdown).
        _idleManager.Sessions.CollectionChanged += (_, _) =>
        {
            if (_suppressIdleSave) return;
            settings.Current.IdleAppIds = _idleManager.Sessions.Select(s => s.AppId).ToList();
            try { settings.Save(); } catch { /* best-effort */ }
        };

        // Persist card-farm queue (any non-Done entry) on every change.
        cardFarmManager.Entries.CollectionChanged += (_, _) =>
        {
            if (_suppressCardSave) return;
            settings.Current.CardFarmAppIds = cardFarmManager.Entries
                .Where(en => en.Status is Models.CardFarmStatus.Queued or Models.CardFarmStatus.Active)
                .Select(en => en.AppId)
                .ToList();
            try { settings.Save(); } catch { /* best-effort */ }
        };

        // Restore the saved idle queue + card farm queue once the Library finishes loading.
        libraryVm.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName != nameof(LibraryViewModel.State)) return;
            if (libraryVm.State != LibraryState.Loaded) return;

            if (!_idleSessionsRestored)
            {
                _idleSessionsRestored = true;
                await RestoreIdleSessionsAsync(libraryVm, _idleManager).ConfigureAwait(true);
            }
            if (!_cardQueueRestored)
            {
                _cardQueueRestored = true;
                RestoreCardFarmQueue(libraryVm, cardFarmManager);
            }
        };

        // Silent re-login from stored DPAPI token, if any.
        _ = TrySilentLoginAsync(authStore, settingsVm, libraryVm);

        // Update check — fire-and-forget. Quiet failure if offline / manifest down.
        _ = CheckForUpdateAsync(http, shell);
    }

    private static async Task CheckForUpdateAsync(HttpClient http, ShellViewModel shell)
    {
        try
        {
            var checker = new UpdateChecker(http);
            var update = await checker.CheckAsync(AppLinks.UpdateManifestUrl).ConfigureAwait(true);
            if (update is null) return;
            shell.UpdateVersion       = update.Version;
            shell.UpdateDownloadUrl   = update.DownloadUrl;
            shell.UpdateReleaseNotes  = update.ReleaseNotes ?? string.Empty;
            shell.HasUpdate           = true;
        }
        catch { /* offline, malformed manifest, whatever — just don't show the banner */ }
    }

    private async Task TrySilentLoginAsync(SteamAuthStore store, SettingsViewModel settingsVm, LibraryViewModel libraryVm)
    {
        if (_steamKit is null) return;

        var stored = store.TryLoad();
        if (stored is null) return;

        libraryVm.SilentLoginMessage = $"Restoring your session as {stored.Value.AccountName}...";
        libraryVm.SilentLoginIsError = false;

        try
        {
            await _steamKit.LogOnWithTokenAsync(stored.Value.AccountName, stored.Value.RefreshToken)
                .ConfigureAwait(true);
            libraryVm.SilentLoginMessage = string.Empty;
            settingsVm.RefreshAccountState();
            await libraryVm.RefreshAsyncPublic().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            libraryVm.SilentLoginMessage =
                $"Couldn't restore your session ({stored.Value.AccountName}): {ex.Message}. " +
                $"Sign in again — your token will be re-saved.";
            libraryVm.SilentLoginIsError = true;
        }
    }

    private async Task RestoreIdleSessionsAsync(LibraryViewModel libraryVm, IdleManager idleManager)
    {
        if (_settings is null) return;
        var savedIds = _settings.Current.IdleAppIds?.ToList() ?? new();
        if (savedIds.Count == 0) return;

        foreach (var appId in savedIds)
        {
            var game = libraryVm.Games.FirstOrDefault(g => g.AppId == appId);
            if (game is null) continue;
            try { await idleManager.StartAsync(game).ConfigureAwait(true); }
            catch { /* skip */ }
        }
    }

    private void RestoreCardFarmQueue(LibraryViewModel libraryVm, CardFarmManager cardFarmManager)
    {
        if (_settings is null) return;
        var savedIds = _settings.Current.CardFarmAppIds?.ToList() ?? new();
        if (savedIds.Count == 0) return;

        var alreadyQueued = cardFarmManager.Entries.Select(en => en.AppId).ToHashSet();
        var idling = _idleManager?.Sessions.Select(s => s.AppId).ToHashSet() ?? new HashSet<uint>();

        foreach (var appId in savedIds)
        {
            if (alreadyQueued.Contains(appId) || idling.Contains(appId)) continue;
            var game = libraryVm.Games.FirstOrDefault(g => g.AppId == appId);
            if (game is null) continue;
            cardFarmManager.Entries.Add(new Models.CardFarmEntry(game));
        }
    }

    private static void RestoreWindowGeometry(MainWindow window, AppSettings settings)
    {
        if (settings.WindowWidth > 200) window.Width = settings.WindowWidth;
        if (settings.WindowHeight > 200) window.Height = settings.WindowHeight;
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top
            && IsOnScreen(left, top, window.Width, window.Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }
        if (settings.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        // Reject saved positions that would land off all monitors (e.g. unplugged screen).
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        return left + 50 < vl + vw && top + 50 < vt + vh
            && left + width - 50 > vl && top + height - 50 > vt;
    }

    private static void SaveWindowGeometry(MainWindow window, SettingsService settings)
    {
        try
        {
            settings.Current.WindowMaximized = window.WindowState == WindowState.Maximized;
            if (window.WindowState == WindowState.Normal)
            {
                settings.Current.WindowWidth = window.Width;
                settings.Current.WindowHeight = window.Height;
                settings.Current.WindowLeft = window.Left;
                settings.Current.WindowTop = window.Top;
            }
            settings.Save();
        }
        catch { /* best-effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _suppressIdleSave = true;
        _suppressCardSave = true;
        try { _tray?.Dispose(); } catch { }
        try { _idleManager?.StopAll(); } catch { }
        try { _cardFarmManager?.StopAll(); } catch { }
        try { _steamKit?.DisposeAsync().AsTask().Wait(2000); } catch { }
        base.OnExit(e);
    }
}
