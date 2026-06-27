using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Models;
using SteamPhantom.Services;

namespace SteamPhantom.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SignInCoordinator _signIn;

    [ObservableProperty] private string _steamId64 = string.Empty;
    [ObservableProperty] private string _steamWebApiKey = string.Empty;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _detectMessage = string.Empty;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _signedInAccount = string.Empty;
    [ObservableProperty] private string _idleAutoStopHoursText = "0";
    [ObservableProperty] private bool _launchMinimized;
    [ObservableProperty] private bool _runAtWindowsStartup;

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public string SettingsFilePath => _settings.SettingsPath;

    public SettingsViewModel(SettingsService settings, SignInCoordinator signIn)
    {
        _settings = settings;
        _signIn = signIn;
        SteamId64 = settings.Current.SteamId64;
        SteamWebApiKey = settings.Current.SteamWebApiKey;
        Theme = settings.Current.Theme;
        IdleAutoStopHoursText = settings.Current.IdleAutoStopHours.ToString();
        LaunchMinimized = settings.Current.LaunchMinimized;
        // Mirror the live registry state so the toggle reflects reality even
        // if the user toggled it from elsewhere.
        RunAtWindowsStartup = WindowsStartup.IsRegistered();
        RefreshAccountState();
    }

    public void RefreshAccountState()
    {
        IsSignedIn = _signIn.Kit.IsLoggedOn;
        SignedInAccount = _signIn.Kit.AccountName ?? string.Empty;
    }

    [RelayCommand]
    private void SignIn()
    {
        if (_signIn.ShowSignInDialog())
            RefreshAccountState();
    }

    [RelayCommand]
    private void SignOut()
    {
        _signIn.SignOut();
        RefreshAccountState();
    }

    [RelayCommand]
    private void Detect()
    {
        var detected = SteamIdResolver.TryDetectActiveSteamId64();
        if (detected is null)
        {
            DetectMessage = "Couldn't detect — make sure Steam is running and signed in.";
            return;
        }
        SteamId64 = detected;
        DetectMessage = "Detected from running Steam.";
    }

    [RelayCommand]
    private void Save()
    {
        _settings.Current.SteamId64 = SteamId64.Trim();
        _settings.Current.SteamWebApiKey = SteamWebApiKey.Trim();
        _settings.Current.Theme = Theme;
        _settings.Current.IdleAutoStopHours =
            int.TryParse(IdleAutoStopHoursText, out var h) && h >= 0 ? h : 0;
        _settings.Current.LaunchMinimized = LaunchMinimized;
        _settings.Current.RunAtWindowsStartup = RunAtWindowsStartup;

        // Push the new auto-stop limit to the running idle manager.
        if (System.Windows.Application.Current is App app && app.IdleManager is not null)
            app.IdleManager.AutoStopHours = _settings.Current.IdleAutoStopHours;

        // Sync the Windows Run key with the toggle state.
        WindowsStartup.Apply(RunAtWindowsStartup);

        _settings.Save();
        StatusMessage = $"Saved · {DateTime.Now:HH:mm:ss}";
    }
}
