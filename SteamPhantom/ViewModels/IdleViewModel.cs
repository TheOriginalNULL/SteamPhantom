using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPhantom.Models;
using SteamPhantom.Services;

namespace SteamPhantom.ViewModels;

public partial class IdleViewModel : ObservableObject
{
    public IdleManager Manager { get; }

    public IdleViewModel(IdleManager manager)
    {
        Manager = manager;
    }

    [RelayCommand]
    private void Stop(IdleSession? session)
    {
        if (session is not null) Manager.Stop(session);
    }

    [RelayCommand]
    private void StopAll() => Manager.StopAll();
}
