namespace OmegaStudio.Audio;
public interface IAudioDecoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }
    long TotalSamples { get; }
    long TotalBytes { get; }
    int FrameBytes => Channels * (BitsPerSample / 8);
    int Read(Span<byte> destination);
    void SeekToFrame(long frame);
}
