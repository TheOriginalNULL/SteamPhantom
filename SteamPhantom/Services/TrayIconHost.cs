using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using SteamPhantom.Models;

namespace SteamPhantom.Services;

/// <summary>
/// Owns the system-tray icon: tooltip showing active idle count, left-click
/// shows the window, right-click menu offers Show / Stop all / Quit.
/// </summary>
public class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly IdleManager _idle;
    private readonly Func<Window?> _windowProvider;

    public TrayIconHost(IdleManager idle, Func<Window?> windowProvider)
    {
        _idle = idle;
        _windowProvider = windowProvider;

        _icon = new TaskbarIcon
        {
            ToolTipText = "SteamPhantom",
            Visibility = Visibility.Visible,
            ContextMenu = BuildMenu(),
        };
        _icon.TrayLeftMouseUp += (_, _) => ToggleWindow();

        // Set via System.Drawing.Icon directly. H.NotifyIcon's IconSource path
        // tries to interpret arbitrary ImageSource bytes as an .ico file and
        // throws ArgumentException for plain PNGs.
        try { _icon.Icon = LoadAppIcon() ?? BuildFallbackIcon(); }
        catch { try { _icon.Icon = BuildFallbackIcon(); } catch { } }

        // Programmatically-created tray icons need ForceCreate to be registered
        // with the OS shell. Without this they never appear at all.
        _icon.ForceCreate();

        _idle.Sessions.CollectionChanged += (_, _) => RefreshTooltip();
        RefreshTooltip();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "Show SteamPhantom" };
        show.Click += (_, _) => ShowWindow();
        var stopAll = new MenuItem { Header = "Stop all idling" };
        stopAll.Click += (_, _) => _idle.StopAll();
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(show);
        menu.Items.Add(stopAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private void RefreshTooltip()
    {
        var n = _idle.Sessions.Count;
        _icon.ToolTipText = n == 0 ? "SteamPhantom" : $"SteamPhantom · {n} idling";
    }

    /// <summary>
    /// Loads the embedded Resources/icon.png and wraps it in a single-image
    /// .ico container so System.Drawing.Icon can consume it. Returns null if
    /// the resource isn't present so the caller can fall back.
    /// </summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(
                new Uri("pack://application:,,,/SteamPhantom;component/Resources/icon.png"));
            if (info is null) return null;
            using var stream = info.Stream;
            using var pngMs = new MemoryStream();
            stream.CopyTo(pngMs);
            return WrapPngAsIcon(pngMs.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an in-memory .ico whose payload is the PNG bytes. Modern Windows
    /// (Vista+) supports PNG-encoded entries in the ICO container, so we avoid
    /// rasterizing to BMP/multi-size.
    /// </summary>
    private static Icon WrapPngAsIcon(byte[] png)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.Default, leaveOpen: true))
        {
            // ICONDIR (6 bytes)
            bw.Write((ushort)0);              // reserved, must be 0
            bw.Write((ushort)1);              // type: 1 = icon
            bw.Write((ushort)1);              // number of images

            // ICONDIRENTRY (16 bytes)
            bw.Write((byte)0);                // width  (0 means 256)
            bw.Write((byte)0);                // height (0 means 256)
            bw.Write((byte)0);                // palette color count (0 for non-palettized)
            bw.Write((byte)0);                // reserved, must be 0
            bw.Write((ushort)1);              // color planes
            bw.Write((ushort)32);             // bits per pixel
            bw.Write((uint)png.Length);       // image data size
            bw.Write((uint)22);               // offset to image data (header is 22 bytes)

            bw.Write(png);
        }
        ms.Position = 0;
        return new Icon(ms);
    }

    /// <summary>Used only when Resources/icon.png is missing.</summary>
    private static Icon BuildFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var halo = new SolidBrush(System.Drawing.Color.FromArgb(80, 124, 92, 255));
            g.FillEllipse(halo, 1, 1, 30, 30);
            using var fill = new SolidBrush(System.Drawing.Color.FromArgb(255, 124, 92, 255));
            g.FillEllipse(fill, 8, 8, 16, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ToggleWindow()
    {
        var window = _windowProvider();
        if (window is null) return;
        if (window.IsVisible && window.WindowState != WindowState.Minimized)
            window.Hide();
        else
            ShowWindow();
    }

    private void ShowWindow()
    {
        var window = _windowProvider();
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose() => _icon.Dispose();
}
