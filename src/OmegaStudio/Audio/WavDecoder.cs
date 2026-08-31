using System.Buffers.Binary;

namespace OmegaStudio.Audio;

/// <summary>
/// Uncompressed PCM WAV. The payload needs no decoding at all — playback hands
/// the <c>data</c> chunk straight to OpenAL — so this is really a header parser.
/// </summary>
public sealed class WavDecoder : IAudioDecoder
{
    private const int MinHeaderSize = 44;
    private const uint FmtChunk = 0x20746D66;   // "fmt "
    private const uint DataChunk = 0x61746164;  // "data"
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

    /// <summary>
    /// Reads the header. Chunks are walked rather than assumed at fixed offsets:
    /// the shipped files carry the odd <c>LIST</c> chunk between <c>fmt</c> and
    /// <c>data</c>. Only uncompressed 8- and 16-bit PCM is accepted.
    /// </summary>
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
                // The 16 bytes of a PCM fmt block have to actually be present: a truncated file
                // would otherwise be read past its own end rather than rejected.
                if (chunkSize < 16 || body + 16 > data.Length
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[body..]) != PcmFormat)
                {
                    return false;
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 2)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data[(body + 4)..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 14)..]);
            }
            else if (chunkId == DataChunk)
            {
                dataOffset = body;
                dataLength = Math.Min(chunkSize, data.Length - dataOffset);
                break;
            }

            // RIFF pads an odd-sized chunk to an even boundary, and the padding byte is not
            // counted in the declared size. Stepping by the size alone desynchronises the walk
            // at the first odd chunk, and every chunk after it - including the one holding the
            // samples - is then read from the wrong place. Widened as well, because the size is
            // file data and offset + 8 + size overflows an int.
            long next = (long)body + chunkSize + (chunkSize & 1);
            if (next <= offset || next > data.Length)
            {
                break;
            }

            offset = (int)next;
        }

        if (sampleRate <= 0 || channels <= 0 || bitsPerSample is not (8 or 16) || dataLength <= 0)
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
        // Nothing to release: the decoder is a view over a caller-owned array.
    }
}
