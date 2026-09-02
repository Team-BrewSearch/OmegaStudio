using Omega.Plugins;
using Texim.Colors;
using Texim.Images;
using Texim.Palettes;
using Texim.Pixels;
using Yarhl.FileSystem;
namespace Nintendo.Formats.Nodes;

internal sealed class BannerIconNode : AssetNode, IImageAsset, IDescribable
{
    private const int TransparentIndex = 0;
    private readonly NdsRom _rom;
    private readonly Node _node;
    private IndexedPaletteImage _icon;
    public BannerIconNode(NdsRom rom, Node node, string path, IndexedPaletteImage icon)
        : base(node.Name, path, AssetKinds.Texture, $"{icon.Width}x{icon.Height}")
    {
        _rom = rom;
        _node = node;
        _icon = icon;
    }
    public ImageData Image { get => field ??= Decode(); private set; }
    public bool CanReplaceImage => true;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _icon = IconEncoder.Write(_icon, image);
        _ = _node.ChangeFormat(_icon);
        Image = Decode();
        _rom.MarkChanged();
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Size", $"{_icon.Width}x{_icon.Height}");
        yield return PropertyRow.Of("Palettes", _icon.Palettes.Count);
        yield return PropertyRow.Of(
            "Colours", _icon.Palettes.Count > 0 ? _icon.Palettes[0].Colors.Count : 0);
    }
    private ImageData Decode()
    {
        int width = _icon.Width;
        int height = _icon.Height;
        long need = (long)width * height;
        if (_icon.Pixels.Length < need)
        {
            throw new InvalidDataException(
                $"'{Name}' is {width}x{height} but holds only {_icon.Pixels.Length} pixels");
        }
        byte[] rgba = new byte[need * 4];
        for (int i = 0; i < need; i++)
        {
            IndexedPixel pixel = _icon.Pixels[i];
            Rgb colour = Colour(pixel);
            int at = i * 4;
            rgba[at] = colour.Red;
            rgba[at + 1] = colour.Green;
            rgba[at + 2] = colour.Blue;
            rgba[at + 3] = pixel.ColorIndex == TransparentIndex ? (byte)0 : byte.MaxValue;
        }
        return ImageData.Create(FileStem, width, height, rgba, "4-bit palette");
    }
    private Rgb Colour(IndexedPixel pixel)
    {
        if (pixel.PaletteIndex >= _icon.Palettes.Count)
        {
            throw new InvalidDataException(
                $"'{Name}' asks for palette {pixel.PaletteIndex}, past the {_icon.Palettes.Count} it has");
        }
        IPalette palette = _icon.Palettes[pixel.PaletteIndex];
        return pixel.ColorIndex < palette.Colors.Count
            ? palette.Colors[pixel.ColorIndex]
            : throw new InvalidDataException(
                $"'{Name}' asks for colour {pixel.ColorIndex}, past the {palette.Colors.Count} in its palette");
    }
}
