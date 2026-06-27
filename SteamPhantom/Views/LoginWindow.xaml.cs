using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SteamPhantom.ViewModels;

namespace SteamPhantom.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        WindowIconLoader.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LoginWindowViewModel oldVm) oldVm.RequestClose -= OnRequestClose;
        if (e.NewValue is LoginWindowViewModel newVm) newVm.RequestClose += OnRequestClose;
    }

    private void OnRequestClose()
    {
        Dispatcher.Invoke(() =>
        {
            DialogResult = DataContext is LoginWindowViewModel vm && vm.IsAuthenticated;
            Close();
        });
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginWindowViewModel vm && sender is PasswordBox pb)
            vm.Password = pb.Password;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableModernChrome(new WindowInteropHelper(this).Handle);
    }

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
