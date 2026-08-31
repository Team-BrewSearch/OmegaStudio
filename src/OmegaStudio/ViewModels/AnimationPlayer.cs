using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
using System.Diagnostics;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Plays a clip against the selected asset and pushes out posed vertex frames.
///
/// <para>
/// The host owns the clock, the transport and the clip list; the plugin owns the
/// skinning, through <see cref="IAnimatedAsset.TryPose"/>. That is the right seam:
/// a timer running at 60 Hz and a play button are the same for every game, while how
/// a vertex follows a bone is the one part that is genuinely format knowledge.
/// </para>
///
/// <para>
/// Knows nothing about the viewport either: a finished frame goes out through
/// <see cref="FrameReady"/> and the return to rest through
/// <see cref="FrameCleared"/>, so the renderer stays replaceable.
/// </para>
/// </summary>
public sealed partial class AnimationPlayer : ObservableObject, IDisposable
{
    private const double TargetFps = 60.0;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    private IAnimatedAsset? _asset;
    private bool _disposed;

    public AnimationPlayer()
    {
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000.0 / TargetFps), DispatcherPriority.Background, OnTick);
    }

    /// <summary>
    /// The "no clip" entry that heads every list.
    ///
    /// <para>
    /// A real <see cref="AnimationClip"/> with no tracks and no frames rather than a
    /// null placeholder, so it can sit in the combo box like any other item and show
    /// its name. Having no tracks is also what makes the dope sheet fall through to
    /// its "No animation selected" message for free.
    /// </para>
    /// </summary>
    public static AnimationClip None { get; } = new() { Name = "None" };

    /// <summary>A posed frame: positions then normals, one entry per source vertex.</summary>
    public event Action<float[], float[]>? FrameReady;

    /// <summary>The asset should return to its rest pose.</summary>
    public event Action? FrameCleared;

    /// <summary>Clips available for the selected asset, always led by <see cref="None"/>.</summary>
    public ObservableCollection<AnimationClip> Clips { get; } = [];

    [ObservableProperty]
    public partial AnimationClip? SelectedClip { get; set; }

    public bool IsPlaying { get; private set; }

    public double Time { get; private set; }

    public double Duration => SelectedClip?.Duration ?? 0;

    /// <summary>Whether the animation pane has anything to offer at all.</summary>
    public bool IsAvailable => _asset is not null && Clips.Count > 1;

    /// <summary>Whether the clip picker should be usable.</summary>
    public bool CanSelect => _asset is not null && Clips.Count > 0;

    /// <summary>Whether there is something to actually play. False on <see cref="None"/>.</summary>
    public bool CanPlay => _asset is not null && SelectedClip is { } clip && !IsNone(clip);

    private static bool IsNone(AnimationClip? clip)
    {
        return ReferenceEquals(clip, None);
    }

    /// <summary>
    /// Points the player at the asset on screen and collects the clips it offers.
    ///
    /// <para>
    /// The asset being viewed is the asset to animate — which sounds obvious, but the
    /// version of this that searched the surrounding container for something animated
    /// routinely found the wrong one. A Harugeki container holds a character and, for
    /// an alt costume, its detachable hat as a second model; searching found the pair
    /// and skinned the character's clips onto the accessory. Asking the selected node
    /// what it can play cannot make that mistake.
    /// </para>
    /// </summary>
    public void Bind(IAnimatedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        Halt();
        Clips.Clear();
        _asset = asset;

        // "None" leads every list, whether or not there are any clips, so there is
        // always a way back to the unposed asset.
        Clips.Add(None);

        foreach (AnimationClip clip in PluginGuard.Try(
            "Listing animation clips", () => (IReadOnlyList<AnimationClip>)[.. asset.Clips], []))
        {
            Clips.Add(clip);
        }

        // Start unposed: selecting an asset shows it as authored until a clip is picked.
        SelectedClip = None;
        NotifyAvailability();
    }

    /// <summary>Detaches from any asset and returns the view to the rest pose.</summary>
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

    /// <summary>
    /// Stops playback and rewinds to the clip's first frame.
    ///
    /// <para>
    /// Deliberately not a return to the rest pose. Stopping parks the asset at the
    /// start of the clip you were watching, which is what you want to look at next;
    /// dropping to rest also forced a full geometry rebuild and re-framed the camera,
    /// throwing away wherever you had orbited to. Choosing <see cref="None"/> is how
    /// you ask for the unposed asset.
    /// </para>
    /// </summary>
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

    /// <summary>Stops the clock without touching the posed frame.</summary>
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

        double duration = clip.Duration;
        Time = duration > 0 ? _clock.Elapsed.TotalSeconds % duration : 0;
        OnPropertyChanged(nameof(Time));
        Pose(clip, Time);
    }

    /// <summary>
    /// Asks the plugin for a posed frame and passes it on.
    ///
    /// <para>
    /// A plugin that declines the frame, or throws producing it, drops the asset to
    /// its rest pose rather than leaving the last good frame frozen on screen —
    /// showing a stale pose while the clock keeps running reads as a hang.
    /// </para>
    /// </summary>
    private void Pose(AnimationClip clip, double seconds)
    {
        if (_asset is not { } asset)
        {
            return;
        }

        (float[] Positions, float[] Normals)? posed = PluginGuard.Try<(float[], float[])?>(
            $"Posing '{clip.Name}'",
            () => asset.TryPose(clip, seconds, out float[] positions, out float[] normals)
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
