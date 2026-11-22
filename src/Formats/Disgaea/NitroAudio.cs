using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed record NitroWave(string Format, string Encoding, int Channels, int SampleRate, bool Loops, byte[] Wav);
internal static class NitroAudio
{
    private static readonly string[] s_encodings = ["PCM8", "PCM16", "IMA-ADPCM"];
    internal static readonly short[] Steps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
        107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623,
        27086, 29794, 32767,
    ];
    internal static readonly sbyte[] IndexShift = [-1, -1, -1, -1, 2, 4, 6, 8];
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("SWAV"u8) || data.StartsWith("STRM"u8);
    }
    public static NitroWave Decode(ReadOnlySpan<byte> data)
    {
        return data.StartsWith("SWAV"u8) ? Swav(data)
            : data.StartsWith("STRM"u8) ? Strm(data)
            : throw new InvalidDataException("not a SWAV or STRM file");
    }
    private static NitroWave Swav(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, 0x24);
        int type = Encoding(data[0x18]);
        int rate = Rate(BinaryRead.U16(data, 0x1A));
        long length = (BinaryRead.U16(data, 0x1E) + (long)BinaryRead.U32(data, 0x20)) * 4;
        BinaryRead.Need(data, 0x24, (int)Math.Min(int.MaxValue, length));
        short[] samples = Channel(data.Slice(0x24, (int)length), type);
        int? loop = data[0x19] != 0 ? Math.Max(0, (BinaryRead.U16(data, 0x1E) - HeaderWords(type)) * SamplesPerWord(type)) : null;
        return new NitroWave("SWAV", s_encodings[type], 1, rate, loop is not null, Wav(samples, 1, rate, loop));
    }
    private static NitroWave Strm(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, 0x40);
        int type = Encoding(data[0x18]);
        int channels = data[0x1A];
        int rate = Rate(BinaryRead.U16(data, 0x1C));
        uint total = BinaryRead.U32(data, 0x24);
        long at = BinaryRead.U32(data, 0x28);
        uint blocks = BinaryRead.U32(data, 0x2C);
        int blockLength = (int)BinaryRead.U32(data, 0x30);
        int lastLength = (int)BinaryRead.U32(data, 0x38);
        if (channels is < 1 or > 2 || blocks == 0 || blockLength < 0 || lastLength < 0)
        {
            throw new InvalidDataException($"unsupported STRM layout: {channels} channel(s), {blocks} block(s)");
        }
        List<short>[] decoded = [.. Enumerable.Range(0, channels).Select(_ => new List<short>())];
        for (uint block = 0; block < blocks; block++)
        {
            int length = block + 1 == blocks ? lastLength : blockLength;
            for (int channel = 0; channel < channels; channel++)
            {
                long from = at + (block * (long)blockLength * channels) + (channel * (long)length);
                if (from + length > data.Length)
                {
                    throw new InvalidDataException($"STRM block {block} runs past the end of the file");
                }
                decoded[channel].AddRange(Channel(data.Slice((int)from, length), type));
            }
        }
        int frames = (int)Math.Min(total, (uint)decoded.Min(d => d.Count));
        short[] interleaved = new short[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                interleaved[(i * channels) + channel] = decoded[channel][i];
            }
        }
        int? loop = data[0x19] != 0 ? (int)Math.Min(BinaryRead.U32(data, 0x20), (uint)frames) : null;
        return new NitroWave("STRM", s_encodings[type], channels, rate, loop is not null, Wav(interleaved, channels, rate, loop));
    }
    internal static int HeaderWords(int type)
    {
        return type == 2 ? 1 : 0;
    }
    internal static int SamplesPerWord(int type)
    {
        return type switch
        {
            0 => 4,
            1 => 2,
            _ => 8,
        };
    }
    internal static int Encoding(byte type)
    {
        return type < s_encodings.Length ? type : throw new InvalidDataException($"unknown wave encoding {type}");
    }
    internal static int Rate(int rate)
    {
        return rate > 0 ? rate : throw new InvalidDataException("the sample rate is 0");
    }
    internal static short[] Channel(ReadOnlySpan<byte> data, int type)
    {
        switch (type)
        {
            case 0:
                {
                    short[] samples = new short[data.Length];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = (short)((sbyte)data[i] << 8);
                    }
                    return samples;
                }
            case 1:
                {
                    short[] samples = new short[data.Length / 2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
                    }
                    return samples;
                }
            default:
                return Adpcm(data);
        }
    }
    private static short[] Adpcm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return [];
        }
        int sample = BinaryPrimitives.ReadInt16LittleEndian(data);
        int index = Math.Clamp((int)BinaryRead.U16(data, 2), 0, Steps.Length - 1);
        short[] samples = new short[(data.Length - 4) * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            int nibble = data[4 + (i >> 1)] >> ((i & 1) * 4) & 0xF;
            int step = Steps[index];
            int diff = step >> 3;
            diff += (nibble & 1) != 0 ? step >> 2 : 0;
            diff += (nibble & 2) != 0 ? step >> 1 : 0;
            diff += (nibble & 4) != 0 ? step : 0;
            sample = Math.Clamp((nibble & 8) != 0 ? sample - diff : sample + diff, -0x7FFF, 0x7FFF);
            index = Math.Clamp(index + IndexShift[nibble & 7], 0, Steps.Length - 1);
            samples[i] = (short)sample;
        }
        return samples;
    }
    private static byte[] Wav(short[] samples, int channels, int rate, int? loop)
    {
        int frames = samples.Length / channels;
        bool looped = loop is { } start && start < frames;
        int pcm = 44 + (samples.Length * 2);
        byte[] wav = new byte[pcm + (looped ? 68 : 0)];
        Span<byte> span = wav;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], samples[i]);
        }
        if (looped)
        {
            Span<byte> smpl = span[pcm..];
            "smpl"u8.CopyTo(smpl);
            BinaryPrimitives.WriteInt32LittleEndian(smpl[4..], 60);
            BinaryPrimitives.WriteInt32LittleEndian(smpl[16..], (int)(1_000_000_000L / rate));
            BinaryPrimitives.WriteInt32LittleEndian(smpl[20..], 60);
            BinaryPrimitives.WriteInt32LittleEndian(smpl[36..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(smpl[52..], loop!.Value);
            BinaryPrimitives.WriteInt32LittleEndian(smpl[56..], frames - 1);
        }
        return wav;
    }
}
