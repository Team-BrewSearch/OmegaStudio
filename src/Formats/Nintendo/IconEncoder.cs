using Omega.Plugins;
using System.Collections.ObjectModel;
using Texim.Colors;
using Texim.Images;
using Texim.Images.Quantization;
using Texim.Palettes;
using Texim.Pixels;
namespace Nintendo.Formats;

internal static class IconEncoder
{
    public const int PaletteSize = 16;
    private const byte AlphaThreshold = 128;
    private static readonly Rgb Black = new(0, 0, 0);
    public static IndexedPaletteImage Write(IndexedPaletteImage icon, ImageData image)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width != icon.Width || image.Height != icon.Height)
        {
            throw new InvalidDataException(
                $"the banner icon is {icon.Width}x{icon.Height}, and this image is " +
                $"{image.Width}x{image.Height}; the console reads a fixed size");
        }
        int count = icon.Width * icon.Height;
        bool[] drawn = new bool[count];
        List<Rgb> opaque = [];
        for (int i = 0; i < count; i++)
        {
            int at = i * 4;
            drawn[i] = image.Rgba[at + 3] >= AlphaThreshold;
            if (drawn[i])
            {
                opaque.Add(Snap(image.Rgba[at], image.Rgba[at + 1], image.Rgba[at + 2]));
            }
        }
        (List<Rgb> colours, int[] indices) = Reduce(opaque);
        IndexedPixel[] pixels = new IndexedPixel[count];
        int next = 0;
        for (int i = 0; i < count; i++)
        {
            short index = drawn[i] ? (short)(indices[next++] + 1) : (short)0;
            pixels[i] = new IndexedPixel(index, byte.MaxValue, 0);
        }
        Collection<Rgb> palette = [Transparent(icon)];
        foreach (Rgb colour in colours)
        {
            palette.Add(colour);
        }
        while (palette.Count < PaletteSize)
        {
            palette.Add(Black);
        }
        IndexedPaletteImage replacement = new()
        {
            Width = icon.Width,
            Height = icon.Height,
            Pixels = pixels,
        };
        replacement.Palettes.Add(new Palette(palette));
        return replacement;
    }
    private static (List<Rgb> Colours, int[] Indices) Reduce(List<Rgb> opaque)
    {
        int[] indices = new int[opaque.Count];
        List<Rgb> exact = [];
        Dictionary<Rgb, int> seen = [];
        for (int i = 0; i < opaque.Count; i++)
        {
            if (!seen.TryGetValue(opaque[i], out int index))
            {
                if (exact.Count == PaletteSize - 1)
                {
                    return Quantize(opaque);
                }
                index = exact.Count;
                seen[opaque[i]] = index;
                exact.Add(opaque[i]);
            }
            indices[i] = index;
        }
        return (exact, indices);
    }
    private static (List<Rgb> Colours, int[] Indices) Quantize(List<Rgb> opaque)
    {
        QuantizationResult result = new MedianCutQuantization(PaletteSize).Quantize([.. opaque]);
        if (result.Palettes.Palettes.Count == 0)
        {
            throw new InvalidDataException("the image could not be reduced to the banner's palette");
        }
        List<Rgb> chosen = [.. result.Palettes.Palettes[0].Colors
            .Select(colour => Snap(colour.Red, colour.Green, colour.Blue))];
        int[] indices = new int[opaque.Count];
        int[] uses = new int[chosen.Count];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = result.Pixels[i].ColorIndex;
            uses[indices[i]]++;
        }
        while (uses.Count(use => use > 0) > PaletteSize - 1)
        {
            Merge(chosen, indices, uses);
        }
        return Compact(chosen, indices, uses);
    }
    private static void Merge(List<Rgb> colours, int[] indices, int[] uses)
    {
        int rarest = -1;
        for (int i = 0; i < uses.Length; i++)
        {
            if (uses[i] > 0 && (rarest < 0 || uses[i] < uses[rarest]))
            {
                rarest = i;
            }
        }
        int nearest = -1;
        long best = long.MaxValue;
        for (int i = 0; i < uses.Length; i++)
        {
            if (i == rarest || uses[i] == 0)
            {
                continue;
            }
            long distance = Distance(colours[rarest], colours[i]);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] == rarest)
            {
                indices[i] = nearest;
            }
        }
        uses[nearest] += uses[rarest];
        uses[rarest] = 0;
    }
    private static (List<Rgb> Colours, int[] Indices) Compact(
        List<Rgb> colours, int[] indices, int[] uses)
    {
        int[] moved = new int[colours.Count];
        List<Rgb> kept = [];
        for (int i = 0; i < colours.Count; i++)
        {
            moved[i] = uses[i] > 0 ? kept.Count : 0;
            if (uses[i] > 0)
            {
                kept.Add(colours[i]);
            }
        }
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = moved[indices[i]];
        }
        return (kept, indices);
    }
    private static long Distance(Rgb left, Rgb right)
    {
        long red = left.Red - right.Red;
        long green = left.Green - right.Green;
        long blue = left.Blue - right.Blue;
        return (red * red) + (green * green) + (blue * blue);
    }
    private static Rgb Transparent(IndexedPaletteImage icon)
    {
        return icon.Palettes.Count > 0 && icon.Palettes[0].Colors.Count > 0
            ? icon.Palettes[0].Colors[0]
            : Black;
    }
    private static Rgb Snap(byte red, byte green, byte blue)
    {
        return new Rgb(Five(red), Five(green), Five(blue));
    }
    private static byte Five(byte value)
    {
        return (byte)(value & 0xF8);
    }
}
