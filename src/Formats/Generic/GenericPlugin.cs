using Generic.Formats;
using Omega.Plugins;
[assembly: OmegaPlugin(typeof(GenericPlugin))]
namespace Generic.Formats;
public sealed class GenericPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "generic",
        DisplayName = "Standard Formats",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Recognises OGG, WAV, PNG, JPEG, GIF and BMP payloads inside any archive.",
    };
    public IEnumerable<IAssetRecognizer> Recognizers { get; } =
    [
        new AudioRecognizer(),
        new ImageRecognizer(),
    ];
}
public sealed class AudioRecognizer : IAssetRecognizer
{
    public string Id => "generic.audio";
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return Magic.IsAudio(data);
    }
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new AudioView(node);
    }
}
public sealed class ImageRecognizer : IAssetRecognizer
{
    public string Id => "generic.image";
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return Magic.IsImage(data);
    }
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new ImageView(node, host);
    }
}
internal sealed class AudioView(IAssetNode inner) : RecognizedAsset(inner), IAudioAsset
{
    public override string Kind => AssetKinds.Audio;
    public override string Extension => Magic.AudioExtension(Data);
    public ReadOnlySpan<byte> EncodedAudio => Data;
    public override IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Content", Extension == ".ogg" ? "OGG Vorbis" : "WAV");
        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
}
internal sealed class ImageView(IAssetNode inner, IHostServices host)
    : RecognizedAsset(inner), IImageAsset
{
    public override string Kind => AssetKinds.Texture;
    public override string Extension => Magic.ImageExtension(Data);
    public ImageData Image { get => field ??= host.Images.Load(Data, Name); private set; }
    public bool CanReplaceImage => CanReplaceBytes && Magic.IsPng(Data);
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ReplaceBytes(host.Images.EncodePng(image));
        Image = image;
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Content", Magic.ImageFormat(Data));
        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
}
