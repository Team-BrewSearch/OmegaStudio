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
    private bool _disposed;
    private double _restingSeconds;
    private const int TicksPerProgress = 4;
    private int _ticks;
    public event Action? StateChanged;
    public event Action? ProgressChanged;
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
    public static PreparedAudio Prepare(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new PreparedAudio(AudioInfoHelper.CreateDecoder(data));
    }
    public void Commit(PreparedAudio prepared)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(prepared);
        lock (_sync)
        {
            Unload();
            try
            {
                _engine.Initialize();
                IAudioDecoder decoder = prepared.Take();
                Duration = AudioInfo.PcmDuration(decoder.TotalSamples, decoder.SampleRate, decoder.Channels);
                _engine.Start(decoder);
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
    public void Clear()
    {
        if (_disposed)
        {
            return;
        }
        lock (_sync)
        {
            Unload();
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
        _pollLoop = Task.Run(() => PollLoop(timer, cts));
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
        bool finished;
        lock (_sync)
        {
            if (_disposed || State != PlaybackState.Playing)
            {
                return;
            }
            _engine.Pump();
            finished = _engine.IsFinished;
            if (finished)
            {
                _engine.Stop();
                State = PlaybackState.Stopped;
                _restingSeconds = Duration.TotalSeconds;
                StopPolling();
            }
            else if (++_ticks % TicksPerProgress != 0)
            {
                return;
            }
        }
        (finished ? StateChanged : ProgressChanged)?.Invoke();
    }
    private void Unload()
    {
        StopPolling();
        _engine.Reset();
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
public sealed class PreparedAudio(IAudioDecoder decoder) : IDisposable
{
    private IAudioDecoder? _decoder = decoder;
    internal IAudioDecoder Take() =>
        Interlocked.Exchange(ref _decoder, null) ?? throw new ObjectDisposedException(nameof(PreparedAudio));
    public void Dispose() => Interlocked.Exchange(ref _decoder, null)?.Dispose();
}
