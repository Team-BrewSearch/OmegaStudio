using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public sealed partial class AnimationPlayer : ObservableObject, IDisposable
{
    private const double TargetFps = 60.0;
    private const decimal DefaultFps = 30m;
    private const decimal SlowestFps = 1m;
    private const decimal FastestFps = 240m;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private IAnimatedAsset? _asset;
    private bool _disposed;
    public AnimationPlayer()
    {
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000.0 / TargetFps), DispatcherPriority.Background, OnTick);
    }
    public static AnimationClip None { get; } = new() { Name = "None" };
    public event Action<float[], float[]>? FrameReady;
    public event Action? FrameCleared;
    public ObservableCollection<AnimationClip> Clips { get; } = [];
    [ObservableProperty]
    public partial AnimationClip? SelectedClip { get; set; }
    [ObservableProperty]
    public partial decimal Fps { get; set; } = DefaultFps;
    public bool IsPlaying { get; private set; }
    public double Time { get; private set; }
    public double Duration => SelectedClip is { Frames: > 0 } clip && Fps > 0 ? clip.Frames / (double)Fps : 0;
    public bool IsAvailable => _asset is not null && Clips.Count > 1;
    public bool CanSelect => _asset is not null && Clips.Count > 0;
    public bool CanPlay => _asset is not null && SelectedClip is { } clip && !IsNone(clip);
    public AnimationClip? ExportClip => SelectedClip is { } clip && !IsNone(clip) ? clip : null;
    public float ExportFps => (float)Fps;
    private static bool IsNone(AnimationClip? clip)
    {
        return ReferenceEquals(clip, None);
    }
    public void Bind(IAnimatedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Halt();
        Clips.Clear();
        _asset = asset;
        Clips.Add(None);
        foreach (AnimationClip clip in PluginGuard.Try(
            "Listing animation clips", () => (IReadOnlyList<AnimationClip>)[.. asset.Clips], []))
        {
            Clips.Add(clip);
        }
        Fps = Clips.FirstOrDefault(clip => !IsNone(clip) && clip.Fps > 0f) is { } rated
            ? Math.Clamp((decimal)rated.Fps, SlowestFps, FastestFps)
            : DefaultFps;
        SelectedClip = None;
        NotifyAvailability();
    }
    public void Unbind()
    {
        Halt();
        Clips.Clear();
        SelectedClip = null;
        _asset = null;
        ShowRestPose();
        NotifyAvailability();
    }
    [RelayCommand]
    public void Play()
    {
        if (!CanPlay)
        {
            return;
        }
        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
        OnPropertyChanged(nameof(IsPlaying));
    }
    [RelayCommand]
    public void Stop()
    {
        Halt();
        if (_asset is not null && SelectedClip is { } clip && !IsNone(clip))
        {
            Pose(clip, 0);
        }
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Time));
    }
    private void Halt()
    {
        IsPlaying = false;
        _clock.Stop();
        _timer.Stop();
        Time = 0;
    }
    private void ShowRestPose()
    {
        FrameCleared?.Invoke();
    }
    partial void OnSelectedClipChanged(AnimationClip? value)
    {
        Halt();
        if (_asset is null || value is null || IsNone(value))
        {
            ShowRestPose();
        }
        else
        {
            Pose(value, 0);
        }
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Time));
        OnPropertyChanged(nameof(Duration));
        NotifyAvailability();
    }
    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsPlaying || SelectedClip is not { } clip || _asset is null)
        {
            return;
        }
        double duration = Duration;
        Time = duration > 0 ? _clock.Elapsed.TotalSeconds % duration : 0;
        OnPropertyChanged(nameof(Time));
        Pose(clip, Time);
    }
    partial void OnFpsChanged(decimal value)
    {
        decimal held = Math.Clamp(value, SlowestFps, FastestFps);
        if (held != value)
        {
            Fps = held;
            return;
        }
        OnPropertyChanged(nameof(Duration));
        if (!IsPlaying && SelectedClip is { } clip && !IsNone(clip))
        {
            Pose(clip, Time);
        }
    }
    private void Pose(AnimationClip clip, double seconds)
    {
        if (_asset is not { } asset)
        {
            return;
        }
        double own = clip.Fps > 0f ? seconds * (double)Fps / clip.Fps : seconds;
        (float[] Positions, float[] Normals)? posed = PluginGuard.Try<(float[], float[])?>(
            $"Posing '{clip.Name}'",
            () => asset.TryPose(clip, own, out float[] positions, out float[] normals)
                ? (positions, normals)
                : null,
            null);
        if (posed is { } frame)
        {
            FrameReady?.Invoke(frame.Positions, frame.Normals);
        }
        else
        {
            ShowRestPose();
        }
    }
    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(Duration));
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _clock.Stop();
    }
}
