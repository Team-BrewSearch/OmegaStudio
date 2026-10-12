namespace OmegaStudio.Audio;
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }
    long TotalSamples { get; }
    int TotalBytes { get; }
    void ReadAll(Span<byte> destination);
}
