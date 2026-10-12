using System.Globalization;
namespace OmegaStudio.Audio;
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}
public sealed class AudioPlaybackService : IDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(25);
    private readonly AudioPlaybackEngine _engine;
    private readonly Lock _sync = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;
    private IAudioDecoder? _decoder;
    private bool _disposed;
    private double _restingSeconds;
    public event Action? StateChanged;
    public bool IsLoaded { get; private set; }
    public PlaybackState State { get; private set; }
    public TimeSpan Duration { get; private set; }
    public AudioPlaybackService()
    {
        _engine = new AudioPlaybackEngine();
    }
    public TimeSpan Position
    {
        get
        {
            lock (_sync)
            {
                if (!IsLoaded)
                {
                    return TimeSpan.Zero;
                }
                double seconds = State == PlaybackState.Playing
                    ? _engine.PositionSeconds
                    : _restingSeconds;
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds));
            }
        }
    }
    public bool CanPlay => IsLoaded && State != PlaybackState.Playing;
    public bool CanPause => State == PlaybackState.Playing;
    public bool CanResume => State == PlaybackState.Paused;
    public bool CanStop => IsLoaded && State != PlaybackState.Stopped;
    public void Load(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        IAudioDecoder decoder = AudioInfoHelper.CreateDecoder(data);
        byte[] pcm = new byte[decoder.TotalBytes];
        decoder.ReadAll(pcm);
        int channels = decoder.Channels;
        int bitsPerSample = decoder.BitsPerSample;
        int sampleRate = decoder.SampleRate;
        long totalSamples = decoder.TotalSamples;
        lock (_sync)
        {
            Unload();
            try
            {
                _decoder = decoder;
                _engine.Initialize();
                _engine.UploadPcm(pcm, channels, bitsPerSample, sampleRate);
                Duration = sampleRate > 0 && channels > 0
                    ? TimeSpan.FromSeconds((double)totalSamples / (sampleRate * channels))
                    : TimeSpan.Zero;
                IsLoaded = true;
                State = PlaybackState.Stopped;
                _restingSeconds = 0;
            }
            catch
            {
                Unload();
                throw;
            }
        }
        StateChanged?.Invoke();
    }
    public void Play()
    {
        Transition(() => CanPlay, () =>
        {
            _engine.PositionSeconds = 0;
            _restingSeconds = 0;
            _engine.Play();
            State = PlaybackState.Playing;
            StartPolling();
        });
    }
    public void Pause()
    {
        Transition(() => CanPause, () =>
        {
            _engine.Pause();
            _restingSeconds = _engine.PositionSeconds;
            State = PlaybackState.Paused;
            StopPolling();
        });
    }
    public void Resume()
    {
        Transition(() => CanResume, () =>
        {
            _engine.Play();
            State = PlaybackState.Playing;
            StartPolling();
        });
    }
    public void TogglePause()
    {
        if (CanPause)
        {
            Pause();
        }
        else if (CanResume)
        {
            Resume();
        }
    }
    public void Stop()
    {
        Transition(() => CanStop, () =>
        {
            _engine.Stop();
            State = PlaybackState.Stopped;
            _restingSeconds = 0;
            StopPolling();
        });
    }
    public void SeekTo(TimeSpan position)
    {
        Transition(() => IsLoaded, () =>
        {
            double target = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
            _engine.PositionSeconds = target;
            _restingSeconds = target;
            if (State == PlaybackState.Stopped)
            {
                State = PlaybackState.Paused;
            }
        });
    }
    private void Transition(Func<bool> canRun, Action body)
    {
        if (_disposed)
        {
            return;
        }
        lock (_sync)
        {
            if (!IsLoaded || !_engine.IsInitialized || !canRun())
            {
                return;
            }
            body();
        }
        StateChanged?.Invoke();
    }
    private void StopPolling()
    {
        CancellationTokenSource? found = _pollCts;
        _pollCts = null;
        _pollLoop = null;
        found?.Cancel();
    }
    private void StartPolling()
    {
        StopPolling();
        CancellationTokenSource cts = new();
        PeriodicTimer timer = new(s_pollInterval);
        _pollCts = cts;
        _pollLoop = Task.Factory.StartNew(
            () => PollLoop(timer, cts),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }
    private async Task PollLoop(PeriodicTimer timer, CancellationTokenSource stop)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            timer.Dispose();
            stop.Dispose();
        }
    }
    private void Tick()
    {
        lock (_sync)
        {
            if (_disposed || State != PlaybackState.Playing)
            {
                return;
            }
            if (_engine.IsPlaying)
            {
                return;
            }
            _engine.Stop();
            State = PlaybackState.Stopped;
            _restingSeconds = Duration.TotalSeconds;
            StopPolling();
        }
        StateChanged?.Invoke();
    }
    private void Unload()
    {
        StopPolling();
        _engine.Reset();
        _decoder?.Dispose();
        _decoder = null;
        IsLoaded = false;
        State = PlaybackState.Stopped;
        Duration = TimeSpan.Zero;
        _restingSeconds = 0;
    }
    public void Dispose()
    {
        Task? loop;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Unload();
            _engine.Dispose();
            loop = _pollLoop;
        }
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }
}
