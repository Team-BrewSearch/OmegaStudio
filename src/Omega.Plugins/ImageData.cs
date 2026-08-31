namespace Omega.Plugins;
/// <summary>A decoded picture: RGBA8, top-down, four bytes per pixel and no row padding.</summary>
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
    /// <summary>Largest width or height accepted, matching what current GPUs will sample.</summary>
    public const int MaxDimension = 16384;
    /// <summary>The image&apos;s own name, used to suggest a filename on export.</summary>
    public string Name { get; }
    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>The pixels, width times height times four bytes long.</summary>
    public byte[] Rgba { get; }
    /// <summary>How the source format stored this image, for the Properties pane. Descriptive only.</summary>
    public string FormatLabel { get; }
    /// <summary>Creates an image, rejecting dimensions or a buffer length that do not agree.</summary>
    /// <param name="name">The image&apos;s name.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="rgba">The pixels, four bytes each.</param>
    /// <param name="formatLabel">How the source format stored the image.</param>
    /// <returns>The image.</returns>
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
    /// <summary>Exchanges the red and blue channels of a 32-bit image, turning RGBA into BGRA or back. Applying it twice returns the original.</summary>
    /// <param name="pixels">The pixels to convert; a trailing partial pixel is left zeroed.</param>
    /// <returns>A new buffer the same length as the input.</returns>
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
    /// <summary>The name, size and format, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} {Width}x{Height} {FormatLabel}";
    }
}
