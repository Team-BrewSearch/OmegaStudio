using Disgaea.Formats;
using Omega.Plugins;
[assembly: OmegaPlugin(typeof(DisgaeaPlugin))]
namespace Disgaea.Formats;
public sealed class DisgaeaPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "disgaea",
        DisplayName = "Disgaea DS",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "DSARC FL and DSARCIDX archives, DSEQ music, SWAR wave archives, LZ10 maps and models, IMY images, SWAV/STRM sound, SSEQ music and maps (3D view and editor) from Disgaea DS.",
    };
    public IEnumerable<IAssetRecognizer> Recognizers { get; } =
    [
        new PackRecognizer("disgaea.dsarc", Dsarc.Claims, (data, _) => Dsarc.Read(data)),
        new PackRecognizer("disgaea.dseq", Dseq.Claims, Dseq.Read),
        new PackRecognizer("disgaea.swar", Swar.Claims, Swar.Read),
        new PackRecognizer("disgaea.lz10", Lz10.Claims, Lz10Pack.Read, [".mpds", ".dsm"]),
        new ImyRecognizer(),
        new AudioRecognizer(),
    ];
    public IEnumerable<IContainerRecognizer> Containers { get; } = [new MapContainerRecognizer()];
}
internal delegate Pack PackReader(ReadOnlySpan<byte> data, string name);
public sealed class PackRecognizer : IAssetRecognizer
{
    private readonly Func<ReadOnlySpan<byte>, bool> _claims;
    private readonly PackReader _read;
    private readonly string[] _extensions;
    internal PackRecognizer(string id, Func<ReadOnlySpan<byte>, bool> claims, PackReader read, string[]? extensions = null)
    {
        Id = id;
        _claims = claims;
        _read = read;
        _extensions = extensions ?? [];
    }
    public string Id { get; }
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return _claims(data);
    }
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _extensions.Length == 0 || _extensions.Contains(Path.GetExtension(node.Name), StringComparer.OrdinalIgnoreCase)
            ? new PackView(node, host, _read)
            : node;
    }
}
public sealed class ImyRecognizer : IAssetRecognizer
{
    public string Id => "disgaea.imy";
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return Imy.Claims(data);
    }
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new ImyView(node);
    }
}
public sealed class AudioRecognizer : IAssetRecognizer
{
    public string Id => "disgaea.audio";
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return NitroAudio.Claims(data);
    }
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new WaveView(node);
    }
}
