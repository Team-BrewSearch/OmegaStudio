using Omega.Plugins;
using OmegaStudio.Services;
using OmegaStudio.Services.Gltf;
namespace OmegaStudio.Plugins;
internal sealed class HostServices : IHostServices
{
    private readonly Action<string> _log;
    private readonly string _prefix;
    private readonly AssetRecognition _recognition;
    private readonly PendingFlushes? _pending;
    public HostServices(
        LoadedPlugin plugin, Action<string> log, AssetRecognition? recognition = null, PendingFlushes? pending = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _prefix = plugin.Info.DisplayName;
        _recognition = recognition ?? AssetRecognition.None;
        _pending = pending;
        PluginDirectory = plugin.Directory;
    }
    public IImageCodec Images => ImageIO.Instance;
    public IGltfCodec Gltf => GltfCodec.Instance;
    public string PluginDirectory { get; }
    public bool IsKnownPayload(ReadOnlySpan<byte> data)
    {
        return _recognition.IsKnownPayload(data);
    }
    public void Log(string message)
    {
        _log($"[{_prefix}] {message}");
    }
    public void Defer(object key, Action flush)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(flush);
        (_pending ?? PendingFlushes.Current)?.Add(key, flush);
    }
}
internal sealed class PendingFlushes
{
    private static readonly AsyncLocal<PendingFlushes?> s_command = new();
    private static PendingFlushes? s_open;
    private readonly Lock _lock = new();
    private readonly Dictionary<object, (Action Flush, long Stamp)> _flushes = new(ReferenceEqualityComparer.Instance);
    private long _stamp;
    private bool _closed;
    public static PendingFlushes? Current => s_command.Value ?? Volatile.Read(ref s_open);
    public bool Any
    {
        get
        {
            lock (_lock)
            {
                return _flushes.Count > 0;
            }
        }
    }
    public static void Use(PendingFlushes? pending)
    {
        s_command.Value = pending;
    }
    public void Open()
    {
        Volatile.Write(ref s_open, this);
    }
    public void Add(object key, Action flush)
    {
        lock (_lock)
        {
            if (!_closed)
            {
                _flushes[key] = (flush, ++_stamp);
            }
        }
    }
    public void Flush()
    {
        while (Next() is (object key, Action flush, long stamp))
        {
            flush();
            lock (_lock)
            {
                if (_flushes.TryGetValue(key, out (Action Flush, long Stamp) now) && now.Stamp == stamp)
                {
                    _ = _flushes.Remove(key);
                }
            }
        }
    }
    private (object Key, Action Flush, long Stamp)? Next()
    {
        lock (_lock)
        {
            foreach ((object key, (Action flush, long stamp)) in _flushes)
            {
                return (key, flush, stamp);
            }
            return null;
        }
    }
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            _flushes.Clear();
        }
        _ = Interlocked.CompareExchange(ref s_open, null, this);
    }
}
