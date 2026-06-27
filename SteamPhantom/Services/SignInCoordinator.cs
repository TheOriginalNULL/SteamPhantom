using System.Windows;
using SteamPhantom.ViewModels;
using SteamPhantom.Views;

namespace SteamPhantom.Services;

/// <summary>
/// Wraps the modal LoginWindow flow so any ViewModel can ask the user to
/// sign in without referencing WPF window types directly past this seam.
/// </summary>
public class SignInCoordinator
{
    public SteamKitClient Kit { get; }
    public SteamAuthStore Store { get; }

    public SignInCoordinator(SteamKitClient kit, SteamAuthStore store)
    {
        Kit = kit;
        Store = store;
    }

    public bool ShowSignInDialog()
    {
        var vm = new LoginWindowViewModel(Kit, Store);
        var window = new LoginWindow
        {
            DataContext = vm,
            Owner = Application.Current?.MainWindow,
        };
        var ok = window.ShowDialog();
        return ok == true && vm.IsAuthenticated;
    }

    public void SignOut()
    {
        Store.Clear();
        Kit.LogOff();
    }
}
