using System.Buffers.Binary;
namespace OmegaStudio.Audio;
public sealed class WavDecoder : IAudioDecoder
{
    private const int MinHeaderSize = 44;
    private const uint FmtChunk = 0x20746D66;
    private const uint DataChunk = 0x61746164;
    private const int PcmFormat = 1;
    private readonly byte[] _data;
    private readonly int _dataOffset;
    public WavDecoder(byte[] data, in AudioMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _dataOffset = metadata.DataOffset;
        TotalBytes = metadata.DataLength;
        SampleRate = metadata.SampleRate;
        Channels = metadata.Channels;
        BitsPerSample = metadata.BitsPerSample;
        TotalSamples = metadata.TotalSamples;
    }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public long TotalSamples { get; }
    public int TotalBytes { get; }
    public static bool TryParse(ReadOnlySpan<byte> data, out AudioMetadata metadata)
    {
        metadata = default;
        if (data.Length < MinHeaderSize || !AudioFormats.IsWav(data))
        {
            return false;
        }
        int sampleRate = 0, channels = 0, bitsPerSample = 0, dataOffset = 0, dataLength = 0;
        for (int offset = 12; offset + 8 <= data.Length;)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            if (chunkSize < 0)
            {
                return false;
            }
            int body = offset + 8;
            if (chunkId == FmtChunk)
            {
                if (chunkSize < 16 || body + 16 > data.Length
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[body..]) != PcmFormat)
                {
                    return false;
                }
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 2)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(body + 4)..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 14)..]);
            }
            else if (chunkId == DataChunk && dataOffset == 0)
            {
                dataOffset = body;
                dataLength = Math.Min(chunkSize, data.Length - dataOffset);
            }
            long next = (long)body + chunkSize + (chunkSize & 1);
            if (next <= offset || next > data.Length)
            {
                break;
            }
            offset = (int)next;
        }
        if (sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16))
        {
            return false;
        }
        dataLength -= dataLength % (channels * (bitsPerSample / 8));
        if (dataLength <= 0)
        {
            return false;
        }
        metadata = new AudioMetadata(
            sampleRate, channels, bitsPerSample,
            dataLength / (bitsPerSample / 8), dataOffset, dataLength);
        return true;
    }
    public void ReadAll(Span<byte> destination)
    {
        _data.AsSpan(_dataOffset, TotalBytes).CopyTo(destination);
    }
    public void Dispose()
    {
    }
}
