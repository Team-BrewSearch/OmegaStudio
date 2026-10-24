using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Omega.Plugins;
namespace OmegaStudio.Services;
public sealed class ImageIO : IImageCodec
{
    private const int BmpFileHeader = 14;
    private const int BmpInfoHeader = 108;
    private const int BiBitfields = 3;
    private const int PixelsPerMetre = 2835;
    private const uint LcsSrgb = 0x73524742;
    private const int JpegQuality = 92;
    public const long MaxPixels = 1L << 26;
    private static readonly string[] s_writable = [".png", ".jpg", ".jpeg", ".bmp"];
    public static ImageIO Instance { get; } = new();
    public static WriteableBitmap ToBitmap(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        WriteableBitmap bitmap = new(
            new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer frame = bitmap.Lock();
        byte[] row = new byte[image.Width * 4];
        for (int y = 0; y < image.Height; y++)
        {
            image.Rgba.AsSpan(y * row.Length, row.Length).CopyTo(row);
            ImageData.SwapRedBlueInPlace(row);
            Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), row.Length);
        }
        return bitmap;
    }
    public void Save(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicSave.Write(path, Encode(image, Path.GetExtension(path)));
    }
    public byte[] Encode(ImageData image, string extension)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Normalise(extension) switch
        {
            ".png" => EncodePng(image),
            ".jpg" => EncodeWith(image, new JpegBitmapEncoderOptions { Quality = JpegQuality }),
            ".bmp" => EncodeBmp(image),
            _ => throw new NotSupportedException(
                $"'{extension}' is not an image format this build can write; " +
                $"use one of {string.Join(", ", s_writable)}."),
        };
    }
    public IReadOnlyList<string> WritableExtensions => s_writable;
    public static byte[] EncodePng(ImageData image)
    {
        return EncodeWith(image, new PngBitmapEncoderOptions());
    }
    private static string Normalise(string extension)
    {
        string found = extension ?? string.Empty;
        return found.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || found.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg"
            : found.Equals(".png", StringComparison.OrdinalIgnoreCase) ? ".png"
            : found.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? ".bmp"
            : string.Empty;
    }
    private static byte[] EncodeWith(ImageData image, BitmapEncoderOptions options)
    {
        using WriteableBitmap bitmap = ToBitmap(image);
        using MemoryStream stream = new();
        bitmap.Save(stream, options);
        return stream.ToArray();
    }
    private static byte[] EncodeBmp(ImageData image)
    {
        int stride = image.Width * 4;
        byte[] file = new byte[BmpFileHeader + BmpInfoHeader + (stride * image.Height)];
        Span<byte> span = file;
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], BmpFileHeader + BmpInfoHeader);
        Span<byte> header = span[BmpFileHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(header, BmpInfoHeader);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], BiBitfields);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * image.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], PixelsPerMetre);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], PixelsPerMetre);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], LcsSrgb);
        Span<byte> pixels = span[(BmpFileHeader + BmpInfoHeader)..];
        for (int y = 0; y < image.Height; y++)
        {
            Span<byte> row = pixels.Slice((image.Height - 1 - y) * stride, stride);
            image.Rgba.AsSpan(y * stride, stride).CopyTo(row);
            ImageData.SwapRedBlueInPlace(row);
        }
        return file;
    }
    public ImageData Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] head = new byte[64 * 1024];
        int read;
        using (FileStream file = File.OpenRead(path))
        {
            read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        RequireSize(Dimensions(head.AsSpan(0, read)), Path.GetFileName(path));
        using Bitmap bitmap = new(path);
        return FromBitmap(bitmap, Path.GetFileNameWithoutExtension(path));
    }
    public ImageData Load(ReadOnlySpan<byte> bytes, string name = "")
    {
        RequireSize(Dimensions(bytes), name);
        using MemoryStream stream = new(bytes.ToArray());
        using Bitmap bitmap = new(stream);
        return FromBitmap(bitmap, name);
    }
    private static ImageData FromBitmap(Bitmap bitmap, string name)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"image has no pixels ({width}x{height})");
        }
        RequireSize((width, height), name);
        byte[] pixels = new byte[checked(width * height * 4)];
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }
        ImageData.SwapRedBlueInPlace(pixels);
        return ImageData.Create(name, width, height, pixels);
    }
    private static void RequireSize((long Width, long Height)? size, string name)
    {
        if (size is not { } found)
        {
            return;
        }
        if (found.Width > ImageData.MaxDimension || found.Height > ImageData.MaxDimension
            || found.Width * found.Height > MaxPixels)
        {
            throw new InvalidDataException(
                $"{(string.IsNullOrEmpty(name) ? "the image" : name)} is {found.Width}x{found.Height}, past the "
                + $"{ImageData.MaxDimension} pixels a side and {MaxPixels:N0} pixels in all this build decodes");
        }
    }
    public static (long Width, long Height)? Dimensions(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 24 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return (BinaryPrimitives.ReadUInt32BigEndian(head[16..]), BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
        }
        if (head.Length >= 26 && head[0] == 'B' && head[1] == 'M')
        {
            return (Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(head[18..])), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
        }
        if (head.Length >= 10 && head[..3].SequenceEqual("GIF"u8))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));
        }
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8)
        {
            for (int at = 2; at + 9 <= head.Length && head[at] == 0xFF;)
            {
                byte marker = head[at + 1];
                int length = BinaryPrimitives.ReadUInt16BigEndian(head[(at + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return (BinaryPrimitives.ReadUInt16BigEndian(head[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(head[(at + 5)..]));
                }
                at += 2 + length;
            }
        }
        return null;
    }
    byte[] IImageCodec.EncodePng(ImageData image)
    {
        return EncodePng(image);
    }
    public static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "asset";
        }
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] safe = [.. name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)];
        safe = [.. safe.Select(c => c is '/' or '\\' ? '_' : c)];
        string result = new string(safe).Trim().TrimStart('.');
        return result.Length == 0
            || result is "." or ".."
            || result.Contains("..", StringComparison.Ordinal)
            || result.Contains(':', StringComparison.Ordinal)
            ? "asset"
            : result;
    }
}
