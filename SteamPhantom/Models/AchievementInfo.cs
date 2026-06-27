using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPhantom.Models;

public partial class AchievementInfo : ObservableObject
{
    public string Apiname { get; }
    public string Name { get; }
    public string Description { get; }
    public DateTime? OriginallyUnlockedAt { get; }
    public bool Hidden { get; }
    public string? IconPath { get; }
    public uint IconWidth { get; }
    public uint IconHeight { get; }

    private bool _originalAchieved;
    private ImageSource? _cachedIcon;

    [ObservableProperty]
    private bool _achieved;

    public bool IsDirty => Achieved != _originalAchieved;

    public string DisplayName        => Hidden && !Achieved ? "Hidden achievement" : Name;
    public string DisplayDescription => Hidden && !Achieved ? "Description hidden until unlocked." : Description;

    public ImageSource? Icon => _cachedIcon ??= TryLoadIcon();

    public AchievementInfo(
        string apiname, string name, string description,
        bool achieved, uint unlockTimeUnix, bool hidden,
        string? iconPath, uint iconW, uint iconH)
    {
        Apiname = apiname;
        Name = string.IsNullOrEmpty(name) ? apiname : name;
        Description = description;
        Hidden = hidden;
        IconPath = iconPath;
        IconWidth = iconW;
        IconHeight = iconH;
        _originalAchieved = achieved;
        _achieved = achieved;
        OriginallyUnlockedAt = unlockTimeUnix == 0
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(unlockTimeUnix).LocalDateTime;
    }

    partial void OnAchievedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDescription));
    }

    public void CommitOriginal()
    {
        _originalAchieved = Achieved;
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>
    /// Reads the worker's icon file (8-byte width/height header + raw RGBA pixels)
    /// and returns a frozen BGRA32 BitmapSource that WPF can render directly.
    /// </summary>
    private ImageSource? TryLoadIcon()
    {
        if (string.IsNullOrEmpty(IconPath) || !File.Exists(IconPath))
            return null;
        if (IconWidth == 0 || IconHeight == 0)
            return null;

        try
        {
            var bytes = File.ReadAllBytes(IconPath);
            if (bytes.Length < 8 + IconWidth * IconHeight * 4) return null;

            // Skip the 8-byte header and convert RGBA → BGRA in place for WPF.
            var pixelCount = (int)(IconWidth * IconHeight);
            var pixels = new byte[pixelCount * 4];
            Array.Copy(bytes, 8, pixels, 0, pixels.Length);
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

            var stride = (int)(IconWidth * 4);
            var bs = BitmapSource.Create(
                (int)IconWidth, (int)IconHeight,
                96, 96,
                PixelFormats.Bgra32, null,
                pixels, stride);
            bs.Freeze();
            return bs;
        }
        catch
        {
            return null;
        }
    }
}
