using System.Numerics;
using System.Runtime.InteropServices;
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
        pixels[..(pixels.Length & ~3)].CopyTo(destination);
        SwapRedBlueInPlace(destination);
        return destination;
    }
    public static void SwapRedBlueInPlace(Span<byte> pixels)
    {
        uint keep = BitConverter.IsLittleEndian ? 0xFF00FF00u : 0x00FF00FFu;
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(pixels);
        for (int i = 0; i < words.Length; i++)
        {
            uint p = words[i];
            words[i] = (p & keep) | BitOperations.RotateLeft(p & ~keep, 16);
        }
    }
    public override string ToString()
    {
        return $"{Name} {Width}x{Height} {FormatLabel}";
    }
}
