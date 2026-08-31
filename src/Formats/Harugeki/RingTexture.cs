using Harugeki.Formats.Binary;
using System.Buffers.Binary;
using System.Text;

namespace Harugeki.Formats;

/// <summary>
/// The <c>ringtex2</c> texture: a 64-byte header then uncompressed pixels.
///
/// <code>
/// 0x00 char[16]  "ringtex2"
/// 0x10 char[16]  asset name
/// 0x2C u32       width
/// 0x30 u32       height
/// 0x40 u8[w*h*4] RGBA8, top-down, no row padding
/// </code>
///
/// The channel order is <b>R,G,B,A</b> — not the B,G,R,A a D3D9 A8R8G8B8
/// surface would use in memory. Measured over the 140 face textures the
/// per-channel means are 220/163/111, a skin tone; read as B,G,R the same bytes
/// would be a saturated sky blue.
/// </summary>
public sealed class RingTexture
{
    public const int HeaderSize = 0x40;
    private const int MaxDimension = 16384;  // 16K, supported by modern GPUs
    private const long MaxPixelCount = 256L * 1024 * 1024;  // 256M pixels
    private static ReadOnlySpan<byte> Magic => "ringtex2"u8;

    public string Name { get; set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Pixels exactly as stored: RGBA8, top-down.</summary>
    public byte[] Pixels { get; private set; } = [];

    /// <summary>Header bytes we do not interpret, preserved for round-tripping.</summary>
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

    /// <summary>
    /// Swaps in a new image, which may be a different size than the one this
    /// texture was parsed from.
    ///
    /// <para>
    /// Dimensions travel with the pixels because the two only mean anything
    /// together: setting them separately would leave the texture briefly claiming a
    /// size its buffer does not have, and <see cref="Build"/> would then refuse to
    /// encode it. Growing or shrinking a texture is what makes the encoded blob
    /// change length, which is what the model splice and the archive rebuild exist
    /// to absorb.
    /// </para>
    /// </summary>
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

    /// <summary>Checks a size is one we are willing to store, and returns its RGBA8 byte count.</summary>
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

    /// <summary>
    /// Rebuilds the blob. The original header is reused so every field we do not
    /// interpret survives untouched; only the name and dimensions are rewritten.
    /// </summary>
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

    // No SwapRedBlue or ToBgra here any more: the RGBA-to-BGRA swizzle is the host's, in
    // ImageData.SwapRedBlue, and both plugins had grown their own identical copy. A texture
    // crosses the seam as RGBA8 and never learns what draws it.

    public override string ToString()
    {
        return $"{Name} {Width}x{Height}";
    }
}
