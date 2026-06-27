using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamPhantom.Models;

public partial class IdleSession : ObservableObject
{
    public uint AppId { get; }
    public string Name { get; }
    public string HeaderImageUrl { get; }
    public DateTime StartedAt { get; }
    public Process Process { get; }

    [ObservableProperty]
    private TimeSpan _elapsed;

    public string ElapsedDisplay =>
        $"{(int)Elapsed.TotalHours:D2}:{Elapsed.Minutes:D2}:{Elapsed.Seconds:D2}";

    public IdleSession(OwnedGame game, Process process)
    {
        AppId = game.AppId;
        Name = game.Name;
        HeaderImageUrl = game.HeaderImageUrl;
        Process = process;
        StartedAt = DateTime.UtcNow;
    }

    partial void OnElapsedChanged(TimeSpan value) => OnPropertyChanged(nameof(ElapsedDisplay));
}
