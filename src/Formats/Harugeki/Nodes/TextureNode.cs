using Harugeki.Formats.Scene;
using Omega.Plugins;
namespace Harugeki.Formats.Nodes;

internal sealed class TextureNode : SlotNode, IImageAsset, IDescribable
{
    private readonly RingTexture _texture;
    private readonly RingModel? _owner;
    private readonly int _indexInOwner;
    private byte[]? _encoded;
    public TextureNode(RingNode slot, string header)
        : this(slot, header, RingTexture.Parse(slot.Span), null, 0)
    {
    }
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
    public override ReadOnlySpan<byte> Data =>
        _owner is null ? Slot.Span : _encoded ??= _texture.Build();
    public override int Size => _owner is null ? Slot.Size : RingTexture.HeaderSize + _texture.Pixels.Length;
    public override bool CanReplaceBytes => _owner is null;
    protected override string DescribeSize()
    {
        return $"{_texture.Width}x{_texture.Height}";
    }
    public ImageData Image => ModelScene.ToImage(_texture);
    public bool CanReplaceImage => true;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
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
