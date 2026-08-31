using NVorbis;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace OmegaStudio.Audio;

public sealed class OggVorbisDecoder : IAudioDecoder
{
    private const int ChunkSamples = 65536;
    private const int PageHeaderSize = 27;
    private const int MinPageSize = PageHeaderSize;
    private const int MaxSamples = 1 << 27;
    private readonly short[] _pcm;
    public OggVorbisDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using MemoryStream stream = new(data);
        using VorbisReader reader = new(stream, true);
        SampleRate = reader.SampleRate;
        Channels = reader.Channels;
        long frames = reader.TotalSamples;
        long hint = Channels > 0 && frames > 0 && frames <= MaxSamples / Channels
            ? frames * Channels
            : 0;
        short[] pcm = new short[hint > 0 ? hint : ChunkSamples];
        float[] buffer = new float[ChunkSamples];
        int written = 0;
        while (written < MaxSamples)
        {
            int read = reader.ReadSamples(buffer, 0, ChunkSamples);
            if (read <= 0)
            {
                break;
            }
            if (written + read > pcm.Length)
            {
                Array.Resize(ref pcm, (int)Math.Min(
                    MaxSamples, Math.Max((long)written + read, pcm.Length * 2L)));
            }
            read = Math.Min(read, pcm.Length - written);
            for (int i = 0; i < read; i++)
            {
                pcm[written + i] = (short)(Math.Clamp(buffer[i], -1f, 1f) * short.MaxValue);
            }
            written += read;
        }
        if (written != pcm.Length)
        {
            Array.Resize(ref pcm, written);
        }
        _pcm = pcm;
        TotalSamples = _pcm.Length;
        TotalBytes = _pcm.Length * sizeof(short);
    }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 16;
    public long TotalSamples { get; }
    public int TotalBytes { get; }
    public void ReadAll(Span<byte> destination)
    {
        MemoryMarshal.AsBytes(_pcm.AsSpan()).CopyTo(destination);
    }
    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;
        if (!AudioFormats.IsOgg(data))
        {
            return false;
        }
        bool foundIdHeader = false;
        ulong samplesPerChannel = 0;
        int sampleRate = 0;
        int channels = 0;
        for (int pos = 0; pos <= data.Length - MinPageSize;)
        {
            if (!AudioFormats.IsOgg(data[pos..]))
            {
                break;
            }
            byte headerType = data[pos + 5];
            ulong granulePos = BinaryPrimitives.ReadUInt64LittleEndian(data[(pos + 6)..]);
            int segmentCount = data[pos + 26];
            int segmentTable = pos + PageHeaderSize;
            int packet = segmentTable + segmentCount;
            if (packet > data.Length)
            {
                break;
            }
            if (!foundIdHeader && granulePos == 0 && (headerType & 0x02) != 0)
            {
                foundIdHeader = TryReadIdHeader(data, packet, out channels, out sampleRate);
            }
            if ((headerType & 0x04) != 0)
            {
                samplesPerChannel = granulePos;
            }
            int pageBytes = 0;
            for (int i = 0; i < segmentCount; i++)
            {
                pageBytes += data[segmentTable + i];
            }
            pos += PageHeaderSize + segmentCount + pageBytes;
        }
        if (!foundIdHeader || sampleRate == 0 || channels == 0)
        {
            return false;
        }
        long interleaved = (long)samplesPerChannel * channels;
        if (interleaved > int.MaxValue / 2)
        {
            return false;
        }
        metadata = new AudioMetadata(sampleRate, channels, 16, interleaved, 0, (int)(interleaved * 2));
        return true;
    }
    private static bool TryReadIdHeader(ReadOnlySpan<byte> data, int at, out int channels, out int sampleRate)
    {
        channels = 0;
        sampleRate = 0;
        ReadOnlySpan<byte> signature = [0x01, .. "vorbis"u8];
        if (at + 30 > data.Length || !data.Slice(at, signature.Length).SequenceEqual(signature))
        {
            return false;
        }
        channels = data[at + 11];
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 12)..]);
        return true;
    }
    public void Dispose()
    {
    }
}
