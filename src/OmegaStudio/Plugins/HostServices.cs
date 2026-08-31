using Omega.Plugins;
using OmegaStudio.Services;
using OmegaStudio.Services.Gltf;
namespace OmegaStudio.Plugins;

internal sealed class HostServices : IHostServices
{
    private readonly Action<string> _log;
    private readonly string _prefix;
    private readonly AssetRecognition _recognition;
    public HostServices(LoadedPlugin plugin, Action<string> log, AssetRecognition? recognition = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _prefix = plugin.Info.DisplayName;
        _recognition = recognition ?? AssetRecognition.None;
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
}
