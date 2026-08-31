using Generic.Formats;
using Omega.Plugins;

[assembly: OmegaPlugin(typeof(GenericPlugin))]

namespace Generic.Formats;

/// <summary>
/// The standard formats every game archive ends up containing.
///
/// <para>
/// Unusual among plugins in that it opens nothing. It contributes recognisers, so an
/// OGG or a PNG sitting inside somebody else's archive gets a transport or a preview
/// without that archive's plugin knowing what either format looks like. Remove this
/// plugin and those payloads fall back to raw blobs — everything still opens, there is
/// just nothing to play or look at.
/// </para>
/// </summary>
public sealed class GenericPlugin : IFormatPlugin
{
    /// <inheritdoc/>
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "generic",
        DisplayName = "Standard Formats",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Recognises OGG, WAV, PNG, JPEG, GIF and BMP payloads inside any archive.",
    };

    /// <inheritdoc/>
    public IEnumerable<IAssetRecognizer> Recognizers { get; } =
    [
        new AudioRecognizer(),
        new ImageRecognizer(),
    ];
}

/// <summary>OGG Vorbis and WAV. The host decodes; this only says what the bytes are.</summary>
public sealed class AudioRecognizer : IAssetRecognizer
{
    /// <inheritdoc/>
    public string Id => "generic.audio";

    /// <inheritdoc/>
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return Magic.IsAudio(data);
    }

    /// <inheritdoc/>
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new AudioView(node);
    }
}

/// <summary>PNG, JPEG, GIF and BMP, decoded through the host's image codec.</summary>
public sealed class ImageRecognizer : IAssetRecognizer
{
    /// <inheritdoc/>
    public string Id => "generic.image";

    /// <inheritdoc/>
    public bool Claims(ReadOnlySpan<byte> data)
    {
        return Magic.IsImage(data);
    }

    /// <inheritdoc/>
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        return new ImageView(node, host);
    }
}

/// <summary>
/// An encoded clip. Earns the Audio pane and <b>Extract Audio…</b>.
///
/// <para>
/// This is the whole of what used to be a per-plugin audio node: the capability hands
/// over the bytes as stored and the host does the decoding, the transport and the
/// seeking.
/// </para>
/// </summary>
internal sealed class AudioView(IAssetNode inner) : RecognizedAsset(inner), IAudioAsset
{
    public override string Kind => AssetKinds.Audio;

    public override string Extension => Magic.AudioExtension(Data);

    public ReadOnlySpan<byte> EncodedAudio => Data;

    /// <summary>
    /// The container, and then whatever the wrapped node had to say. The host appends
    /// the decoded sample rate and duration itself, so there is no point repeating them.
    /// </summary>
    public override IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Content", Extension == ".ogg" ? "OGG Vorbis" : "WAV");

        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
}

/// <summary>
/// An encoded image. Earns the Texture pane, <b>Export PNG…</b> and, for a PNG,
/// <b>Replace from PNG…</b>.
/// </summary>
internal sealed class ImageView(IAssetNode inner, IHostServices host)
    : RecognizedAsset(inner), IImageAsset
{
    public override string Kind => AssetKinds.Texture;

    public override string Extension => Magic.ImageExtension(Data);

    /// <summary>
    /// Decoded on demand and kept. Recognition runs for every row in a folder, so
    /// decoding there would cost a thousand images to expand a folder of a thousand
    /// files; the host only reads this on selection.
    /// </summary>
    public ImageData Image { get => field ??= host.Images.Load(Data, Name); private set; }

    /// <summary>
    /// Only a PNG. Replacement re-encodes as PNG, so allowing it for a JPEG would
    /// quietly leave PNG bytes under a <c>.jpg</c> name.
    /// </summary>
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
