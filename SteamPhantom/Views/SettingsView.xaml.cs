using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SteamPhantom.ViewModels;

namespace SteamPhantom.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Discord_Click(object sender, RoutedEventArgs e) => AppLinks.OpenDiscord();

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try
        {
            var folder = Path.GetDirectoryName(vm.SettingsFilePath);
            if (string.IsNullOrEmpty(folder)) return;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch { }
    }
}
