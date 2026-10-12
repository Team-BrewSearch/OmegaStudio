using System.Buffers.Binary;
using System.Text;
using Omega.Plugins;
namespace Harugeki.Formats;
public sealed class RingTexture
{
    public const int HeaderSize = 0x40;
    private const int MaxDimension = 16384;
    private const long MaxPixelCount = 256L * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "ringtex2"u8;
    public string Name { get; set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    public byte[] HeaderTail { get; init; } = new byte[HeaderSize];
    public static bool IsTexture(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= HeaderSize && blob[..8].SequenceEqual(Magic);
    }
    public static RingTexture Parse(ReadOnlySpan<byte> blob)
    {
        if (!IsTexture(blob))
        {
            throw new ArgumentException("not a ringtex2 blob");
        }
        int w = (int)BinaryRead.U32(blob, 0x2C);
        int h = (int)BinaryRead.U32(blob, 0x30);
        long need = ValidateDimensions(w, h);
        return HeaderSize + need > blob.Length
            ? throw new ArgumentException(
                $"Texture blob {blob.Length} bytes too small for {w}x{h} RGBA8 data ({HeaderSize + need} bytes needed)")
            : new RingTexture
            {
                Name = BinaryRead.Name(blob, 0x10, 16),
                Width = w,
                Height = h,
                Pixels = blob.Slice(HeaderSize, (int)need).ToArray(),
                HeaderTail = blob[..HeaderSize].ToArray(),
            };
    }
    public void SetPixels(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        long need = ValidateDimensions(width, height);
        if (need != rgba.Length)
        {
            throw new ArgumentException(
                $"{width}x{height} RGBA8 needs {need} bytes but got {rgba.Length}", nameof(rgba));
        }
        Width = width;
        Height = height;
        Pixels = rgba;
    }
    public static int EncodedSize(ReadOnlySpan<byte> blob)
    {
        if (!IsTexture(blob))
        {
            throw new ArgumentException("not a ringtex2 blob", nameof(blob));
        }
        long pixels = ValidateDimensions(
            (int)BinaryRead.U32(blob, 0x2C), (int)BinaryRead.U32(blob, 0x30));
        return HeaderSize + (int)pixels;
    }
    private static long ValidateDimensions(int width, int height)
    {
        if (width <= 0 || width > MaxDimension || height <= 0 || height > MaxDimension)
        {
            throw new ArgumentException(
                $"Texture dimensions {width}x{height} out of valid range (1-{MaxDimension})");
        }
        long pixelCount = (long)width * height;
        return pixelCount > MaxPixelCount
            ? throw new ArgumentException(
                $"Texture pixel count {pixelCount} exceeds maximum {MaxPixelCount}")
            : pixelCount * 4;
    }
    public byte[] Build()
    {
        if ((long)Width * Height * 4 != Pixels.Length)
        {
            throw new ArgumentException("pixel buffer does not match the dimensions");
        }
        byte[] result = new byte[HeaderSize + Pixels.Length];
        HeaderTail.AsSpan(0, HeaderSize).CopyTo(result);
        Magic.CopyTo(result.AsSpan(0));
        result.AsSpan(0x10, 16).Clear();
        Encoding.ASCII.GetBytes(Name.Length > 16 ? Name[..16] : Name).CopyTo(result.AsSpan(0x10));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x2C), (uint)Width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x30), (uint)Height);
        Pixels.CopyTo(result.AsSpan(HeaderSize));
        return result;
    }
    public override string ToString()
    {
        return $"{Name} {Width}x{Height}";
    }
}
