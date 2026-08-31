namespace OmegaStudio.Audio;

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
    public static string ExtensionOf(ReadOnlySpan<byte> blob)
    {
        return IsOgg(blob) ? ".ogg" : ".wav";
    }
}
