using Silk.NET.OpenAL;
namespace OmegaStudio.Audio;
public class AudioPlaybackEngine : IDisposable
{
    private const int BufferCount = 4;
    private const double BufferSeconds = 0.25;
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly Dictionary<uint, int> _bufferFrames = [];
    private readonly uint[] _one = new uint[1];
    private AudioContext? _context;
    private AL? _al;
    private uint _source;
    private IAudioDecoder? _decoder;
    private BufferFormat _format;
    private byte[] _scratch = [];
    private int _sampleRate;
    private long _frameCount;
    private long _playedFrames;
    private bool _exhausted;
    private bool _disposed;
    public bool IsInitialized => _al is not null && _source != 0;
    public bool IsPlaying => TryGetState() == SourceState.Playing;
    public bool IsFinished => _exhausted && QueuedBuffers() == 0;
    public double PositionSeconds
    {
        get
        {
            if (_al is null || _source == 0 || _sampleRate <= 0)
            {
                return 0;
            }
            _context?.MakeCurrent();
            _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int frames);
            return (double)(_playedFrames + frames) / _sampleRate;
        }
        set
        {
            if (_al is null || _source == 0 || _sampleRate <= 0 || _decoder is null)
            {
                return;
            }
            bool playing = IsPlaying;
            _context?.MakeCurrent();
            _al.SourceStop(_source);
            UnqueueAll();
            long frame = Math.Clamp((long)Math.Round(value * _sampleRate, MidpointRounding.AwayFromZero), 0, Math.Max(0, _frameCount - 1));
            _decoder.SeekToFrame(frame);
            _playedFrames = frame;
            _exhausted = false;
            Fill();
            if (playing)
            {
                _al.SourcePlay(_source);
            }
            _ = _al.GetError();
        }
    }
    private SourceState TryGetState()
    {
        if (_al is null || _source == 0)
        {
            return SourceState.Stopped;
        }
        _context?.MakeCurrent();
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        return (SourceState)state;
    }
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Reset();
        AL al = Api();
        _ = al.GetError();
        _source = al.GenSource();
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = al.GenBuffer();
        }
    }
    public void Start(IAudioDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (_al is null || _source == 0)
        {
            throw new InvalidOperationException("Engine not initialized");
        }
        _context?.MakeCurrent();
        _format = (decoder.Channels, decoder.BitsPerSample) switch
        {
            (1, 8) => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8) => BufferFormat.Stereo8,
            (2, 16) => BufferFormat.Stereo16,
            _ => throw new NotSupportedException(
                $"Unsupported PCM layout: {decoder.Channels} channels at {decoder.BitsPerSample}-bit"),
        };
        _decoder = decoder;
        _sampleRate = decoder.SampleRate;
        _frameCount = decoder.TotalSamples / decoder.Channels;
        int frameBytes = decoder.FrameBytes;
        int chunk = (int)Math.Max(frameBytes, Math.Round(decoder.SampleRate * BufferSeconds) * frameBytes);
        _scratch = new byte[chunk];
        _playedFrames = 0;
        _exhausted = false;
        _al.SetSourceProperty(_source, SourceBoolean.Looping, false);
        Fill();
    }
    public void Pump()
    {
        if (_al is null || _source == 0 || _decoder is null)
        {
            return;
        }
        _context?.MakeCurrent();
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        for (int i = 0; i < processed; i++)
        {
            _al.SourceUnqueueBuffers(_source, _one);
            _playedFrames += _bufferFrames.GetValueOrDefault(_one[0]);
            _bufferFrames.Remove(_one[0]);
            Enqueue(_one[0]);
        }
        if (QueuedBuffers() > 0 && TryGetState() == SourceState.Stopped)
        {
            _al.SourcePlay(_source);
        }
        _ = _al.GetError();
    }
    private void Fill()
    {
        foreach (uint buffer in _buffers)
        {
            if (!_bufferFrames.ContainsKey(buffer) && !Enqueue(buffer))
            {
                break;
            }
        }
    }
    private bool Enqueue(uint buffer)
    {
        if (_al is null || _decoder is null || _exhausted)
        {
            return false;
        }
        int read = _decoder.Read(_scratch);
        if (read <= 0)
        {
            _exhausted = true;
            return false;
        }
        _al.BufferData(buffer, _format, read == _scratch.Length ? _scratch : _scratch[..read], _sampleRate);
        _one[0] = buffer;
        _al.SourceQueueBuffers(_source, _one);
        _bufferFrames[buffer] = read / _decoder.FrameBytes;
        return true;
    }
    private int QueuedBuffers()
    {
        if (_al is null || _source == 0)
        {
            return 0;
        }
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        return queued;
    }
    private void UnqueueAll()
    {
        if (_al is null)
        {
            return;
        }
        int queued = QueuedBuffers();
        if (queued > 0)
        {
            uint[] unqueue = new uint[queued];
            _al.SourceUnqueueBuffers(_source, unqueue);
        }
        _bufferFrames.Clear();
    }
    public void Play()
    {
        if (_al is null || _source == 0)
        {
            return;
        }
        _context?.MakeCurrent();
        _al.SourcePlay(_source);
        _ = _al.GetError();
    }
    public void Pause()
    {
        if (_al is null || _source == 0)
        {
            return;
        }
        _context?.MakeCurrent();
        _al.SourcePause(_source);
        _ = _al.GetError();
    }
    public void Stop()
    {
        if (_al is null || _source == 0)
        {
            return;
        }
        _context?.MakeCurrent();
        _al.SourceStop(_source);
        _ = _al.GetError();
        PositionSeconds = 0;
    }
    public void Reset()
    {
        if (_al is not null)
        {
            _context?.MakeCurrent();
            if (_source != 0)
            {
                _al.SourceStop(_source);
                UnqueueAll();
                _al.DeleteSource(_source);
            }
            for (int i = 0; i < BufferCount; i++)
            {
                if (_buffers[i] != 0)
                {
                    _al.DeleteBuffer(_buffers[i]);
                    _buffers[i] = 0;
                }
            }
            _ = _al.GetError();
        }
        _decoder?.Dispose();
        _decoder = null;
        _bufferFrames.Clear();
        _source = 0;
        _sampleRate = 0;
        _frameCount = 0;
        _playedFrames = 0;
        _exhausted = false;
    }
    private void ReleaseContext()
    {
        try
        {
            _context?.MakeCurrent();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        _context?.Dispose();
        _context = null;
    }
    private AL Api()
    {
        _context ??= new AudioContext(null, 0, 0, true);
        _al ??= AL.GetApi(true);
        _context.MakeCurrent();
        return _al;
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Reset();
        _al?.Dispose();
        _al = null;
        ReleaseContext();
        GC.SuppressFinalize(this);
    }
}
