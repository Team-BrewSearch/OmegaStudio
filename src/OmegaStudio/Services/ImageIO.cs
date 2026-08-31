using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Omega.Plugins;
using System.Runtime.InteropServices;
namespace OmegaStudio.Services;

public sealed class ImageIO : IImageCodec
{
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
    public void SavePng(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using WriteableBitmap bitmap = ToBitmap(image);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
    public static byte[] EncodePng(ImageData image)
    {
        using WriteableBitmap bitmap = ToBitmap(image);
        using MemoryStream stream = new();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
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
        string result = new string(safe).Trim();
        return result.Length == 0 ? "asset" : result;
    }
}
