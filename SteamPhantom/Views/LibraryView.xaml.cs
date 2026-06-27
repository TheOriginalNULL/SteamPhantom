using System.Windows.Controls;
using SteamPhantom.ViewModels;

namespace SteamPhantom.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is LibraryViewModel vm)
                await vm.EnsureLoadedAsync();
        };
    }
}
