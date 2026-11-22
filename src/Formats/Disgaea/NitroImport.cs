using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal static class NitroImport
{
    private const int SwavData = 0x24;
    public static byte[] Encode(ReadOnlySpan<byte> original, byte[] wav)
    {
        bool swav = original.StartsWith("SWAV"u8);
        if (!swav && !original.StartsWith("STRM"u8))
        {
            throw new InvalidDataException("not a SWAV or STRM file");
        }
        BinaryRead.Need(original, 0, 0x20);
        int rateAt = swav ? 0x1A : 0x1C;
        int oldRate = NitroAudio.Rate(BinaryRead.U16(original, rateAt));
        int timer = BinaryRead.U16(original, rateAt + 2);
        int channels = swav ? 1 : original[0x1A];
        (short[] samples, int from, int rate, (long Start, long End)? loop) = ReadWav(wav);
        samples = Mix(samples, from, channels);
        if (!Fits(timer, oldRate, rate))
        {
            samples = Resample(samples, channels, rate, oldRate);
            loop = loop is { } found ? ((long)Math.Round(found.Start * (double)oldRate / rate), (long)Math.Round(found.End * (double)oldRate / rate)) : null;
            rate = oldRate;
        }
        int frames = samples.Length / channels;
        int? start = null;
        if (loop is { } points)
        {
            int end = (int)Math.Clamp(points.End + 1, 1, frames);
            start = (int)Math.Clamp(points.Start, 0, end - 1);
            samples = samples[..(end * channels)];
        }
        return swav ? Swav(original, samples, rate, start) : Strm(original, samples, channels, rate, start);
    }
    private static bool Fits(int timer, int oldRate, int rate)
    {
        double scaled = Math.Round(timer * (double)oldRate / rate);
        return rate is > 0 and <= ushort.MaxValue && scaled is >= 1 and <= ushort.MaxValue;
    }
    private static short[] Mix(short[] samples, int from, int to)
    {
        if (from == to)
        {
            return samples;
        }
        int frames = samples.Length / from;
        short[] output = new short[frames * to];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < to; c++)
            {
                long sum = 0;
                int count = 0;
                for (int k = c % from; k < from; k += to)
                {
                    sum += samples[(i * from) + k];
                    count++;
                }
                output[(i * to) + c] = (short)(sum / count);
            }
        }
        return output;
    }
    private static short[] Resample(short[] samples, int channels, int from, int to)
    {
        int frames = samples.Length / channels;
        int count = (int)Math.Max(1, Math.Round(frames * (double)to / from));
        short[] output = new short[count * channels];
        for (int i = 0; i < count; i++)
        {
            double at = i * (double)from / to;
            int left = Math.Min((int)at, frames - 1);
            int right = Math.Min(left + 1, frames - 1);
            double t = at - left;
            for (int c = 0; c < channels; c++)
            {
                output[(i * channels) + c] = (short)Math.Round(samples[(left * channels) + c] + ((samples[(right * channels) + c] - samples[(left * channels) + c]) * t));
            }
        }
        return output;
    }
    private static byte[] Swav(ReadOnlySpan<byte> original, short[] samples, int rate, int? loopStart)
    {
        BinaryRead.Need(original, 0, SwavData);
        int type = NitroAudio.Encoding(original[0x18]);
        int oldRate = NitroAudio.Rate(BinaryRead.U16(original, 0x1A));
        int oldLength = (int)Math.Min(int.MaxValue, (BinaryRead.U16(original, 0x1E) + (long)BinaryRead.U32(original, 0x20)) * 4);
        BinaryRead.Need(original, SwavData, oldLength);
        byte[] chunk = Pad(Chunk(samples, type, original.Slice(SwavData, oldLength)));
        int words = chunk.Length / 4;
        int loop = Math.Min(NitroAudio.HeaderWords(type) + ((loopStart ?? 0) / NitroAudio.SamplesPerWord(type)), words);
        byte[] output = new byte[SwavData + chunk.Length];
        original[..SwavData].CopyTo(output);
        chunk.CopyTo(output, SwavData);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x08), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x14), output.Length - 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x1A), checked((ushort)rate));
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x1C), Timer(BinaryRead.U16(original, 0x1C), oldRate, rate));
        output[0x19] = loopStart is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0x1E), checked((ushort)loop));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(0x20), words - loop);
        return output;
    }
    private static byte[] Strm(ReadOnlySpan<byte> original, short[] samples, int channels, int rate, int? loopStart)
    {
        BinaryRead.Need(original, 0, 0x40);
        int type = NitroAudio.Encoding(original[0x18]);
        if (channels is < 1 or > 2)
        {
            throw new InvalidDataException($"unsupported STRM layout: {channels} channel(s)");
        }
        int oldRate = NitroAudio.Rate(BinaryRead.U16(original, 0x1C));
        uint total = BinaryRead.U32(original, 0x24);
        int at = (int)BinaryRead.U32(original, 0x28);
        int oldBlocks = (int)BinaryRead.U32(original, 0x2C);
        int blockLength = (int)BinaryRead.U32(original, 0x30);
        int blockSamples = (int)BinaryRead.U32(original, 0x34);
        int lastLength = (int)BinaryRead.U32(original, 0x38);
        if (at < 0x48 || blockSamples <= 0 || oldBlocks <= 0)
        {
            throw new InvalidDataException("unsupported STRM layout");
        }
        int frames = samples.Length / channels;
        if (frames == 0)
        {
            throw new InvalidDataException("the WAV holds no samples");
        }
        byte[][] oldChunks = new byte[oldBlocks * channels][];
        List<short>[] padded = new List<short>[channels];
        for (int c = 0; c < channels; c++)
        {
            padded[c] = new List<short>(frames);
            for (int i = 0; i < frames; i++)
            {
                padded[c].Add(samples[(i * channels) + c]);
            }
        }
        for (int block = 0; block < oldBlocks; block++)
        {
            int length = block + 1 == oldBlocks ? lastLength : blockLength;
            for (int c = 0; c < channels; c++)
            {
                long from = at + (block * (long)blockLength * channels) + (c * (long)length);
                if (from + length > original.Length)
                {
                    throw new InvalidDataException($"STRM block {block} runs past the end of the file");
                }
                oldChunks[(block * channels) + c] = original.Slice((int)from, length).ToArray();
            }
        }
        if (frames == total)
        {
            for (int c = 0; c < channels; c++)
            {
                short[] tail = NitroAudio.Channel(oldChunks[((oldBlocks - 1) * channels) + c], type);
                int kept = (int)(total - ((oldBlocks - 1) * (long)blockSamples));
                padded[c].AddRange(tail.Skip(Math.Max(0, kept)));
            }
        }
        int blocks = (frames + blockSamples - 1) / blockSamples;
        using MemoryStream data = new();
        int newLast = 0;
        for (int block = 0; block < blocks; block++)
        {
            for (int c = 0; c < channels; c++)
            {
                int start = block * blockSamples;
                int count = block + 1 == blocks ? padded[c].Count - start : blockSamples;
                short[] slice = [.. padded[c].GetRange(start, count)];
                byte[] hint = block < oldBlocks ? oldChunks[(block * channels) + c] : [];
                byte[] chunk = Chunk(slice, type, hint);
                newLast = chunk.Length;
                data.Write(chunk);
            }
        }
        byte[] packed = Pad(data.ToArray());
        byte[] output = new byte[at + packed.Length];
        original[..at].CopyTo(output);
        packed.CopyTo(output, at);
        Span<byte> head = output;
        BinaryPrimitives.WriteInt32LittleEndian(head[0x08..], output.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(head[0x1C..], checked((ushort)rate));
        BinaryPrimitives.WriteUInt16LittleEndian(head[0x1E..], Timer(BinaryRead.U16(original, 0x1E), oldRate, rate));
        head[0x19] = loopStart is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32LittleEndian(head[0x20..], loopStart ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(head[0x24..], frames);
        BinaryPrimitives.WriteInt32LittleEndian(head[0x2C..], blocks);
        BinaryPrimitives.WriteInt32LittleEndian(head[0x38..], newLast);
        BinaryPrimitives.WriteInt32LittleEndian(head[0x3C..], frames - ((blocks - 1) * blockSamples));
        BinaryPrimitives.WriteInt32LittleEndian(head[(at - 4)..], packed.Length + 8);
        return output;
    }
    private static byte[] Chunk(short[] samples, int type, ReadOnlySpan<byte> original)
    {
        if (original.Length > 0 && NitroAudio.Channel(original, type).AsSpan().SequenceEqual(samples))
        {
            return original.ToArray();
        }
        byte[] chunk = type switch
        {
            0 => [.. samples.Select(s => (byte)(sbyte)Math.Clamp((s + 128) >> 8, sbyte.MinValue, sbyte.MaxValue))],
            1 => [.. samples.SelectMany(s => new[] { (byte)s, (byte)(s >> 8) })],
            _ => Adpcm(samples, original.Length >= 4 ? BinaryRead.U16(original, 2) : 0),
        };
        return chunk;
    }
    private static byte[] Pad(byte[] chunk)
    {
        return chunk.Length % 4 == 0 ? chunk : [.. chunk, .. new byte[4 - (chunk.Length % 4)]];
    }
    private static byte[] Adpcm(short[] samples, int startIndex)
    {
        byte[] output = new byte[4 + ((samples.Length + 1) / 2)];
        int sample = samples.Length > 0 ? samples[0] : 0;
        int index = Math.Clamp(startIndex, 0, NitroAudio.Steps.Length - 1);
        BinaryPrimitives.WriteInt16LittleEndian(output, (short)sample);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)index);
        for (int i = 0; i < samples.Length; i++)
        {
            int best = 0;
            int bestSample = 0;
            long bestError = long.MaxValue;
            for (int nibble = 0; nibble < 16; nibble++)
            {
                int next = Step(sample, index, nibble);
                long error = Math.Abs((long)next - samples[i]);
                if (error < bestError)
                {
                    (best, bestSample, bestError) = (nibble, next, error);
                }
            }
            output[4 + (i >> 1)] |= (byte)(best << ((i & 1) * 4));
            sample = bestSample;
            index = Math.Clamp(index + NitroAudio.IndexShift[best & 7], 0, NitroAudio.Steps.Length - 1);
        }
        return output;
    }
    private static int Step(int sample, int index, int nibble)
    {
        int step = NitroAudio.Steps[index];
        int diff = step >> 3;
        diff += (nibble & 1) != 0 ? step >> 2 : 0;
        diff += (nibble & 2) != 0 ? step >> 1 : 0;
        diff += (nibble & 4) != 0 ? step : 0;
        return Math.Clamp((nibble & 8) != 0 ? sample - diff : sample + diff, -0x7FFF, 0x7FFF);
    }
    private static ushort Timer(int timer, int oldRate, int rate)
    {
        return rate == oldRate ? (ushort)timer : checked((ushort)Math.Round(timer * (double)oldRate / rate));
    }
    private static (short[] Samples, int Channels, int Rate, (long Start, long End)? Loop) ReadWav(byte[] wav)
    {
        ReadOnlySpan<byte> data = wav;
        if (data.Length < 12 || !data.StartsWith("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a WAV file");
        }
        int format = 0;
        int channels = 0;
        int rate = 0;
        int bits = 0;
        int at = 12;
        short[]? samples = null;
        (long, long)? loop = null;
        while (at + 8 <= data.Length)
        {
            uint size = BinaryRead.U32(data, at + 4);
            int body = at + 8;
            if (data.Slice(at, 4).SequenceEqual("fmt "u8))
            {
                BinaryRead.Need(data, body, 16);
                format = BinaryRead.U16(data, body);
                channels = BinaryRead.U16(data, body + 2);
                rate = (int)BinaryRead.U32(data, body + 4);
                bits = BinaryRead.U16(data, body + 14);
                if (format == 0xFFFE && size >= 26)
                {
                    BinaryRead.Need(data, body, 26);
                    format = BinaryRead.U16(data, body + 24);
                }
            }
            else if (data.Slice(at, 4).SequenceEqual("data"u8))
            {
                samples = Samples(data.Slice(body, (int)Math.Min(size, (uint)(data.Length - body))), format, channels, bits, rate);
            }
            else if (data.Slice(at, 4).SequenceEqual("smpl"u8) && size >= 60 && body + 60 <= data.Length && BinaryRead.U32(data, body + 28) > 0)
            {
                loop = (BinaryRead.U32(data, body + 44), BinaryRead.U32(data, body + 48));
            }
            if (size > (uint)(data.Length - body))
            {
                break;
            }
            at = body + (int)size + (int)(size & 1);
        }
        return samples is null ? throw new InvalidDataException("the WAV has no data chunk") : (samples, channels, rate, loop);
    }
    private static short[] Samples(ReadOnlySpan<byte> pcm, int format, int channels, int bits, int rate)
    {
        bool valid = (format, bits) is (1, 8 or 16 or 24 or 32) or (3, 32 or 64);
        if (!valid || channels < 1 || rate <= 0)
        {
            throw new InvalidDataException($"unsupported WAV: format {format}, {channels} channel(s), {rate} Hz, {bits}-bit");
        }
        int width = bits / 8;
        int count = pcm.Length / (width * channels) * channels;
        if (count == 0)
        {
            throw new InvalidDataException("the WAV holds no samples");
        }
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> v = pcm.Slice(i * width, width);
            samples[i] = (format, bits) switch
            {
                (1, 8) => (short)((v[0] - 128) << 8),
                (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(v),
                (1, 24) => (short)((v[1]) | (v[2] << 8)),
                (1, 32) => (short)(BinaryPrimitives.ReadInt32LittleEndian(v) >> 16),
                (3, 32) => Float(BinaryPrimitives.ReadSingleLittleEndian(v)),
                _ => Float(BinaryPrimitives.ReadDoubleLittleEndian(v)),
            };
        }
        return samples;
    }
    private static short Float(double value)
    {
        return double.IsNaN(value) ? (short)0 : (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);
    }
}
