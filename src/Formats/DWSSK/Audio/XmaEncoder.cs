using DWSSK.Formats.Core;

namespace DWSSK.Formats.Audio;

internal static class XmaEncoder
{
    private const int FrameSize = 512;
    private const int Delay = 64;
    private const int PayloadBits = (Xma.PacketSize - 4) * 8;
    private const int PacketsPerBlock = 8;
    private const double Precision = 6;
    private const double Mask = 30;
    private const double Silence = 8;

    public static byte[] Encode(byte[] orig, short[] x, int rate, int nch)
    {
        if (rate is not (24000 or 32000 or 44100 or 48000))
        {
            throw new InvalidDataException($"XMA2 plays at 24000, 32000, 44100 or 48000 Hz, and this WAV is {rate} Hz; resample it first");
        }
        if (nch is < 1 or > 2)
        {
            throw new NotSupportedException("XMA2 is written as one stream of one or two channels");
        }
        WemInfo info = Wem.Info(orig);
        int length = x.Length / nch;
        int frames = ((Delay + length + FrameSize - 1) / FrameSize) + 1;
        int trimEnd = (frames * FrameSize) - 384 - length;
        WmaProStream layout = new(rate, nch);
        int bands = layout.NumSfb(0);
        int[] offsets = layout.SfbOffsets(0);
        double[] window = new double[2 * FrameSize];
        for (int n = 0; n < window.Length; n++)
        {
            window[n] = Math.Sin(Math.PI * (n + 0.5) / window.Length);
        }
        double[] block = new double[2 * FrameSize];
        double[][] coeffs = [.. Enumerable.Range(0, nch).Select(_ => new double[FrameSize])];
        List<MsbWriter> encoded = [];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < nch; c++)
            {
                for (int n = 0; n < block.Length; n++)
                {
                    int t = ((f - 1) * FrameSize) + n - Delay;
                    block[n] = t >= 0 && t < length ? x[(t * nch) + c] * window[n] : 0;
                }
                Dct4.Forward(block, coeffs[c]);
            }
            double boost = 0;
            MsbWriter frame;
            do
            {
                frame = Frame(coeffs, nch, bands, offsets, f == 0, f == frames - 1, trimEnd, boost);
                boost += 3;
            }
            while (frame.Bits > PayloadBits - 32);
            encoded.Add(frame);
        }
        (byte[] data, int[] counts, long endBit) = Pack(encoded);
        return Assemble(orig, info, data, counts, endBit, frames, length, rate, nch);
    }

    private static MsbWriter Frame(double[][] coeffs, int nch, int bands, int[] offsets, bool first, bool last, int trimEnd, double boost)
    {
        MsbWriter w = new(4096);
        w.Put(0, 15);
        w.Put(1, 1);
        w.Put(0, 1);
        if (nch > 1)
        {
            w.Put(0, 1);
        }
        w.Put(0, 8);
        if (first || last)
        {
            w.Put(1, 1);
            w.Put(first ? 1u : 0, 1);
            if (first)
            {
                w.Put(FrameSize, 10);
            }
            w.Put(last ? 1u : 0, 1);
            if (last)
            {
                w.Put((uint)trimEnd, 10);
            }
        }
        else
        {
            w.Put(0, 1);
        }
        w.Put(0, 1);
        w.Put(0, 1);
        if (nch > 1)
        {
            w.Put(0, 1);
            w.Put(1, 1);
            w.Put(0, 1);
        }
        double[][] targets = [.. coeffs.Select(c => Targets(c, bands, offsets, boost))];
        int[] steps = [.. targets.Select(t => (int)Math.Ceiling(t.Max()))];
        int floorStep = steps.Max() - 128;
        steps = [.. steps.Select(q => Math.Max(q, floorStep))];
        ChannelPlan[] plans = [.. coeffs.Select((c, i) => Plan(c, targets[i], steps[i], offsets))];
        bool any = false;
        foreach (ChannelPlan p in plans)
        {
            w.Put(p.Transmit ? 1u : 0, 1);
            any |= p.Transmit;
        }
        if (any)
        {
            w.Put(1, 1);
            foreach (ChannelPlan p in plans)
            {
                w.Put((uint)(p.Vectors >> 2), 8);
            }
            int quantStep = Enumerable.Range(0, nch).Where(i => plans[i].Transmit).Min(i => steps[i]);
            WriteQuantStep(w, quantStep);
            if (nch > 1)
            {
                int[] mods = [.. Enumerable.Range(0, nch).Select(i => plans[i].Transmit ? steps[i] - quantStep : 0)];
                int top = mods.Max();
                int bits = top <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(top - 1));
                w.Put((uint)bits, 3);
                foreach (int m in mods)
                {
                    w.Put(m > 0 ? 1u : 0, 1);
                    if (m > 0 && bits > 0)
                    {
                        w.Put((uint)(m - 1), bits);
                    }
                }
            }
            foreach (ChannelPlan p in plans)
            {
                int step = p.Transmit ? p.Step : 1;
                w.Put((uint)(step - 1), 2);
                int val = 45 / step;
                for (int b = 0; b < bands; b++)
                {
                    int sf = p.Transmit ? p.ScaleFactors[b] : val;
                    WmaProStream.ScaleVlc.Write(w, sf - val);
                    val = sf;
                }
            }
            foreach (ChannelPlan p in plans)
            {
                if (p.Transmit)
                {
                    Coefficients(w, p.Values, p.Vectors);
                }
            }
        }
        w.Put(1, 1);
        return w;
    }

    private sealed record ChannelPlan(bool Transmit, int Step, int[] ScaleFactors, int[] Values, int Vectors);

    private static double[] Targets(double[] coeffs, int bands, int[] offsets, double boost)
    {
        double[] target = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            double energy = 0;
            int end = Math.Min(offsets[b + 1], FrameSize);
            for (int k = offsets[b]; k < end; k++)
            {
                energy += coeffs[k] * coeffs[k];
            }
            double rms = Math.Sqrt(energy / Math.Max(1, end - offsets[b]));
            target[b] = (20 * Math.Log10(Math.Max(rms, Silence) / Precision)) + boost;
        }
        double loudest = target.Max();
        for (int b = 0; b < bands; b++)
        {
            target[b] = Math.Max(target[b], loudest - Mask);
        }
        return target;
    }

    private static ChannelPlan Plan(double[] coeffs, double[] target, int quantStep, int[] offsets)
    {
        int bands = target.Length;
        double range = quantStep - target.Min();
        int step = Math.Clamp((int)Math.Ceiling(range / 60), 1, 4);
        int[] sf = new int[bands];
        int[] values = new int[FrameSize];
        int lastNonZero = -1;
        for (int b = 0; b < bands; b++)
        {
            int drop = Math.Clamp((int)Math.Floor((quantStep - target[b]) / step), 0, 60);
            sf[b] = 60 - drop;
            double q = Math.Pow(10, (quantStep - (drop * step)) / 20.0);
            int end = Math.Min(offsets[b + 1], FrameSize);
            for (int k = offsets[b]; k < end; k++)
            {
                values[k] = (int)Math.Round(-coeffs[k] / q, MidpointRounding.AwayFromZero);
                if (values[k] != 0)
                {
                    lastNonZero = k;
                }
            }
        }
        int vectors = Math.Min(FrameSize, (lastNonZero + 4) & ~3);
        return new ChannelPlan(lastNonZero >= 0, step, sf, values, vectors);
    }

    private static void WriteQuantStep(MsbWriter w, int quantStep)
    {
        int step = quantStep - 90;
        if (step is > -32 and < 31)
        {
            w.Put((uint)step & 63, 6);
            return;
        }
        bool up = step >= 31;
        w.Put(up ? 31u : 32u, 6);
        int rest = up ? step - 31 : -32 - step;
        while (rest >= 31)
        {
            w.Put(31, 5);
            rest -= 31;
        }
        w.Put((uint)rest, 5);
    }

    private static int LargeBits(uint v)
    {
        return v < 256 ? 9 : v < 65536 ? 18 : v < 16777216 ? 27 : 34;
    }

    private static void Large(MsbWriter w, uint v)
    {
        if (v < 256)
        {
            w.Put(0, 1);
            w.Put(v, 8);
        }
        else if (v < 65536)
        {
            w.Put(2, 2);
            w.Put(v, 16);
        }
        else if (v < 16777216)
        {
            w.Put(6, 3);
            w.Put(v, 24);
        }
        else
        {
            w.Put(7, 3);
            w.Put(v, 31);
        }
    }

    private static int Vec1Cost(int v)
    {
        return v < 100 ? WmaProStream.Vec1Vlc.Length(v) : WmaProStream.Vec1Vlc.Length(100) + LargeBits((uint)(v - 100));
    }

    private static void Vec1(MsbWriter w, int v)
    {
        if (v < 100)
        {
            WmaProStream.Vec1Vlc.Write(w, v);
            return;
        }
        WmaProStream.Vec1Vlc.Write(w, 100);
        Large(w, (uint)(v - 100));
    }

    private static int PairCost(int a, int b)
    {
        int sym = (a << 4) | b;
        return a < 16 && b < 16 && WmaProStream.Vec2Vlc.Has(sym)
            ? WmaProStream.Vec2Vlc.Length(sym)
            : WmaProStream.Vec2Vlc.Length(-1) + Vec1Cost(a) + Vec1Cost(b);
    }

    private static void Pair(MsbWriter w, int a, int b)
    {
        int sym = (a << 4) | b;
        if (a < 16 && b < 16 && WmaProStream.Vec2Vlc.Has(sym))
        {
            WmaProStream.Vec2Vlc.Write(w, sym);
            return;
        }
        WmaProStream.Vec2Vlc.Write(w, -1);
        Vec1(w, a);
        Vec1(w, b);
    }

    private static void Coefficients(MsbWriter w, int[] values, int vectors)
    {
        Vlc eob0 = WmaProStream.CoefVlc(0);
        Vlc eob1 = WmaProStream.CoefVlc(1);
        int table = eob1.Length(1) < eob0.Length(1) ? 1 : 0;
        w.Put((uint)table, 1);
        Span<int> a = stackalloc int[4];
        for (int g = 0; g < vectors; g += 4)
        {
            for (int k = 0; k < 4; k++)
            {
                a[k] = Math.Abs(values[g + k]);
            }
            int sym = a[0] < 16 && a[1] < 16 && a[2] < 16 && a[3] < 16 ? (a[0] << 12) | (a[1] << 8) | (a[2] << 4) | a[3] : -2;
            int direct = sym >= 0 && WmaProStream.Vec4Vlc.Has(sym) ? WmaProStream.Vec4Vlc.Length(sym) : int.MaxValue;
            int escaped = WmaProStream.Vec4Vlc.Length(-1) + PairCost(a[0], a[1]) + PairCost(a[2], a[3]);
            if (direct <= escaped)
            {
                WmaProStream.Vec4Vlc.Write(w, sym);
            }
            else
            {
                WmaProStream.Vec4Vlc.Write(w, -1);
                Pair(w, a[0], a[1]);
                Pair(w, a[2], a[3]);
            }
            for (int k = 0; k < 4; k++)
            {
                if (values[g + k] != 0)
                {
                    w.Put(values[g + k] > 0 ? 1u : 0, 1);
                }
            }
        }
        if (vectors < FrameSize)
        {
            WmaProStream.CoefVlc(table).Write(w, 1);
        }
    }

    private static (byte[] Data, int[] Counts, long EndBit) Pack(List<MsbWriter> frames)
    {
        long[] starts = new long[frames.Count];
        int[] lengths = [.. frames.Select(f => f.Bits + 1)];
        Dictionary<long, int> perPacket = [];
        long pos = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            int len = lengths[i];
            if (len > 0x7FFF)
            {
                throw new InvalidDataException("an XMA2 frame came out longer than the format allows");
            }
            while (true)
            {
                long packet = pos / PayloadBits;
                long blockEnd = ((packet / PacketsPerBlock) + 1) * PacketsPerBlock * PayloadBits;
                if (PayloadBits - (pos % PayloadBits) < 16 || perPacket.GetValueOrDefault(packet) >= 63)
                {
                    pos = (packet + 1) * PayloadBits;
                    continue;
                }
                if (pos + len > blockEnd)
                {
                    pos = blockEnd;
                    continue;
                }
                break;
            }
            starts[i] = pos;
            perPacket[pos / PayloadBits] = perPacket.GetValueOrDefault(pos / PayloadBits) + 1;
            pos += len;
        }
        int packets = (int)((pos + PayloadBits - 1) / PayloadBits);
        byte[] data = new byte[packets * Xma.PacketSize];
        for (int p = 0; p < packets; p++)
        {
            data.AsSpan((p * Xma.PacketSize) + 4, Xma.PacketSize - 4).Fill(0xFF);
        }
        for (int i = 0; i < frames.Count; i++)
        {
            MsbWriter f = frames[i];
            bool more = i + 1 < frames.Count && starts[i + 1] / PayloadBits == starts[i] / PayloadBits;
            byte[] bits = f.Data;
            int total = lengths[i];
            for (int b = 0; b < total; b++)
            {
                int bit = b < 15 ? (total >> (14 - b)) & 1 : b < total - 1 ? (bits[b >> 3] >> (7 - (b & 7))) & 1 : more ? 1 : 0;
                long at = starts[i] + b;
                long byteAt = ((at / PayloadBits) * Xma.PacketSize) + 4 + ((at % PayloadBits) >> 3);
                int mask = 0x80 >> (int)(at & 7);
                data[byteAt] = (byte)(bit != 0 ? data[byteAt] | mask : data[byteAt] & ~mask);
            }
        }
        int[] counts = new int[packets];
        for (int p = 0; p < packets; p++)
        {
            counts[p] = perPacket.GetValueOrDefault(p);
            long packetStart = (long)p * PayloadBits;
            int carry = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (starts[i] < packetStart && starts[i] + lengths[i] > packetStart)
                {
                    carry = (int)Math.Min(PayloadBits, starts[i] + lengths[i] - packetStart);
                }
            }
            uint header = ((uint)counts[p] << 26) | ((uint)carry << 11) | (1u << 8);
            Bin.PutU32(data, p * Xma.PacketSize, header, true);
        }
        long end = starts[^1] + lengths[^1];
        long endBit = ((end / PayloadBits) * Xma.PacketSize * 8) + 32 + (end % PayloadBits);
        return (data, counts, endBit);
    }

    private static byte[] Assemble(byte[] orig, WemInfo info, byte[] data, int[] counts, long endBit, int frames, int length, int rate, int nch)
    {
        bool big = info.Big;
        byte[] fmt = Bin.Slice(orig, info.Fmt.Offset, info.Fmt.Offset + info.Fmt.Size).ToArray();
        int encoded = frames * FrameSize;
        int blocks = (counts.Length + PacketsPerBlock - 1) / PacketsPerBlock;
        Bin.PutU16(fmt, 2, (uint)nch, big);
        Bin.PutU32(fmt, 4, (uint)rate, big);
        Bin.PutU32(fmt, 8, (uint)((long)data.Length * rate / encoded), big);
        Bin.PutU16(fmt, 18, 1, big);
        Bin.PutU32(fmt, 20, nch == 1 ? 4u : 3u, big);
        Bin.PutU32(fmt, 24, (uint)encoded, big);
        Bin.PutU32(fmt, 28, PacketsPerBlock * Xma.PacketSize, big);
        Bin.PutU32(fmt, 32, 0, big);
        Bin.PutU32(fmt, 36, (uint)length, big);
        Bin.PutU32(fmt, 40, 0, big);
        Bin.PutU32(fmt, 44, 0, big);
        fmt[48] = 0;
        Bin.PutU16(fmt, 50, (uint)blocks, big);
        if (fmt.Length >= 60)
        {
            Bin.PutU32(fmt, 56, (uint)endBit, big);
        }
        ByteWriter seek = new();
        int sum = 0;
        for (int b = 0; b < blocks; b++)
        {
            sum += counts.Skip(b * PacketsPerBlock).Take(PacketsPerBlock).Sum();
            seek.U32((uint)(sum * FrameSize), big);
        }
        Dictionary<string, byte[]> chunks = new() { ["fmt "] = fmt, ["seek"] = seek.ToArray(), ["data"] = data };
        if (info.Chunks.ContainsKey("JUNK"))
        {
            int before = 12;
            foreach (string id in Wem.ChunkMap(orig, out _).Keys)
            {
                if (id == "data")
                {
                    break;
                }
                if (id != "JUNK")
                {
                    int size = chunks.TryGetValue(id, out byte[]? c) ? c.Length : info.Chunks[id].Size;
                    before += 8 + size + (size & 1);
                }
            }
            chunks["JUNK"] = new byte[Bin.Mod(-(before + 8 + 8), Xma.PacketSize)];
        }
        return Wem.Rebuild(orig, chunks);
    }
}
