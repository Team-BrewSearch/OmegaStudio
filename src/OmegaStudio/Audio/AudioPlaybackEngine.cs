using Silk.NET.OpenAL;
namespace OmegaStudio.Audio;
public class AudioPlaybackEngine : IDisposable
{
    private readonly uint[] _queue = new uint[1];
    private AudioContext? _context;
    private AL? _al;
    private uint _source;
    private uint _buffer;
    private int _sampleRate;
    private int _frameCount;
    private bool _disposed;
    public bool IsInitialized => _al is not null && _source != 0 && _buffer != 0;
    public bool IsPlaying => TryGetState() == SourceState.Playing;
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
    private int ClampToBuffer(double seconds)
    {
        long frames = (long)Math.Round(seconds * _sampleRate, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(frames, 0, _frameCount - 1);
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
        _buffer = al.GenBuffer();
    }
    public void UploadPcm(byte[] pcm, int channels, int bitsPerSample, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (_al is null || _source == 0 || _buffer == 0)
        {
            throw new InvalidOperationException("Engine not initialized");
        }
        _context?.MakeCurrent();
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
        _al.SourceRewind(_source);
        _ = _al.GetError();
    }
    public void Reset()
    {
        if (_al is not null)
        {
            _context?.MakeCurrent();
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
        }
        _source = 0;
        _buffer = 0;
        _sampleRate = 0;
        _frameCount = 0;
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
