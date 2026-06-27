using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SteamPhantom;

public partial class MainWindow : Window
{
    private const string GlyphMaximize = "";
    private const string GlyphRestore  = "";

    public MainWindow()
    {
        InitializeComponent();
        WindowIconLoader.Apply(this);
        BrandIcon.Source = WindowIconLoader.Icon;
    }

    private void Discord_Click(object sender, RoutedEventArgs e) => AppLinks.OpenDiscord();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableModernChrome(new WindowInteropHelper(this).Handle);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Any minimize (titlebar button OR taskbar OR Win+D) folds to tray.
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }
        bool maxed = WindowState == WindowState.Maximized;
        RootContainer.Margin = maxed ? new Thickness(7) : new Thickness(0);
        MaxRestoreGlyph.Text = maxed ? GlyphRestore : GlyphMaximize;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- DWM: round corners + dark immersive chrome (Win11 22H2+) ----

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
        catch
        {
            // Older Windows — silently fall back to square corners.
        }
    }
}
