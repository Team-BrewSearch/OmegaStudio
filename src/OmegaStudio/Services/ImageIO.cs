using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Omega.Plugins;
using System.Runtime.InteropServices;

namespace OmegaStudio.Services;

/// <summary>
/// PNG in and out, using Avalonia's Skia-backed bitmaps — which is why this is the
/// host's job and not a plugin's. A plugin hands over an <see cref="ImageData"/>
/// and never learns what decoded it.
///
/// <para>
/// Images cross the plugin seam as RGBA8 and Avalonia's universally supported
/// layout is Bgra8888, so every conversion goes through
/// <see cref="ImageData.SwapRedBlue"/>, which serves both directions.
/// </para>
///
/// <para>
/// <b>A limitation worth knowing about.</b> The bitmaps are created
/// <see cref="AlphaFormat.Unpremul"/>, but Skia premultiplies anyway when it encodes
/// and decodes a PNG. Fully opaque pixels therefore survive an export and re-import
/// exactly, while a pixel with alpha below 255 comes back with its colour scaled by
/// that alpha — and the colour hidden behind a fully transparent pixel is lost
/// entirely. That is fine for the formats here, whose transparency is a colour key
/// rather than real blending, and it is why an importer must never identify a texture
/// by comparing pixels alone. Nothing pins this behaviour yet - a change to it would be a
/// surprise rather than a decision, which is the first thing a test project here should fix.
/// </para>
/// </summary>
public sealed class ImageIO : IImageCodec
{
    /// <summary>The one instance; it holds no state.</summary>
    public static ImageIO Instance { get; } = new();

    /// <summary>Renders an image into a bitmap the UI can show.</summary>
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

    /// <inheritdoc/>
    public void SavePng(ImageData image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using WriteableBitmap bitmap = ToBitmap(image);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>Encodes an image as PNG bytes, for embedding in a glTF.</summary>
    public static byte[] EncodePng(ImageData image)
    {
        using WriteableBitmap bitmap = ToBitmap(image);
        using MemoryStream stream = new();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>
    /// Loads an image file as RGBA8, at whatever size it happens to be.
    ///
    /// <para>
    /// Never resampled to fit the texture it is replacing. A replacement used to be
    /// refused unless the dimensions matched exactly; now the texture takes the
    /// image's size and the format rebuilds around the new length — or refuses, if
    /// it genuinely cannot.
    /// </para>
    /// </summary>
    public ImageData Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using Bitmap bitmap = new(path);
        return FromBitmap(bitmap, Path.GetFileNameWithoutExtension(path));
    }

    /// <inheritdoc/>
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

    /// <summary>
    /// A filename-safe version of <paramref name="name"/>, for suggesting one in a
    /// save dialog.
    /// </summary>
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
