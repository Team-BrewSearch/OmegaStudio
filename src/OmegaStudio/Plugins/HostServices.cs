using Omega.Plugins;
using OmegaStudio.Services;
using OmegaStudio.Services.Gltf;

namespace OmegaStudio.Plugins;

/// <summary>
/// The host's side of <see cref="IHostServices"/>: what a plugin is lent for the
/// lifetime of an archive.
///
/// <para>
/// One instance per open archive rather than a singleton, so the console line a
/// plugin writes can be attributed to it and so <see cref="PluginDirectory"/> points
/// at the right folder. The codecs behind it are stateless and shared.
/// </para>
/// </summary>
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

    /// <summary>
    /// Writes a line to the console, tagged with the plugin that wrote it. Several
    /// plugins share one console, and an unattributed message about a format the user
    /// is not looking at is just noise.
    /// </summary>
    public void Log(string message)
    {
        _log($"[{_prefix}] {message}");
    }
}
