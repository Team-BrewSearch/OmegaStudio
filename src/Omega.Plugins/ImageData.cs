namespace Omega.Plugins;

/// <summary>
/// A decoded image: RGBA8, top-down, no row padding.
///
/// <para>
/// RGBA rather than BGRA because that is what both games happen to store and what
/// PNG uses; the single swizzle into the layout the UI toolkit wants lives in the
/// host, so no plugin has to think about it.
/// </para>
/// </summary>
public sealed class ImageData
{
    private ImageData(string name, int width, int height, byte[] rgba, string formatLabel)
    {
        Name = name;
        Width = width;
        Height = height;
        Rgba = rgba;
        FormatLabel = formatLabel;
    }

    /// <summary>Largest dimension accepted, matching what current GPUs will sample.</summary>
    public const int MaxDimension = 16384;

    /// <summary>The image's own name, used to suggest a filename on export.</summary>
    public string Name { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Four bytes per pixel, <c>Width * Height * 4</c> long.</summary>
    public byte[] Rgba { get; }

    /// <summary>
    /// How the format stores this image, for the Properties pane — e.g.
    /// <c>RGBA8 uncompressed</c> or <c>5TX fmt3</c>. Purely descriptive.
    /// </summary>
    public string FormatLabel { get; }

    /// <summary>
    /// Creates an image, checking that the buffer matches the dimensions.
    ///
    /// <para>
    /// Validated on the way in rather than at the point of use: a mismatch here is a
    /// plugin bug, and catching it at the boundary names the plugin instead of
    /// surfacing later as a torn preview or a GPU upload of the wrong length.
    /// </para>
    /// </summary>
    public static ImageData Create(string name, int width, int height, byte[] rgba, string formatLabel = "RGBA8")
    {
        ArgumentNullException.ThrowIfNull(rgba);

        if (width <= 0 || width > MaxDimension || height <= 0 || height > MaxDimension)
        {
            throw new ArgumentException(
                $"Image dimensions {width}x{height} are outside the supported range (1-{MaxDimension}).");
        }

        long need = (long)width * height * 4;
        return need != rgba.Length
            ? throw new ArgumentException(
                $"{width}x{height} RGBA8 needs {need} bytes but got {rgba.Length}.", nameof(rgba))
            : new ImageData(name ?? string.Empty, width, height, rgba, formatLabel);
    }

    /// <summary>
    /// Swaps the red and blue channels of a 32-bit image, converting RGBA to BGRA or
    /// back. One method covers both directions because the swizzle is an involution:
    /// applying it twice returns the original.
    /// </summary>
    public static byte[] SwapRedBlue(ReadOnlySpan<byte> pixels)
    {
        byte[] destination = new byte[pixels.Length];
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            destination[i + 0] = pixels[i + 2];
            destination[i + 1] = pixels[i + 1];
            destination[i + 2] = pixels[i + 0];
            destination[i + 3] = pixels[i + 3];
        }

        return destination;
    }

    /// <summary>The image's name, size and format, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} {Width}x{Height} {FormatLabel}";
    }
}
