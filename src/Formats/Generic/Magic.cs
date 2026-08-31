namespace Generic.Formats;

/// <summary>
/// The file signatures this plugin knows, and the extension each implies.
///
/// <para>
/// One table, because the two questions a recogniser is asked — "are these bytes
/// yours?" and "what should an extract call them?" — have the same answer and used to
/// be answered by two separate copies of the same magic numbers.
/// </para>
/// </summary>
internal static class Magic
{
    /// <summary>A signature: the bytes, where they sit, and what the file is.</summary>
    private readonly record struct Signature(byte[] Bytes, int Offset, string Extension);

    private static readonly Signature[] s_audio =
    [
        new("OggS"u8.ToArray(), 0, ".ogg"),
        new("RIFF"u8.ToArray(), 0, ".wav"),   // narrowed to WAVE below
    ];

    private static readonly Signature[] s_images =
    [
        new([0x89, (byte)'P', (byte)'N', (byte)'G'], 0, ".png"),
        new([0xFF, 0xD8, 0xFF], 0, ".jpg"),
        new("GIF8"u8.ToArray(), 0, ".gif"),
        new("BM"u8.ToArray(), 0, ".bmp"),
    ];

    public static bool IsAudio(ReadOnlySpan<byte> data)
    {
        return Match(s_audio, data) is not null && (!StartsWith(data, "RIFF"u8) || IsWave(data));
    }

    public static bool IsImage(ReadOnlySpan<byte> data)
    {
        return Match(s_images, data) is not null;
    }

    /// <summary>Whether these bytes are a PNG, the one image format this plugin writes.</summary>
    public static bool IsPng(ReadOnlySpan<byte> data)
    {
        return StartsWith(data, [0x89, (byte)'P', (byte)'N', (byte)'G']);
    }

    public static string AudioExtension(ReadOnlySpan<byte> data)
    {
        return Match(s_audio, data) ?? ".wav";
    }

    public static string ImageExtension(ReadOnlySpan<byte> data)
    {
        return Match(s_images, data) ?? ".png";
    }

    /// <summary>The image format's name, for the Properties pane.</summary>
    public static string ImageFormat(ReadOnlySpan<byte> data)
    {
        return ImageExtension(data).TrimStart('.').ToUpperInvariant();
    }

    private static string? Match(Signature[] signatures, ReadOnlySpan<byte> data)
    {
        foreach (Signature signature in signatures)
        {
            ReadOnlySpan<byte> bytes = signature.Bytes;
            if (data.Length >= signature.Offset + bytes.Length
                && data.Slice(signature.Offset, bytes.Length).SequenceEqual(bytes))
            {
                return signature.Extension;
            }
        }

        return null;
    }

    /// <summary>
    /// A RIFF container is only ours if it is a WAVE. RIFF also carries AVI, WebP and
    /// several others, and claiming those would give them an audio transport that can
    /// decode nothing.
    /// </summary>
    private static bool IsWave(ReadOnlySpan<byte> data)
    {
        return data.Length >= 12 && data.Slice(8, 4).SequenceEqual("WAVE"u8);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        return data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
    }
}
