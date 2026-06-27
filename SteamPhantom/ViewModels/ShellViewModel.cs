using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SteamPhantom.ViewModels;

public enum NavSection
{
    Library,
    Idle,
    Cards,
    Settings
}

public partial class ShellViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly IdleViewModel _idle;
    private readonly CardFarmViewModel _cards;
    private readonly SettingsViewModel _settings;

    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private NavSection _activeSection;

    // Update-checker hooks (populated from App.OnStartup after the manifest check).
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string _updateVersion = string.Empty;
    [ObservableProperty] private string _updateDownloadUrl = string.Empty;
    [ObservableProperty] private string _updateReleaseNotes = string.Empty;

    public ShellViewModel(
        LibraryViewModel library, IdleViewModel idle,
        CardFarmViewModel cards, SettingsViewModel settings)
    {
        _library = library;
        _idle = idle;
        _cards = cards;
        _settings = settings;
        _currentPage = _library;
        _activeSection = NavSection.Library;
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        switch (section)
        {
            case nameof(NavSection.Library):
                CurrentPage = _library; ActiveSection = NavSection.Library; break;
            case nameof(NavSection.Idle):
                CurrentPage = _idle; ActiveSection = NavSection.Idle; break;
            case nameof(NavSection.Cards):
                CurrentPage = _cards; ActiveSection = NavSection.Cards; break;
            case nameof(NavSection.Settings):
                CurrentPage = _settings; ActiveSection = NavSection.Settings; break;
        }
    }

    [RelayCommand]
    private async Task RefreshLibrary()
    {
        // F5 / Ctrl+R always refreshes the Library regardless of active tab.
        if (_library.RefreshCommand.CanExecute(null))
            await _library.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenDownload()
    {
        if (!string.IsNullOrWhiteSpace(UpdateDownloadUrl))
            SteamPhantom.AppLinks.Open(UpdateDownloadUrl);
    }

    [RelayCommand]
    private void DismissUpdate() => HasUpdate = false;
}
