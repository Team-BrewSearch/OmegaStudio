using Harugeki.Formats.Scene;
using Omega.Plugins;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// A <c>ringtex2</c> texture, either as its own archive slot or embedded inside a
/// model.
///
/// <para>
/// One class for both because everything above it — the preview, the PNG export, the
/// PNG replacement — is identical; only the route the new bytes take back into the
/// archive differs. A slot texture is replaced whole; an embedded one is spliced at a
/// known offset so the surrounding model is untouched.
/// </para>
/// </summary>
internal sealed class TextureNode : SlotNode, IImageAsset, IDescribable
{
    private readonly RingTexture _texture;
    private readonly RingModel? _owner;
    private readonly int _indexInOwner;

    /// <summary>
    /// An embedded texture's encoded blob, kept between reads. <see cref="Data"/> is on the
    /// path recognition takes for every row, and re-encoding header plus pixels on each access
    /// made expanding a folder of textures cost one full encode per row per look.
    /// </summary>
    private byte[]? _encoded;

    /// <summary>A texture that is an archive slot in its own right.</summary>
    public TextureNode(RingNode slot, string header)
        : this(slot, header, RingTexture.Parse(slot.Span), null, 0)
    {
    }

    /// <summary>
    /// A texture embedded in <paramref name="owner"/>'s blob. A factory rather than a
    /// second constructor: the two would have the same signature once the owner's
    /// nullability is erased.
    /// </summary>
    public static TextureNode Embedded(
        RingNode slot, string header, RingTexture texture, RingModel owner, int index)
    {
        return new TextureNode(slot, header, texture, owner, index);
    }

    private TextureNode(RingNode slot, string header, RingTexture texture, RingModel? owner, int index)
        : base(slot, Label(header, texture), detail: $"{texture.Width}x{texture.Height}")
    {
        _texture = texture;
        _owner = owner;
        _indexInOwner = index;
    }

    private static string Label(string header, RingTexture texture)
    {
        return string.IsNullOrEmpty(texture.Name) ? header : $"{header} {texture.Name}";
    }

    public override string Extension => ".ringtex2";

    /// <summary>
    /// The encoded texture, not the slot it lives in.
    ///
    /// <para>
    /// For an embedded texture these differ: the slot holds the whole model. Handing
    /// back the model there would make "Extract Raw" on a texture silently write the
    /// model out under a texture's filename, which is what the version of this that
    /// forwarded to the slot actually did.
    /// </para>
    /// </summary>
    public override ReadOnlySpan<byte> Data =>
        _owner is null ? Slot.Span : _encoded ??= _texture.Build();

    public override int Size => _owner is null ? Slot.Size : RingTexture.HeaderSize + _texture.Pixels.Length;

    /// <summary>
    /// Raw replacement is offered only for a texture that owns its slot. Splicing
    /// arbitrary bytes into the middle of a model needs them to be a valid
    /// <c>ringtex2</c> of a known length, which is what "Replace from PNG" does
    /// properly and a raw byte swap cannot check.
    /// </summary>
    public override bool CanReplaceBytes => _owner is null;

    protected override string DescribeSize()
    {
        return $"{_texture.Width}x{_texture.Height}";
    }

    public ImageData Image => ModelScene.ToImage(_texture);

    public bool CanReplaceImage => true;

    /// <summary>
    /// Swaps in a new image, at any size.
    ///
    /// <para>
    /// The live <see cref="RingTexture"/> is mutated rather than replaced, because the
    /// outliner row, the preview and the owning model all hold this same instance;
    /// handing back a new one would leave every one of them showing the old image. It
    /// is updated only once the bytes are safely written, so a rejected replacement
    /// leaves the texture exactly as it was.
    /// </para>
    /// </summary>
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Encode against a throwaway copy so a failed write cannot leave the live
        // texture describing an image the archive does not contain.
        RingTexture staged = RingTexture.Parse(_texture.Build());
        staged.SetPixels(image.Width, image.Height, image.Rgba);
        byte[] encoded = staged.Build();

        if (_owner is null)
        {
            Slot.Replace(encoded);
        }
        else
        {
            SpliceIntoModel(encoded);
        }

        _texture.SetPixels(image.Width, image.Height, image.Rgba);
        _encoded = null;
        Detail = $"{_texture.Width}x{_texture.Height}";
    }

    /// <summary>
    /// Rewrites the owning model's blob around the new texture and re-reads where each
    /// embedded texture ended up, so a later replacement of a sibling still splices at
    /// the right offset.
    /// </summary>
    private void SpliceIntoModel(byte[] encoded)
    {
        RingModel owner = _owner!;

        if (_indexInOwner < 0 || _indexInOwner >= owner.TextureSpans.Count)
        {
            throw new InvalidOperationException(
                $"Texture index {_indexInOwner} is out of range for this model.");
        }

        (int offset, _) = owner.TextureSpans[_indexInOwner];
        Slot.Replace(ModelTextures.Replace(Slot.Span, offset, encoded));

        int at = owner.TextureSpans[0].Offset;
        for (int i = 0; i < owner.TextureSpans.Count; i++)
        {
            int size = i == _indexInOwner
                ? encoded.Length
                : RingTexture.HeaderSize + (owner.Textures[i].Width * owner.Textures[i].Height * 4);

            owner.TextureSpans[i] = (at, size);
            at += size;
        }
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _texture.Name);
        yield return PropertyRow.Of("Format", "RGBA8 uncompressed");
        yield return PropertyRow.Of("Size", $"{_texture.Width} x {_texture.Height}");

        if (_owner is not null)
        {
            yield return PropertyRow.Of("Embedded in", "model");
            yield return PropertyRow.Of("Texture index", _indexInOwner);
        }

        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
