namespace OmegaStudio.Audio;
public readonly record struct AudioInfo(
    string Format, int SampleRate, int Channels, int BitsPerSample, long TotalSamples)
{
    public const int MaxSampleRate = 768_000;
    public const int MaxChannels = 255;
    public TimeSpan Duration => PcmDuration(TotalSamples, SampleRate, Channels);
    public static TimeSpan PcmDuration(long samples, int sampleRate, int channels)
    {
        if (samples <= 0 || sampleRate <= 0 || channels <= 0)
        {
            return TimeSpan.Zero;
        }
        double seconds = samples / ((double)sampleRate * channels);
        return double.IsFinite(seconds) && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
    }
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
            return info.SampleRate <= AudioInfo.MaxSampleRate && info.Channels <= AudioInfo.MaxChannels;
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
