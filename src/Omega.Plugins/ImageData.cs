namespace Omega.Plugins;

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
    public const int MaxDimension = 16384;
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
    public string FormatLabel { get; }
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
    public override string ToString()
    {
        return $"{Name} {Width}x{Height} {FormatLabel}";
    }
}
