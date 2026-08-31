namespace OmegaStudio.Audio;

public readonly record struct AudioInfo(
    string Format, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    public TimeSpan Duration => SampleRate > 0 && Channels > 0
        ? TimeSpan.FromSeconds((double)TotalSamples / (SampleRate * Channels))
        : TimeSpan.Zero;
    public override string ToString()
    {
        string channels = Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{Channels} ch" };
        return $"{Format} · {SampleRate:N0} Hz · {channels} · {BitsPerSample}-bit · {Duration:mm\\:ss\\.fff}";
    }
}
public static class AudioInfoHelper
{
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out AudioInfo info)
    {
        if (AudioFormats.IsOgg(data) && OggVorbisDecoder.TryParse(data, out AudioMetadata ogg))
        {
            info = new AudioInfo("OGG Vorbis", ogg.SampleRate, ogg.Channels, ogg.BitsPerSample, ogg.TotalSamples);
            return true;
        }
        if (AudioFormats.IsWav(data) && WavDecoder.TryParse(data, out AudioMetadata wav))
        {
            info = new AudioInfo("WAV", wav.SampleRate, wav.Channels, wav.BitsPerSample, wav.TotalSamples);
            return true;
        }
        info = default;
        return false;
    }
    public static IAudioDecoder CreateDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return AudioFormats.IsOgg(data)
            ? new OggVorbisDecoder(data)
            : WavDecoder.TryParse(data, out AudioMetadata metadata)
            ? new WavDecoder(data, metadata)
            : throw new InvalidDataException("Not a recognised WAV or Ogg Vorbis clip.");
    }
}
