using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SteamPhantom.ViewModels;

namespace SteamPhantom.Views;

public partial class AchievementsWindow : Window
{
    private const string GlyphMaximize = "";
    private const string GlyphRestore  = "";

    public AchievementsWindow()
    {
        InitializeComponent();
        WindowIconLoader.Apply(this);

        Loaded += async (_, _) =>
        {
            if (DataContext is AchievementsWindowViewModel vm)
                await vm.LoadAsync();
        };

        Closed += async (_, _) =>
        {
            if (DataContext is AchievementsWindowViewModel vm)
                await vm.DisposeAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableModernChrome(new WindowInteropHelper(this).Handle);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool maxed = WindowState == WindowState.Maximized;
        RootContainer.Margin = maxed ? new Thickness(7) : new Thickness(0);
        MaxRestoreGlyph.Text = maxed ? GlyphRestore : GlyphMaximize;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND                   = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static void EnableModernChrome(IntPtr hwnd)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { }
    }
}
