using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NVorbis;
namespace OmegaStudio.Audio;
public sealed class OggVorbisDecoder : IAudioDecoder
{
    private const int ChunkSamples = 65536;
    private const int PageHeaderSize = 27;
    private const int MinPageSize = PageHeaderSize;
    private readonly MemoryStream _stream;
    private readonly VorbisReader _reader;
    private float[] _scratch = [];
    public OggVorbisDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _stream = new MemoryStream(data, writable: false);
        _reader = new VorbisReader(_stream, false);
        SampleRate = _reader.SampleRate;
        Channels = _reader.Channels;
        if (SampleRate <= 0 || Channels <= 0)
        {
            Dispose();
            throw new InvalidDataException("the Ogg Vorbis header declares no sample rate or no channels");
        }
        TotalSamples = Math.Max(0, _reader.TotalSamples) * Channels;
        TotalBytes = TotalSamples * sizeof(short);
    }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 16;
    public long TotalSamples { get; }
    public long TotalBytes { get; }
    public int Read(Span<byte> destination)
    {
        int want = Math.Min(destination.Length / sizeof(short), ChunkSamples);
        want -= want % Channels;
        if (want <= 0)
        {
            return 0;
        }
        if (_scratch.Length < want)
        {
            _scratch = new float[want];
        }
        int read = _reader.ReadSamples(_scratch, 0, want);
        if (read <= 0)
        {
            return 0;
        }
        Span<short> pcm = MemoryMarshal.Cast<byte, short>(destination);
        for (int i = 0; i < read; i++)
        {
            pcm[i] = (short)(Math.Clamp(_scratch[i], -1f, 1f) * short.MaxValue);
        }
        return read * sizeof(short);
    }
    public void SeekToFrame(long frame)
    {
        _reader.SamplePosition = Math.Clamp(frame, 0, Math.Max(0, _reader.TotalSamples));
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
        _reader.Dispose();
        _stream.Dispose();
    }
}
