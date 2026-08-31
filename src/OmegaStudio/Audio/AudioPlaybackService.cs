namespace OmegaStudio.Audio;

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Transport controls over <see cref="AudioPlaybackEngine"/>.
///
/// <para>
/// One <see cref="State"/> replaces what used to be four independent booleans
/// that every method had to keep mutually consistent by hand. The play position
/// is read from the OpenAL source rather than estimated from a wall clock, which
/// is what lets a seek actually land: the clock and the audio cannot disagree
/// when there is only one of them.
/// </para>
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    /// <summary>How often the position is polled while playing. 25 ms is smooth at 60 Hz.</summary>
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(25);

    private readonly AudioPlaybackEngine _engine = new();
    private readonly Lock _sync = new();

    private Timer? _poll;
    private IAudioDecoder? _decoder;
    private bool _disposed;

    /// <summary>Raised whenever the position or the state changes. Not marshalled
    /// to any thread — the timer fires on the pool, so subscribers must post.</summary>
    public event Action? StateChanged;

    public bool IsLoaded { get; private set; }

    public PlaybackState State { get; private set; }

    public TimeSpan Duration { get; private set; }

    /// <summary>
    /// Where the transport is.
    ///
    /// <para>
    /// The driver is only asked while it is actually running the clip. A source that
    /// is stopped reports 0 no matter what offset has been written to it, so a scrub
    /// on a stopped or finished clip would read back as a jump to the start — the
    /// slider snapping to zero the instant you let go of it. Every non-playing state
    /// is therefore served from <see cref="_restingSeconds"/>, which the transport
    /// commands keep up to date.
    /// </para>
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            // Under the lock: the poll timer reaches the same engine from the pool, and a read
            // racing its Stop() means two threads calling into one OpenAL source. Safe to take
            // here because nothing holding it ever calls back into the UI.
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

    /// <summary>Where a non-playing transport sits. Meaningless while playing.</summary>
    private double _restingSeconds;

    public bool CanPlay => IsLoaded && State != PlaybackState.Playing;
    public bool CanPause => State == PlaybackState.Playing;
    public bool CanResume => State == PlaybackState.Paused;
    public bool CanStop => IsLoaded && State != PlaybackState.Stopped;

    /// <summary>Decodes a clip and hands it to the device, replacing whatever was loaded.</summary>
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

    /// <summary>
    /// Plays the clip from the beginning, always.
    ///
    /// <para>
    /// Play is a restart, not a resume: it rewinds even when the transport is paused
    /// or parked on a scrub. Continuing from where the transport is sitting is what
    /// <see cref="Resume"/> is for, and the Pause/Resume button stays enabled after a
    /// scrub precisely so that route exists.
    /// </para>
    /// </summary>
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

    /// <summary>Pauses if playing, resumes if paused. What the single UI button does.</summary>
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

    /// <summary>
    /// Moves the play position, in whatever state the transport is in.
    ///
    /// <para>
    /// Playing sources are re-pointed and keep going; paused sources take the offset
    /// straight away; stopped sources hold it until the next <see cref="Play"/>. All
    /// three are safe to drive from a slider at pointer-move rate.
    /// </para>
    /// </summary>
    public void SeekTo(TimeSpan position)
    {
        Transition(() => IsLoaded, () =>
        {
            double target = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
            _engine.PositionSeconds = target;
            _restingSeconds = target;

            // A scrub on a stopped clip gives the transport somewhere to start from,
            // so Play must resume rather than rewind. That is what Paused means here.
            if (State == PlaybackState.Stopped)
            {
                State = PlaybackState.Paused;
            }
        });
    }

    /// <summary>
    /// The shared guard for every transport command: hold the lock, re-check the
    /// precondition, run, notify. Exceptions propagate — swallowing them here is
    /// what let a seek that did nothing look like a seek that worked.
    /// </summary>
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

    /// <summary>
    /// Polls the source so the UI sees the position advance, and notices when the
    /// clip runs out — OpenAL has no completion callback.
    /// </summary>
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

                // Hold the transport at the end rather than letting it snap back to
                // zero on the last tick — a finished source reports 0.
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
