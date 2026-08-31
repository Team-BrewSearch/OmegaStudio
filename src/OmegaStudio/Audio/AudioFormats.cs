namespace OmegaStudio.Audio;

/// <summary>
/// Magic-number checks for the clip containers the host can decode.
///
/// <para>
/// Here rather than in a plugin because decoding audio is not format knowledge: a
/// plugin says "this node holds an encoded clip" through
/// <see cref="Omega.Plugins.IAudioAsset"/> and hands over the bytes, and the host
/// works out the rest. That is what stops every game's plugin shipping its own
/// Vorbis reader.
/// </para>
/// </summary>
public static class AudioFormats
{
    private static ReadOnlySpan<byte> OggMagic => "OggS"u8;
    private static ReadOnlySpan<byte> RiffMagic => "RIFF"u8;
    private static ReadOnlySpan<byte> WaveMagic => "WAVE"u8;

    public static bool IsOgg(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 4 && blob[..4].SequenceEqual(OggMagic);
    }

    public static bool IsWav(ReadOnlySpan<byte> blob)
    {
        return blob.Length >= 12
            && blob[..4].SequenceEqual(RiffMagic)
            && blob.Slice(8, 4).SequenceEqual(WaveMagic);
    }

    public static bool IsAudio(ReadOnlySpan<byte> blob)
    {
        return IsOgg(blob) || IsWav(blob);
    }

    /// <summary>The file extension a clip should be extracted with, including the dot.</summary>
    public static string ExtensionOf(ReadOnlySpan<byte> blob)
    {
        return IsOgg(blob) ? ".ogg" : ".wav";
    }
}
