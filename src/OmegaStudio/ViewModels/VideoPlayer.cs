using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public sealed partial class VideoPlayer : ObservableObject, IDisposable
{
    private const int Ahead = 6;
    private readonly AudioPlaybackService _audio;
    private readonly Action<string> _log;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly ConcurrentBag<byte[]> _pool = [];
    private IVideoAsset? _asset;
    private IVideoReader? _reader;
    private VideoInfo? _info;
    private Channel<(int Index, byte[] Bgra)>? _frames;
    private CancellationTokenSource? _decoding;
    private Task _producer = Task.CompletedTask;
    private WriteableBitmap? _bitmap;
    private (int Index, byte[] Bgra)? _pending;
    private bool _hasSound;
    private double _start;
    private bool _pushing;
    private bool _scrubbing;
    private int _generation;
    private bool _disposed;
    private readonly Func<byte[], Task<bool>> _loadSound;
    private bool _awaitingFrame;
    public VideoPlayer(AudioPlaybackService audio, Func<byte[], Task<bool>> loadSound, Action<string> log)
    {
        _audio = audio;
        _loadSound = loadSound;
        _log = log;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Render, OnTick);
    }
    public Bitmap? Frame => _bitmap;
    public int FrameVersion { get; private set; }
    public bool IsLoaded => _info is not null;
    public bool IsPlaying { get; private set; }
    public double Duration => _info?.Duration.TotalSeconds ?? 0;
    public int CurrentFrame { get; private set; }
    public string PlayLabel => IsPlaying ? "Pause" : "Play";
    public string Status { get; private set; } = "No video selected";
    public string TimeDisplay => _info is { } info
        ? $"{TimeSpan.FromSeconds(Position):mm\\:ss\\.fff} / {info.Duration:mm\\:ss\\.fff}   frame {CurrentFrame + 1} / {info.FrameCount}"
        : string.Empty;
    public double Position
    {
        get;
        set
        {
            value = Math.Clamp(value, 0, Duration);
            if (field.Equals(value))
            {
                return;
            }
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeDisplay));
            if (!_pushing && !_scrubbing)
            {
                SeekTo(value);
            }
        }
    }
    public async Task BindAsync(IVideoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Unbind();
        int generation = _generation;
        _asset = asset;
        VideoInfo? info = PluginGuard.Try("Reading the video header", () => asset.Video, null);
        IVideoReader? reader = info is null ? null : PluginGuard.Try<IVideoReader?>("Opening the video", asset.OpenVideo, null);
        if (info is null || reader is null)
        {
            Status = "The video could not be opened";
            Notify();
            return;
        }
        _info = info;
        _reader = reader;
        _bitmap = new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        Status = $"{info.Codec} · {info.Width} x {info.Height} · {info.FramesPerSecond:0.###} fps · {info.FrameCount:N0} frames";
        OnPropertyChanged(nameof(Frame));
        Notify();
        Restart(0);
        byte[] soundtrack = await Task.Run(() => PluginGuard.Try("Reading the soundtrack", () => asset.Soundtrack.ToArray(), []));
        if (generation != _generation)
        {
            return;
        }
        bool sound = soundtrack.Length > 0 && await _loadSound(soundtrack);
        if (generation != _generation)
        {
            return;
        }
        _hasSound = sound;
        if (sound && IsPlaying)
        {
            _audio.SeekTo(TimeSpan.FromSeconds(Clock()));
            _audio.Resume();
        }
        Status += sound ? $" · sound {_audio.Duration:mm\\:ss\\.fff}" : soundtrack.Length > 0 ? " · sound failed to load" : " · no sound";
        OnPropertyChanged(nameof(Status));
    }
    public void Unbind()
    {
        _generation++;
        Halt();
        _timer.Stop();
        StopProducer();
        if (_reader is { } reader)
        {
            _ = _producer.ContinueWith(_ => PluginGuard.Try("Closing the video", reader.Dispose), TaskScheduler.Default);
        }
        _producer = Task.CompletedTask;
        _reader = null;
        _info = null;
        _asset = null;
        _hasSound = false;
        _pending = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _pool.Clear();
        CurrentFrame = 0;
        Status = "No video selected";
        _pushing = true;
        Position = 0;
        _pushing = false;
        OnPropertyChanged(nameof(Frame));
        Notify();
    }
    [RelayCommand]
    private void PlayPause()
    {
        if (_info is null)
        {
            return;
        }
        if (IsPlaying)
        {
            Halt();
        }
        else
        {
            if (Position >= Duration - (1 / _info.FramesPerSecond))
            {
                SeekTo(0);
            }
            Begin();
        }
        Notify();
    }
    [RelayCommand]
    private void Stop()
    {
        if (_info is null)
        {
            return;
        }
        Halt();
        SeekTo(0);
        Notify();
    }
    public void Scrub(bool held)
    {
        _scrubbing = held;
        if (!held)
        {
            SeekTo(Position);
        }
    }
    private void Begin()
    {
        _start = Position;
        IsPlaying = true;
        if (_hasSound)
        {
            _audio.SeekTo(TimeSpan.FromSeconds(Position));
            _audio.Resume();
        }
        _clock.Restart();
        _timer.Start();
    }
    private void Halt()
    {
        if (IsPlaying && _hasSound)
        {
            _audio.Pause();
        }
        IsPlaying = false;
        _clock.Stop();
    }
    private double Clock()
    {
        return _hasSound && _audio.State == PlaybackState.Playing
            ? _audio.Position.TotalSeconds
            : _start + _clock.Elapsed.TotalSeconds;
    }
    private void SeekTo(double seconds)
    {
        if (_info is not { } info)
        {
            return;
        }
        bool playing = IsPlaying;
        Halt();
        _pushing = true;
        Position = seconds;
        _pushing = false;
        Restart(info.FrameAt(TimeSpan.FromSeconds(seconds)));
        if (playing)
        {
            Begin();
        }
        Notify();
    }
    private void Restart(int frame)
    {
        StopProducer();
        if (_reader is not { } reader || _info is not { } info)
        {
            return;
        }
        Channel<(int, byte[])> frames = Channel.CreateBounded<(int, byte[])>(new BoundedChannelOptions(Ahead) { SingleReader = true, SingleWriter = true });
        CancellationTokenSource cancel = new();
        CancellationToken token = cancel.Token;
        Task previous = _producer;
        _frames = frames;
        _decoding = cancel;
        _pending = null;
        _awaitingFrame = true;
        _producer = Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try
            {
                token.ThrowIfCancellationRequested();
                reader.Seek(frame);
                while (!token.IsCancellationRequested)
                {
                    int index = reader.Position;
                    byte[] buffer = _pool.TryTake(out byte[]? reused) && reused.Length == info.FrameBytes ? reused : new byte[info.FrameBytes];
                    if (!reader.ReadFrame(buffer))
                    {
                        break;
                    }
                    ImageData.SwapRedBlueInPlace(buffer);
                    await frames.Writer.WriteAsync((index, buffer), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"Decoding the video stopped at frame {reader.Position}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                frames.Writer.TryComplete();
            }
        });
        _timer.Start();
    }
    private void StopProducer()
    {
        _decoding?.Cancel();
        _decoding?.Dispose();
        _decoding = null;
        if (_frames is { } frames)
        {
            while (frames.Reader.TryRead(out (int, byte[] Bgra) stale))
            {
                _pool.Add(stale.Bgra);
            }
        }
        _frames = null;
    }
    private void OnTick(object? sender, EventArgs e)
    {
        if (_info is not { } info || _frames is not { } frames)
        {
            _timer.Stop();
            return;
        }
        if (!IsPlaying)
        {
            if (_awaitingFrame && frames.Reader.TryRead(out (int Index, byte[] Bgra) first))
            {
                _awaitingFrame = false;
                Present(first);
            }
            else if (frames.Reader.Completion.IsCompleted)
            {
                _awaitingFrame = false;
            }
            if (!_awaitingFrame)
            {
                _timer.Stop();
            }
            return;
        }
        _awaitingFrame = false;
        double now = Clock();
        if (now >= Duration)
        {
            Halt();
            _pushing = true;
            Position = Duration;
            _pushing = false;
            Notify();
            return;
        }
        int target = info.FrameAt(TimeSpan.FromSeconds(now));
        (int Index, byte[] Bgra)? show = null;
        while (true)
        {
            (int Index, byte[] Bgra) next;
            if (_pending is { } held)
            {
                next = held;
                _pending = null;
            }
            else if (!frames.Reader.TryRead(out next))
            {
                break;
            }
            if (next.Index > target)
            {
                _pending = next;
                break;
            }
            if (show is { } dropped)
            {
                _pool.Add(dropped.Bgra);
            }
            show = next;
        }
        if (show is { } frame)
        {
            Present(frame);
        }
        if (!_scrubbing)
        {
            _pushing = true;
            Position = now;
            _pushing = false;
        }
    }
    private void Present((int Index, byte[] Bgra) frame)
    {
        if (_bitmap is not { } bitmap || _info is not { } info)
        {
            _pool.Add(frame.Bgra);
            return;
        }
        using (ILockedFramebuffer target = bitmap.Lock())
        {
            int row = info.Width * 4;
            for (int y = 0; y < info.Height; y++)
            {
                Marshal.Copy(frame.Bgra, y * row, target.Address + (y * target.RowBytes), row);
            }
        }
        _pool.Add(frame.Bgra);
        CurrentFrame = frame.Index;
        FrameVersion++;
        OnPropertyChanged(nameof(FrameVersion));
        OnPropertyChanged(nameof(CurrentFrame));
        OnPropertyChanged(nameof(TimeDisplay));
    }
    private void Notify()
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayLabel));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(CurrentFrame));
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Unbind();
        _timer.Stop();
    }
}
