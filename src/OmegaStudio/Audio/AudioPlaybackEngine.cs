using Silk.NET.OpenAL;

namespace OmegaStudio.Audio;

/// <summary>
/// A single OpenAL source fed by a single static buffer.
///
/// <para>
/// Because the whole clip lives in one buffer, the driver owns the play position:
/// seeking is a property set on the source, and the position it reports back is
/// the real one. Nothing here estimates elapsed time from a wall clock.
/// </para>
/// </summary>
public sealed class AudioPlaybackEngine : IDisposable
{
    private readonly uint[] _queue = new uint[1];
    private AudioContext? _context;
    private AL? _al;
    private uint _source;
    private uint _buffer;
    private int _sampleRate;
    private int _frameCount;
    private bool _disposed;

    /// <summary>True once a device exists and a source and buffer are live on it.</summary>
    public bool IsInitialized => _al is not null && _source != 0 && _buffer != 0;

    public bool IsPlaying => TryGetState() == SourceState.Playing;

    /// <summary>
    /// The source's play position in seconds. Reading this is what keeps the UI
    /// clock and the audio in agreement; writing it is what makes seeking real.
    ///
    /// <para>
    /// Only meaningful while the source is playing or paused. A stopped source
    /// reports 0 even when a seek has been written to it — the offset is held and
    /// applied on the next <see cref="Play"/> — so the caller has to remember where
    /// a stopped transport is sitting rather than ask for it back.
    /// </para>
    ///
    /// <para>
    /// The offset is expressed in samples rather than seconds because the driver
    /// accepts sample counts exactly, while a float second count near the end of a
    /// long clip rounds up past the buffer and is discarded.
    /// </para>
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            if (_al is null || _source == 0 || _sampleRate <= 0)
            {
                return 0;
            }

            _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int frames);
            return (double)frames / _sampleRate;
        }

        set
        {
            if (_al is null || _source == 0 || _sampleRate <= 0 || _frameCount <= 0)
            {
                return;
            }

            _context?.MakeCurrent();
            _al.SetSourceProperty(_source, SourceInteger.SampleOffset, ClampToBuffer(value));
            _ = _al.GetError();
        }
    }

    /// <summary>
    /// Turns seconds into a sample offset the driver will actually take.
    ///
    /// <para>
    /// An offset at or past the buffer length is <em>silently</em> dropped: the write
    /// does nothing and <c>alGetError</c> still reports NoError, so nothing downstream
    /// can notice. That is what made dragging the transport slider to the far right
    /// leave playback running wherever it already was. The last sample is as far as the
    /// source can be moved, and landing there ends the clip on the next mix — which is
    /// what seeking to the end should do anyway.
    /// </para>
    /// </summary>
    private int ClampToBuffer(double seconds)
    {
        long frames = (long)Math.Round(seconds * _sampleRate, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(frames, 0, _frameCount - 1);
    }

    /// <summary>Opens the device and creates the source and buffer, replacing any previous ones.</summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Reset();

        _context = new AudioContext(null, 0, 0, true);
        _al = AL.GetApi(true);
        _ = _al.GetError();

        _source = _al.GenSource();
        _buffer = _al.GenBuffer();
    }

    public void UploadPcm(byte[] pcm, int channels, int bitsPerSample, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        if (_al is null)
        {
            throw new InvalidOperationException("Engine not initialized");
        }

        BufferFormat format = (channels, bitsPerSample) switch
        {
            (1, 8) => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8) => BufferFormat.Stereo8,
            (2, 16) => BufferFormat.Stereo16,
            _ => throw new NotSupportedException(
                $"Unsupported PCM layout: {channels} channels at {bitsPerSample}-bit"),
        };

        _al.BufferData<byte>(_buffer, format, pcm, sampleRate);
        _al.SetSourceProperty(_source, SourceBoolean.Looping, false);

        _sampleRate = sampleRate;
        _frameCount = pcm.Length / (channels * (bitsPerSample / 8));

        Queue();
    }

    /// <summary>Starts from the current position.</summary>
    public void Play()
    {
        if (_al is null)
        {
            return;
        }

        _context?.MakeCurrent();
        _al.SourcePlay(_source);
        _ = _al.GetError();
    }

    public void Pause()
    {
        if (_al is null)
        {
            return;
        }

        _context?.MakeCurrent();
        _al.SourcePause(_source);
        _ = _al.GetError();
    }

    /// <summary>Stops and rewinds to the start.</summary>
    public void Stop()
    {
        if (_al is null)
        {
            return;
        }

        _context?.MakeCurrent();
        _al.SourceStop(_source);
        _al.SourceRewind(_source);
        _ = _al.GetError();
    }

    /// <summary>
    /// Releases the source and buffer and closes the device, leaving the engine
    /// re-initialisable. <see cref="IsInitialized"/> reports false afterwards —
    /// it used to keep claiming true with a source handle of 0, so every later
    /// call went quietly nowhere.
    /// </summary>
    public void Reset()
    {
        if (_al is not null)
        {
            if (_source != 0)
            {
                _al.SourceStop(_source);
                _al.DeleteSource(_source);
            }

            if (_buffer != 0)
            {
                _al.DeleteBuffer(_buffer);
            }

            _ = _al.GetError();
            _al.Dispose();
        }

        _source = 0;
        _buffer = 0;
        _sampleRate = 0;
        _frameCount = 0;
        _al = null;

        _context?.Dispose();
        _context = null;
    }

    private void Queue()
    {
        if (_al is null)
        {
            return;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        if (queued > 0)
        {
            uint[] unqueue = new uint[queued];
            _al.SourceUnqueueBuffers(_source, unqueue);
        }

        _queue[0] = _buffer;
        _al.SourceQueueBuffers(_source, _queue);
        _ = _al.GetError();
    }

    private SourceState? TryGetState()
    {
        if (_al is null || _source == 0)
        {
            return null;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        return (SourceState)state;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
    }
}
