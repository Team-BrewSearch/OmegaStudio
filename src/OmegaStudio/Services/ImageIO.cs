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
    private static readonly string[] s_writable = [".png", ".jpg", ".jpeg", ".bmp"];
    public static ImageIO Instance { get; } = new();
    public static WriteableBitmap ToBitmap(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        WriteableBitmap bitmap = new(
            new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        byte[] bgra = ImageData.SwapRedBlue(image.Rgba);
        using ILockedFramebuffer frame = bitmap.Lock();
        int rowBytes = image.Width * 4;
        for (int y = 0; y < image.Height; y++)
        {
            Marshal.Copy(bgra, y * rowBytes, frame.Address + (y * frame.RowBytes), rowBytes);
        }
        return bitmap;
    }
    public void Save(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(image, Path.GetExtension(path)));
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
            int source = y * stride;
            int destination = (image.Height - 1 - y) * stride;
            for (int x = 0; x < stride; x += 4)
            {
                pixels[destination + x + 0] = image.Rgba[source + x + 2];
                pixels[destination + x + 1] = image.Rgba[source + x + 1];
                pixels[destination + x + 2] = image.Rgba[source + x + 0];
                pixels[destination + x + 3] = image.Rgba[source + x + 3];
            }
        }
        return file;
    }
    public ImageData Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using Bitmap bitmap = new(path);
        return FromBitmap(bitmap, Path.GetFileNameWithoutExtension(path));
    }
    public ImageData Load(ReadOnlySpan<byte> bytes, string name = "")
    {
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
        byte[] bgra = new byte[width * height * 4];
        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bgra.Length, width * 4);
        }
        finally
        {
            handle.Free();
        }
        return ImageData.Create(name, width, height, ImageData.SwapRedBlue(bgra));
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
