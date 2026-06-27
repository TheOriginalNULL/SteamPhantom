using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SteamPhantom;

/// <summary>
/// Loads the embedded Resources/icon.png as a window icon, silently no-ops if
/// the file isn't present yet (e.g. fresh checkout before the PNG is dropped).
/// Also exposes a static instance for binding from the Image elements that
/// show the icon in the title bar.
/// </summary>
internal static class WindowIconLoader
{
    private static ImageSource? _cached;
    private static bool _attempted;

    public static ImageSource? Icon
    {
        get
        {
            if (_attempted) return _cached;
            _attempted = true;
            try
            {
                var uri = new Uri("pack://application:,,,/SteamPhantom;component/Resources/icon.png");
                var bm = new BitmapImage();
                bm.BeginInit();
                bm.UriSource = uri;
                bm.CacheOption = BitmapCacheOption.OnLoad;
                bm.EndInit();
                bm.Freeze();
                _cached = bm;
            }
            catch
            {
                _cached = null;
            }
            return _cached;
        }
    }

    public static void Apply(Window window)
    {
        var ic = Icon;
        if (ic is not null) window.Icon = ic;
    }
}
