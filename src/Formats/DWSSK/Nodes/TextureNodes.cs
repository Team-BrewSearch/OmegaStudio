using DWSSK.Formats.Core;
using DWSSK.Formats.Textures;
using Omega.Plugins;
namespace DWSSK.Formats.Nodes;
internal sealed class HtxNode : SlotNode, IImageAsset
{
    private (int Revision, ImageData Image)? _image;
    public HtxNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Texture, Label(slot))
    {
    }
    private static string Label(Slot slot)
    {
        try
        {
            Htx t = Htx.Parse(slot.Data);
            (int w, int h, _) = t.Dimensions(slot.Platform);
            return $"{w}x{h} {t.Describe(slot.Platform)}";
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            return Sizes.Describe(slot.Data.Length);
        }
    }
    protected override string DescribeSize()
    {
        return Label(Slot);
    }
    public ImageData Image
    {
        get
        {
            if (_image is not { } cached || cached.Revision != Slot.Revision)
            {
                Htx t = Htx.Parse(Slot.Data);
                (byte[] rgba, int w, int h) = t.DecodeLevel(Slot.Platform, 0);
                _image = cached = (Slot.Revision, ImageData.Create(FileStem, w, h, rgba, t.Describe(Slot.Platform)));
            }
            return cached.Image;
        }
    }
    public bool CanReplaceImage => Slot.CanWrite;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] blob = Htx.FromImage(Slot.Data, Slot.Platform, image.Rgba, image.Width, image.Height);
        if (!ReferenceEquals(blob, Slot.Data))
        {
            Store(blob);
        }
    }
    protected override void Check(byte[] payload)
    {
        Htx t = Htx.Parse(payload);
        _ = t.Dimensions(Slot.Platform);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        Htx t = Htx.Parse(Slot.Data);
        (int w, int h, int mips) = t.Dimensions(Slot.Platform);
        yield return PropertyRow.Of("Size", $"{w} x {h}");
        yield return PropertyRow.Of("Format", t.Describe(Slot.Platform));
        yield return PropertyRow.Of("Mip levels", mips);
        yield return PropertyRow.Of("Encoding", t.IsLossless(Slot.Platform) ? "lossless: an unchanged PNG gives back the same bytes" : "lossy: an edited image is re-encoded");
        yield return PropertyRow.Of("Layout", Slot.Platform switch
        {
            Platform.PS3 => t.Format is 5 or 6 or 7 ? "linear DXT blocks" : "Morton swizzled",
            Platform.Xbox360 => "Xenos rows, 32-block pitch, 16/32-bit word swap",
            Platform.Wii => "GX tiles",
            _ => "CTPK, PICA200 8x8 Morton tiles, bottom-up",
        });
        if (Slot.Platform == Platform.N3ds)
        {
            Ctpk c = Pica.ParseCtpk(t.Data);
            yield return PropertyRow.Of("CTPK name", c.Name);
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
internal sealed class TplNode : AssetNode, IImageAsset, IDescribable, IBinaryAsset
{
    private readonly Func<byte[]> _tpl;
    private readonly Action<byte[]>? _store;
    private readonly int _index;
    public TplNode(Func<byte[]> tpl, Action<byte[]>? store, int index, string name, string path)
        : base(name, path, AssetKinds.Texture, string.Empty)
    {
        _tpl = tpl;
        _store = store;
        _index = index;
        TplImage im = Tpl.Images(tpl())[index];
        Detail = $"{im.Width}x{im.Height} {(im.Format < Tpl.Names.Length ? Tpl.Names[im.Format] : im.Format.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
    }
    private (TplImage Image, int Size) Info(byte[] b)
    {
        TplImage im = Tpl.Images(b)[_index];
        return (im, Tpl.Size(im.Format, im.Width, im.Height));
    }
    public ImageData Image
    {
        get
        {
            byte[] b = _tpl();
            (TplImage im, int size) = Info(b);
            byte[] px = Tpl.Decode(im.Format, Bin.Slice(b, im.DataOffset, im.DataOffset + size), im.Width, im.Height);
            return ImageData.Create(Name, im.Width, im.Height, px, $"GX {Tpl.Names[im.Format]}");
        }
    }
    public bool CanReplaceImage => _store is not null;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_store is null)
        {
            throw new InvalidOperationException("this texture cannot be written back");
        }
        byte[] b = (byte[])_tpl().Clone();
        (TplImage im, int size) = Info(b);
        if (image.Width != im.Width || image.Height != im.Height)
        {
            throw new InvalidDataException($"the image is {image.Width}x{image.Height}, the TPL image is {im.Width}x{im.Height}");
        }
        Tpl.Encode(im.Format, image.Rgba, im.Width, im.Height).CopyTo(b, im.DataOffset);
        _ = size;
        _store(b);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        (TplImage im, int size) = Info(_tpl());
        yield return PropertyRow.Of("Size", $"{im.Width} x {im.Height}");
        yield return PropertyRow.Of("GX format", Tpl.Names[im.Format]);
        yield return PropertyRow.Of("Data", $"0x{im.DataOffset:X}, {Sizes.Describe(size)}");
        yield return PropertyRow.Of("Encoding", "exact: an unchanged PNG gives back the same bytes");
    }
    public int Size => Info(_tpl()).Size;
    public ReadOnlySpan<byte> Data
    {
        get
        {
            byte[] b = _tpl();
            (TplImage im, int size) = Info(b);
            return Bin.Slice(b, im.DataOffset, im.DataOffset + size);
        }
    }
    public string Extension => ".gx";
    public bool CanReplaceBytes => false;
    public void ReplaceBytes(byte[] payload)
    {
        throw new InvalidOperationException("replace the image from a PNG instead");
    }
    public override string FileStem => Name;
}
