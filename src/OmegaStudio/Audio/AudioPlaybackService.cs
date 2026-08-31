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
    private readonly AudioPlaybackEngine _engine = new();
    private readonly Lock _sync = new();
    private Timer? _poll;
    private IAudioDecoder? _decoder;
    private bool _disposed;
    public event Action? StateChanged;
    public bool IsLoaded { get; private set; }
    public PlaybackState State { get; private set; }
    public TimeSpan Duration { get; private set; }
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
    private double _restingSeconds;
    public bool CanPlay => IsLoaded && State != PlaybackState.Playing;
    public bool CanPause => State == PlaybackState.Playing;
    public bool CanResume => State == PlaybackState.Paused;
    public bool CanStop => IsLoaded && State != PlaybackState.Stopped;
    public void Load(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            Unload();
            try
            {
                _decoder = AudioInfoHelper.CreateDecoder(data);
                _engine.Initialize();
                byte[] pcm = new byte[_decoder.TotalBytes];
                _decoder.ReadAll(pcm);
                _engine.UploadPcm(pcm, _decoder.Channels, _decoder.BitsPerSample, _decoder.SampleRate);
                Duration = _decoder.SampleRate > 0 && _decoder.Channels > 0
                    ? TimeSpan.FromSeconds(
                        (double)_decoder.TotalSamples / (_decoder.SampleRate * _decoder.Channels))
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
    private void StartPolling()
    {
        StopPolling();
        _poll = new Timer(_ => Tick(), null, s_pollInterval, s_pollInterval);
    }
    private void StopPolling()
    {
        _poll?.Dispose();
        _poll = null;
    }
    private void Tick()
    {
        lock (_sync)
        {
            if (_disposed || State != PlaybackState.Playing)
            {
                return;
            }
            if (!_engine.IsPlaying)
            {
                _engine.Stop();
                State = PlaybackState.Stopped;
                _restingSeconds = Duration.TotalSeconds;
                StopPolling();
            }
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_sync)
        {
            Unload();
            _engine.Dispose();
        }
    }
}
