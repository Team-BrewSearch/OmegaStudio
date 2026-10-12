using System.Buffers.Binary;
using System.Text;
namespace Polarbit.Formats;
internal sealed class PsndSound
{
    private static readonly (int Bit, int Size)[] s_fields =
        [(0x001, 1), (0x002, 1), (0x004, 2), (0x008, 8), (0x010, 1), (0x200, 2), (0x400, 2), (0x800, 1)];
    private PsndSound()
    {
    }
    public int Flags { get; private init; }
    public int Rate { get; private init; }
    public int Channels { get; private init; }
    public int Bits { get; private init; }
    public int DataOffset { get; private init; }
    public uint SizeField { get; private init; }
    public int LoopOffset { get; private init; } = -1;
    public (uint Start, uint End)? Loop { get; private init; }
    public bool PingPong { get; private init; }
    public string Codec => Bits switch
    {
        16 => "PCM 16-bit",
        8 => "PCM 8-bit",
        _ => "IMA ADPCM",
    };
    public static PsndSound Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 14 && b.StartsWith("PSND"u8) && Le.U32(b, 4) <= b.Length - 8, "PSND header is truncated.");
        int a = Le.U16(b, 8), flags = Le.U16(b, 10), rate = Le.U16(b, 12);
        Le.Need((flags & ~0xE1F) == 0, $"PSND flag bits 0x{flags:X} are unknown.");
        int p = 14, channels = 1, bits = 0, loopOffset = -1, loopType = 0;
        foreach ((int bit, int size) in s_fields)
        {
            if ((flags & bit) == 0)
            {
                continue;
            }
            Le.Need(p + size <= b.Length, "PSND header fields run past the data.");
            switch (bit)
            {
                case 0x001: channels = b[p]; break;
                case 0x002: bits = b[p]; break;
                case 0x008: loopOffset = p; break;
                case 0x010: loopType = b[p]; break;
                default: break;
            }
            p += size;
        }
        Le.Need(p == 10 + a, "PSND header length field disagrees with its flags.");
        Le.Need(bits is 0 or 8 or 16 && channels is 1 or 2 && (bits != 0 || channels == 1), $"PSND with {channels} channel(s) at {bits} bits is not supported.");
        Le.Need(8 + Le.U32(b, 4) == b.Length || bits == 16, "PSND size field disagrees with the data.");
        return new PsndSound
        {
            Flags = flags,
            Rate = rate,
            Channels = channels,
            Bits = bits,
            DataOffset = p,
            SizeField = Le.U32(b, 4),
            LoopOffset = loopOffset,
            Loop = loopOffset < 0 ? null : (Le.U32(b, loopOffset), Le.U32(b, loopOffset + 4)),
            PingPong = loopType == 2,
        };
    }
    public short[] Samples(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> data = b[DataOffset..];
        switch (Bits)
        {
            case 16:
                short[] pcm = new short[data.Length / (2 * Channels) * Channels];
                for (int i = 0; i < pcm.Length; i++)
                {
                    pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
                }
                return pcm;
            case 8:
                short[] wide = new short[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    wide[i] = (short)((sbyte)data[i] << 8);
                }
                return wide;
            default:
                return Ima.Decode(data);
        }
    }
    public double Seconds(ReadOnlySpan<byte> b)
    {
        int frames = Bits switch
        {
            16 => (b.Length - DataOffset) / (2 * Channels),
            8 => b.Length - DataOffset,
            _ => (b.Length - DataOffset) * 2,
        };
        return Rate > 0 ? frames / (double)Rate : 0;
    }
    public byte[] ToWav(ReadOnlySpan<byte> b)
    {
        return Wav.Write(Rate, Channels, Samples(b), Loop, PingPong);
    }
    public byte[] FromWav(ReadOnlySpan<byte> original, ReadOnlySpan<byte> wav)
    {
        (int rate, int channels, short[] samples) = Wav.Read(wav);
        Le.Need(channels == Channels, $"The WAV has {channels} channel(s); this sound needs {Channels}.");
        Le.Need(rate is > 0 and <= ushort.MaxValue, $"A {rate} Hz sample rate does not fit the PSND header.");
        ReadOnlySpan<byte> hint = original[DataOffset..];
        byte[] data = Bits switch
        {
            16 => Pcm16(samples, Channels, hint),
            8 => [.. samples.Select(v => (byte)System.Math.Min(127, (v + 128) >> 8))],
            _ => Ima.Encode(samples, hint),
        };
        byte[] output = new byte[DataOffset + data.Length];
        original[..DataOffset].CopyTo(output);
        data.CopyTo(output, DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), data.Length == hint.Length ? SizeField : (uint)(output.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(12), (ushort)rate);
        if (Loop is { } loop)
        {
            uint frames = (uint)(samples.Length / Channels);
            uint end = System.Math.Min(loop.End, frames);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(LoopOffset), System.Math.Min(loop.Start, end));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(LoopOffset + 4), end);
        }
        return output;
    }
    private static byte[] Pcm16(short[] samples, int channels, ReadOnlySpan<byte> hint)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }
        return hint.Length - data.Length == hint.Length % (2 * channels) && hint.StartsWith(data) ? hint.ToArray() : data;
    }
}
internal static class Ima
{
    private static readonly int[] s_index = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] s_steps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
        107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724,
        796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
        4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500,
        20350, 22385, 24623, 27086, 29794, 32767,
    ];
    private static int Delta(int index, int nibble)
    {
        int s = s_steps[index];
        int d = (s >> 3) + ((nibble & 1) * (s >> 2)) + (((nibble >> 1) & 1) * (s >> 1)) + (((nibble >> 2) & 1) * s);
        return (nibble & 8) != 0 ? -d : d;
    }
    private static (int Predictor, int Index) Step(int predictor, int index, int nibble)
    {
        return (System.Math.Clamp(predictor + Delta(index, nibble), short.MinValue, short.MaxValue),
                System.Math.Clamp(index + s_index[nibble], 0, 88));
    }
    public static short[] Decode(ReadOnlySpan<byte> data)
    {
        short[] output = new short[data.Length * 2];
        int predictor = 0, index = 0;
        for (int i = 0; i < output.Length; i++)
        {
            int nibble = (i & 1) == 0 ? data[i >> 1] >> 4 : data[i >> 1] & 15;
            (predictor, index) = Step(predictor, index, nibble);
            output[i] = (short)predictor;
        }
        return output;
    }
    public static byte[] Encode(short[] samples, ReadOnlySpan<byte> hint)
    {
        byte[] output = new byte[(samples.Length + 1) / 2];
        int predictor = 0, index = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int nibble = (i >> 1) < hint.Length ? (hint[i >> 1] >> ((i & 1) == 0 ? 4 : 0)) & 15 : 0;
            if (Step(predictor, index, nibble).Predictor != samples[i])
            {
                int bestError = int.MaxValue;
                for (int m = 0; m < 16; m++)
                {
                    int error = System.Math.Abs(Step(predictor, index, m).Predictor - samples[i]);
                    if (error < bestError)
                    {
                        bestError = error;
                        nibble = m;
                    }
                }
            }
            (predictor, index) = Step(predictor, index, nibble);
            output[i >> 1] |= (byte)((i & 1) == 0 ? nibble << 4 : nibble);
        }
        return output;
    }
}
internal static class Wav
{
    public static byte[] Write(int rate, int channels, short[] samples, (uint Start, uint End)? loop, bool pingPong)
    {
        int smpl = loop is null ? 0 : 68;
        byte[] output = new byte[44 + (samples.Length * 2) + smpl];
        Span<byte> o = output;
        "RIFF"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], (uint)(output.Length - 8));
        "WAVEfmt "u8.CopyTo(o[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(o[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(o[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(o[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(o[28..], (uint)(rate * 2 * channels));
        BinaryPrimitives.WriteUInt16LittleEndian(o[32..], (ushort)(2 * channels));
        BinaryPrimitives.WriteUInt16LittleEndian(o[34..], 16);
        "data"u8.CopyTo(o[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(o[40..], (uint)(samples.Length * 2));
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(o[(44 + (i * 2))..], samples[i]);
        }
        if (loop is { } l)
        {
            Span<byte> s = o[(44 + (samples.Length * 2))..];
            "smpl"u8.CopyTo(s);
            uint[] fields = [60, 0, 0, (uint)System.Math.Round(1e9 / System.Math.Max(rate, 1)), 60, 0, 0, 0, 1, 0, 0,
                pingPong ? 1u : 0u, l.Start, System.Math.Max(l.End == 0 ? 0 : l.End - 1, l.Start), 0];
            for (int i = 0; i < fields.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s[(4 + (i * 4))..], fields[i]);
            }
        }
        return output;
    }
    public static (int Rate, int Channels, short[] Samples) Read(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 12 && b.StartsWith("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8), "Not a RIFF WAVE file.");
        int rate = 0, channels = 0, bits = 0, format = 0;
        ReadOnlySpan<byte> data = default;
        bool haveData = false;
        for (int p = 12; p + 8 <= b.Length;)
        {
            string id = Encoding.ASCII.GetString(b.Slice(p, 4));
            int size = (int)System.Math.Min(Le.U32(b, p + 4), (uint)(b.Length - p - 8));
            if (id == "fmt " && size >= 16)
            {
                format = Le.U16(b, p + 8);
                channels = Le.U16(b, p + 10);
                rate = (int)Le.U32(b, p + 12);
                bits = Le.U16(b, p + 22);
            }
            else if (id == "data")
            {
                data = b.Slice(p + 8, size);
                haveData = true;
            }
            p += 8 + size + (size & 1);
        }
        Le.Need(haveData && format is 1 or 0xFFFE && bits is 8 or 16 or 24 or 32 && channels > 0,
            $"Only integer PCM WAV files can be imported (this one is format {format}, {bits}-bit).");
        int width = bits / 8;
        short[] samples = new short[data.Length / width / channels * channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = width == 1
                ? (short)((data[i] - 128) << 8)
                : BinaryPrimitives.ReadInt16LittleEndian(data[((i * width) + width - 2)..]);
        }
        return (rate, channels, samples);
    }
}
