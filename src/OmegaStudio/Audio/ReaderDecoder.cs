using System.Runtime.InteropServices;
using Omega.Plugins;
namespace OmegaStudio.Audio;
public sealed class ReaderDecoder : IAudioDecoder
{
    private readonly IAudioReader _reader;
    public ReaderDecoder(IAudioReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.SampleRate is <= 0 or > AudioInfo.MaxSampleRate || reader.Channels is not (1 or 2) || reader.TotalFrames < 0)
        {
            reader.Dispose();
            throw new InvalidDataException(
                $"A streamed clip must be mono or stereo at 1 to {AudioInfo.MaxSampleRate} Hz.");
        }
        _reader = reader;
    }
    public int SampleRate => _reader.SampleRate;
    public int Channels => _reader.Channels;
    public int BitsPerSample => 16;
    public long TotalSamples => _reader.TotalFrames * _reader.Channels;
    public long TotalBytes => TotalSamples * 2;
    public int Read(Span<byte> destination)
    {
        int frameBytes = Channels * 2;
        Span<short> samples = MemoryMarshal.Cast<byte, short>(destination[..(destination.Length - (destination.Length % frameBytes))]);
        return _reader.Read(samples) * frameBytes;
    }
    public void SeekToFrame(long frame)
    {
        _reader.Seek(frame);
    }
    public void Dispose()
    {
        _reader.Dispose();
    }
}
