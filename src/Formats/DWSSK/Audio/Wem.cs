using DWSSK.Formats.Core;
namespace DWSSK.Formats.Audio;
internal sealed class WemInfo
{
    public bool Big { get; init; }
    public int Tag { get; init; }
    public int Channels { get; init; }
    public int Rate { get; init; }
    public int Align { get; init; }
    public int Bits { get; init; }
    public required byte[] Extra { get; init; }
    public (int Offset, int Size) Fmt { get; init; }
    public required byte[] Data { get; init; }
    public required Dictionary<string, (int Offset, int Size)> Chunks { get; init; }
}
internal static class Wem
{
    public static bool IsWem(ReadOnlySpan<byte> b)
    {
        if (b.Length < 22 || !(b[..4].SequenceEqual("RIFF"u8) || b[..4].SequenceEqual("RIFX"u8)) || !b.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }
        if (!b.Slice(12, 4).SequenceEqual("fmt "u8))
        {
            return false;
        }
        bool big = b[3] == (byte)'X';
        int tag = Bin.U16(b, 20, big);
        return big || tag is 0xFFFE or 0x0002 or 0x0166 or 0xFFFF;
    }
    public static Dictionary<string, (int Offset, int Size)> ChunkMap(ReadOnlySpan<byte> b, out bool big)
    {
        bool riff = b.Length >= 4 && b[..4].SequenceEqual("RIFF"u8);
        big = !riff;
        if (b.Length < 12 || !(riff || b[..4].SequenceEqual("RIFX"u8)) || !b.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a RIFF/RIFX WAVE");
        }
        Dictionary<string, (int, int)> output = [];
        long o = 12;
        while (o + 8 <= b.Length)
        {
            string cid = Bin.Latin1(b.Slice((int)o, 4));
            long size = Bin.U32(b, (int)o + 4, big);
            output[cid] = ((int)o + 8, (int)Math.Min(size, int.MaxValue));
            o += 8 + size + (size & 1);
        }
        return output;
    }
    public static WemInfo Info(byte[] b)
    {
        Dictionary<string, (int Offset, int Size)> ch = ChunkMap(b, out bool big);
        if (!ch.TryGetValue("fmt ", out (int Offset, int Size) fmt))
        {
            throw new KeyNotFoundException("'fmt '");
        }
        if (!ch.TryGetValue("data", out (int Offset, int Size) data))
        {
            throw new KeyNotFoundException("'data'");
        }
        return new WemInfo
        {
            Big = big,
            Tag = Bin.U16(b, fmt.Offset, big),
            Channels = Bin.U16(b, fmt.Offset + 2, big),
            Rate = (int)Bin.U32(b, fmt.Offset + 4, big),
            Align = Bin.U16(b, fmt.Offset + 12, big),
            Bits = Bin.U16(b, fmt.Offset + 14, big),
            Extra = fmt.Size > 16 ? Bin.Slice(b, fmt.Offset + 18, fmt.Offset + fmt.Size).ToArray() : [],
            Fmt = fmt,
            Data = Bin.Slice(b, data.Offset, data.Offset + data.Size).ToArray(),
            Chunks = ch,
        };
    }
    public static string CodecName(WemInfo i)
    {
        return i.Tag switch
        {
            0xFFFE or 0x0001 => "PCM",
            0x0002 when i.Extra.Length == 0x0C + (i.Channels * 0x2E) => "Nintendo DSP ADPCM",
            0x0002 => "Wwise IMA ADPCM",
            0x0166 => "XMA2",
            0xFFFF => "Wwise Vorbis",
            _ => $"codec 0x{i.Tag:x}",
        };
    }
    public static short[] DspDecode(ReadOnlySpan<byte> data, ReadOnlySpan<byte> header, bool big, int nsamples)
    {
        short[] coef = new short[16];
        for (int k = 0; k < 16; k++)
        {
            coef[k] = Bin.I16(header, 2 * k, big);
        }
        long yn1 = Bin.I16(header, 0x24, big);
        long yn2 = Bin.I16(header, 0x26, big);
        short[] output = new short[nsamples];
        int n = 0;
        for (int f = 0; f < data.Length - 7; f += 8)
        {
            int ps = data[f];
            long c1 = coef[2 * ((ps >> 4) & 7)];
            long c2 = coef[(2 * ((ps >> 4) & 7)) + 1];
            long scale = 1L << (ps & 15);
            for (int k = 0; k < 14; k++)
            {
                if (n >= nsamples)
                {
                    return output;
                }
                int bt = data[f + 1 + (k / 2)];
                long nib = k % 2 == 0 ? bt >> 4 : bt & 15;
                if (nib >= 8)
                {
                    nib -= 16;
                }
                long s = (((nib * scale) << 11) + (c1 * yn1) + (c2 * yn2) + 1024) >> 11;
                s = Math.Clamp(s, -32768, 32767);
                output[n++] = (short)s;
                yn2 = yn1;
                yn1 = s;
            }
        }
        return output[..n];
    }
    private static readonly int[] s_imaStep =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73,
        80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494,
        544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499,
        2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487,
        12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];
    private static readonly int[] s_imaIndex = [-1, -1, -1, -1, 2, 4, 6, 8];
    public static short[] ImaDecode(ReadOnlySpan<byte> data, bool big, int align)
    {
        if (align <= 4)
        {
            throw new InvalidDataException($"IMA block size {align}");
        }
        List<short> output = [];
        for (int blk = 0; blk <= data.Length - align; blk += align)
        {
            int pred = Bin.I16(data, blk, big);
            int idx = Math.Min(88, (int)data[blk + 2]);
            output.Add((short)pred);
            for (int i = 0; i < ((align - 4) * 2) - 1; i++)
            {
                int nib = (data[blk + 4 + (i >> 1)] >> ((i & 1) * 4)) & 15;
                pred = ImaStep(pred, idx, nib);
                idx = Math.Clamp(idx + s_imaIndex[nib & 7], 0, 88);
                output.Add((short)pred);
            }
        }
        return [.. output];
    }
    private static int ImaStep(int pred, int idx, int nib)
    {
        int diff = s_imaStep[idx] * (((nib & 7) * 2) + 1) >> 3;
        return Math.Clamp((nib & 8) != 0 ? pred - diff : pred + diff, -32768, 32767);
    }
    public static (short[] Samples, int Rate, int Channels) Decode(byte[] b)
    {
        WemInfo i = Info(b);
        bool big = i.Big;
        int nch = i.Channels;
        if (i.Tag is 0xFFFE or 0x0001)
        {
            short[] s = new short[i.Data.Length / 2];
            for (int k = 0; k < s.Length; k++)
            {
                s[k] = Bin.I16(i.Data, 2 * k, big);
            }
            return (s, i.Rate, nch);
        }
        if (i.Tag == 0x0002 && i.Extra.Length == 0x0C + (nch * 0x2E))
        {
            int nsamples = (int)Bin.U32(i.Extra, 6, big);
            if (nch != 1)
            {
                throw new NotSupportedException("multi-channel DSP");
            }
            return (DspDecode(i.Data, i.Extra.AsSpan(0x0A, 0x2E), big, nsamples), i.Rate, nch);
        }
        if (i.Tag == 0x0002)
        {
            if (nch != 1)
            {
                throw new NotSupportedException("multi-channel IMA");
            }
            return (ImaDecode(i.Data, big, i.Align), i.Rate, nch);
        }
        if (i.Tag == 0x0166)
        {
            return (Xma.Decode(i), i.Rate, nch);
        }
        if (i.Tag == 0xFFFF)
        {
            return (VorbisDecoder.Decode(i, b), i.Rate, nch);
        }
        throw new NotSupportedException($"codec 0x{i.Tag:x}");
    }
    public static byte[] ToWav(ReadOnlySpan<short> samples, int rate, int nch)
    {
        ByteWriter o = new(44 + (samples.Length * 2));
        int data = samples.Length * 2;
        o.Add("RIFF"u8);
        o.U32((uint)(36 + data), false);
        o.Add("WAVE"u8);
        o.Add("fmt "u8);
        o.U32(16, false);
        o.U16(1, false);
        o.U16((uint)nch, false);
        o.U32((uint)rate, false);
        o.U32((uint)(rate * nch * 2), false);
        o.U16((uint)(nch * 2), false);
        o.U16(16, false);
        o.Add("data"u8);
        o.U32((uint)data, false);
        foreach (short s in samples)
        {
            o.U16((ushort)s, false);
        }
        return o.ToArray();
    }
    public static byte[] ImaEncode(short[] x0, bool big, int align)
    {
        int per = (align - 4) * 2;
        short[] x = new short[x0.Length + Bin.Mod(-x0.Length, per)];
        x0.CopyTo(x, 0);
        ByteWriter output = new();
        int idx = 0;
        for (int blk = 0; blk < x.Length; blk += per)
        {
            int pred = x[blk];
            output.U16((ushort)(short)pred, big);
            output.Add((byte)idx);
            output.Add(0);
            int[] nibs = new int[per];
            for (int i = 1; i < per; i++)
            {
                int best = 0;
                for (int nib = 1; nib < 16; nib++)
                {
                    if (Math.Abs(x[blk + i] - ImaStep(pred, idx, nib)) < Math.Abs(x[blk + i] - ImaStep(pred, idx, best)))
                    {
                        best = nib;
                    }
                }
                pred = ImaStep(pred, idx, best);
                idx = Math.Clamp(idx + s_imaIndex[best & 7], 0, 88);
                nibs[i - 1] = best;
            }
            for (int k = 0; k < per; k += 2)
            {
                output.Add((byte)(nibs[k] | (nibs[k + 1] << 4)));
            }
        }
        return output.ToArray();
    }
    public static long[,] DspCoefficients(short[] x0)
    {
        double[] x = [.. x0.Select(v => (double)v)];
        List<double[]> pairs = [];
        for (int k = 0; k < x.Length - 15; k += 14)
        {
            double[] a1 = new double[14];
            double[] a2 = new double[14];
            double[] y = new double[14];
            double abs = 0;
            for (int i = 0; i < 14; i++)
            {
                a1[i] = x[k + 1 + i];
                a2[i] = x[k + i];
                y[i] = x[k + 2 + i];
            }
            for (int i = 0; i < 14; i++)
            {
                abs += Math.Abs(a1[i]);
                abs += Math.Abs(a2[i]);
            }
            if (abs > 0)
            {
                pairs.Add(LeastSquares.Solve2(a1, a2, y));
            }
        }
        if (pairs.Count == 0)
        {
            pairs.Add([0.0, 0.0]);
        }
        foreach (double[] p in pairs)
        {
            p[0] = Math.Clamp(p[0], -15.9, 15.9);
            p[1] = Math.Clamp(p[1], -15.9, 15.9);
        }
        double[,] centres = { { 0, 0 }, { 1, 0 }, { 2, -1 }, { 1.5, -0.6 }, { 0.9, 0 }, { 1.8, -0.85 }, { 1.2, -0.3 }, { 0.5, 0 } };
        int[] near = new int[pairs.Count];
        for (int iter = 0; iter < 20; iter++)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                double best = double.PositiveInfinity;
                int at = 0;
                for (int c = 0; c < 8; c++)
                {
                    double dx = pairs[i][0] - centres[c, 0];
                    double dy = pairs[i][1] - centres[c, 1];
                    double dist = (dx * dx) + (dy * dy);
                    if (c == 0 || dist < best)
                    {
                        best = dist;
                        at = c;
                    }
                }
                near[i] = at;
            }
            for (int c = 0; c < 8; c++)
            {
                double sx = 0;
                double sy = 0;
                int count = 0;
                for (int i = 0; i < pairs.Count; i++)
                {
                    if (near[i] == c)
                    {
                        sx += pairs[i][0];
                        sy += pairs[i][1];
                        count++;
                    }
                }
                if (count > 0)
                {
                    centres[c, 0] = sx / count;
                    centres[c, 1] = sy / count;
                }
            }
        }
        long[,] coefs = new long[8, 2];
        for (int c = 0; c < 8; c++)
        {
            coefs[c, 0] = (long)Math.Round(centres[c, 0] * 2048, MidpointRounding.ToEven);
            coefs[c, 1] = (long)Math.Round(centres[c, 1] * 2048, MidpointRounding.ToEven);
        }
        return coefs;
    }
    public static (byte[] Frames, List<(int Ps, long Yn1, long Yn2)> States) DspEncode(short[] x0, long[,] coefs)
    {
        long[] x = new long[x0.Length + Bin.Mod(-x0.Length, 14)];
        for (int k = 0; k < x0.Length; k++)
        {
            x[k] = x0[k];
        }
        const int Cands = 8 * 13;
        long[] c1 = new long[Cands];
        long[] c2 = new long[Cands];
        long[] scale = new long[Cands];
        for (int c = 0; c < 8; c++)
        {
            for (int sc = 0; sc < 13; sc++)
            {
                c1[(c * 13) + sc] = coefs[c, 0];
                c2[(c * 13) + sc] = coefs[c, 1];
                scale[(c * 13) + sc] = 1L << sc;
            }
        }
        ByteWriter output = new();
        List<(int, long, long)> states = [];
        long yn1 = 0;
        long yn2 = 0;
        long[] h1 = new long[Cands];
        long[] h2 = new long[Cands];
        double[] err = new double[Cands];
        long[,] nibs = new long[Cands, 14];
        for (int f = 0; f < x.Length; f += 14)
        {
            Array.Fill(h1, yn1);
            Array.Fill(h2, yn2);
            Array.Clear(err);
            for (int k = 0; k < 14; k++)
            {
                for (int i = 0; i < Cands; i++)
                {
                    long pred = (c1[i] * h1[i]) + (c2[i] * h2[i]);
                    double q = (double)((x[f + k] * 2048) - pred - 1024) / (scale[i] * 2048);
                    long nib = (long)Math.Clamp(Math.Round(q, MidpointRounding.ToEven), -8, 7);
                    long s = Math.Clamp(((nib * scale[i] * 2048) + pred + 1024) >> 11, -32768, 32767);
                    long e = s - x[f + k];
                    err[i] += e * e;
                    nibs[i, k] = nib;
                    h2[i] = h1[i];
                    h1[i] = s;
                }
            }
            int best = 0;
            for (int i = 1; i < Cands; i++)
            {
                if (err[i] < err[best])
                {
                    best = i;
                }
            }
            int ps = ((best / 13) << 4) | (best % 13);
            states.Add((ps, yn1, yn2));
            output.Add((byte)ps);
            for (int k = 0; k < 14; k += 2)
            {
                output.Add((byte)(((nibs[best, k] & 15) << 4) | (nibs[best, k + 1] & 15)));
            }
            yn1 = h1[best];
            yn2 = h2[best];
        }
        return (output.ToArray(), states);
    }
    public static byte[] Encode(byte[] orig, short[] x, int rate, int nch)
    {
        WemInfo i = Info(orig);
        bool big = i.Big;
        int tag = i.Tag;
        if (nch != i.Channels)
        {
            throw new InvalidDataException($"the original has {i.Channels} channel(s), the new audio {nch}");
        }
        (int fo, int fs) = i.Fmt;
        byte[] fmt = Bin.Slice(orig, fo, fo + fs).ToArray();
        Dictionary<string, byte[]> nw = [];
        long? loop = null;
        if (i.Chunks.TryGetValue("smpl", out (int Offset, int Size) smplAt))
        {
            byte[] smpl = Bin.Slice(orig, smplAt.Offset, smplAt.Offset + smplAt.Size).ToArray();
            long oldN = Decode(orig).Samples.Length / nch;
            long n = x.Length / nch;
            long count = Bin.U32(smpl, 28, big);
            for (int k = 0; k < count; k++)
            {
                int at = 36 + (24 * k) + 8;
                long st = Bin.U32(smpl, at, big);
                long en = Bin.U32(smpl, at + 4, big);
                en = en >= oldN - 1 ? n - 1 : Math.Min(en, n - 1);
                Bin.PutU32(smpl, at, (uint)Math.Min(st, en), big);
                Bin.PutU32(smpl, at + 4, (uint)en, big);
                loop ??= Math.Min(st, en);
            }
            nw["smpl"] = smpl;
        }
        byte[] data;
        if (tag is 0xFFFE or 0x0001)
        {
            ByteWriter d = new(x.Length * 2);
            foreach (short s in x)
            {
                d.U16((ushort)s, big);
            }
            data = d.ToArray();
            Bin.PutU32(fmt, 8, (uint)(rate * 2 * nch), big);
        }
        else if (tag == 0x0002 && nch == 1 && i.Extra.Length == 0x0C + 0x2E)
        {
            long[,] coefs = DspCoefficients(x);
            (data, List<(int Ps, long Yn1, long Yn2)> states) = DspEncode(x, coefs);
            ByteWriter hdr = new();
            for (int c = 0; c < 8; c++)
            {
                hdr.U16((ushort)J.I16(coefs[c, 0]), big);
                hdr.U16((ushort)J.I16(coefs[c, 1]), big);
            }
            hdr.U16(0, big);
            hdr.U16((uint)states[0].Ps, big);
            hdr.U16(0, big);
            hdr.U16(0, big);
            (long Ps, long Y1, long Y2) lp = (0, 0, 0);
            if (loop is long lv)
            {
                byte[] h6 = [.. hdr.Span, 0, 0, 0, 0, 0, 0];
                short[] y = DspDecode(data, h6, big, x.Length);
                lp = (data[(int)(lv / 14 * 8)], lv > 0 ? y[lv - 1] : 0, lv > 1 ? y[lv - 2] : 0);
            }
            Bin.PutU32(fmt, 18 + 6, (uint)x.Length, big);
            hdr.U16((uint)lp.Ps, big);
            hdr.U16((ushort)J.I16(lp.Y1), big);
            hdr.U16((ushort)J.I16(lp.Y2), big);
            hdr.Span.CopyTo(fmt.AsSpan(18 + 0x0A, 0x2E));
        }
        else if (tag == 0x0002 && nch == 1)
        {
            data = ImaEncode(x, big, i.Align);
            Bin.PutU32(fmt, 8, (uint)((long)rate * i.Align / ((i.Align - 4) * 2)), big);
        }
        else if (tag == 0xFFFF)
        {
            return VorbisEncoder.Encode(orig, x, rate, nch);
        }
        else if (tag == 0x0166)
        {
            return XmaEncoder.Encode(orig, x, rate, nch);
        }
        else
        {
            throw new NotSupportedException($"no encoder for codec 0x{tag:x} with {nch} channel(s)");
        }
        Bin.PutU32(fmt, 4, (uint)rate, big);
        nw["fmt "] = fmt;
        nw["data"] = data;
        return Rebuild(orig, nw);
    }
    public static byte[] Rebuild(byte[] orig, IReadOnlyDictionary<string, byte[]> chunks)
    {
        bool big = orig[3] == (byte)'X';
        ByteWriter body = new();
        body.Add("WAVE"u8);
        long o = 12;
        while (o + 8 <= orig.Length)
        {
            string cid = Bin.Latin1(orig.AsSpan((int)o, 4));
            long size = Bin.U32(orig, (int)o + 4, big);
            byte[] chunk = chunks.TryGetValue(cid, out byte[]? replaced) ? replaced : Bin.Slice(orig, (int)(o + 8), (int)Math.Min(int.MaxValue, o + 8 + size)).ToArray();
            body.Add(orig.AsSpan((int)o, 4));
            body.U32((uint)chunk.Length, big);
            body.Add(chunk);
            body.Zeros(chunk.Length & 1);
            o += 8 + size + (size & 1);
        }
        ByteWriter output = new();
        output.Add(orig.AsSpan(0, 4));
        output.U32((uint)body.Length, big);
        output.Add(body.Span);
        return output.ToArray();
    }
    public static (short[] Samples, int Rate, int Channels) ReadWav(byte[] b)
    {
        Dictionary<string, (int Offset, int Size)> ch = ChunkMap(b, out bool big);
        if (big)
        {
            throw new InvalidDataException("file does not start with RIFF id");
        }
        (int fo, int fs) = ch.TryGetValue("fmt ", out (int, int) f) ? f : throw new InvalidDataException("fmt chunk and/or data chunk missing");
        (int dof, int ds) = ch.TryGetValue("data", out (int, int) d) ? d : throw new InvalidDataException("fmt chunk and/or data chunk missing");
        int tag = Bin.U16(b, fo, false);
        if (tag == 0xFFFE && fs >= 40)
        {
            tag = Bin.U16(b, fo + 24, false);
        }
        if (tag != 1)
        {
            throw new InvalidDataException($"unknown format: {tag}");
        }
        int nch = Bin.U16(b, fo + 2, false);
        int rate = (int)Bin.U32(b, fo + 4, false);
        int bits = Bin.U16(b, fo + 14, false);
        if (bits != 16)
        {
            throw new InvalidDataException("16-bit WAV expected");
        }
        int frames = Math.Min(ds, b.Length - dof) / (2 * Math.Max(1, nch));
        short[] s = new short[frames * nch];
        for (int k = 0; k < s.Length; k++)
        {
            s[k] = Bin.I16(b, dof + (2 * k), false);
        }
        return (s, rate, nch);
    }
}
internal static class LeastSquares
{
    public static double[] Solve2(double[] c1, double[] c2, double[] y)
    {
        int m = c1.Length;
        double n1 = Norm(c1);
        if (n1 == 0)
        {
            double n2only = Norm(c2);
            return n2only == 0 ? [0, 0] : [0, Dot(c2, y) / (n2only * n2only)];
        }
        double[] q1 = [.. c1.Select(v => v / n1)];
        double r12 = Dot(q1, c2);
        double[] v2 = new double[m];
        for (int i = 0; i < m; i++)
        {
            v2[i] = c2[i] - (r12 * q1[i]);
        }
        double r22 = Norm(v2);
        double b1 = Dot(q1, y);
        double s1 = Math.Sqrt((n1 * n1) + (r12 * r12));
        double tol = 2.220446049250313e-16 * Math.Max(m, 2) * Math.Max(s1, r22);
        if (r22 > tol && r22 > 1e-300)
        {
            double[] q2 = [.. v2.Select(v => v / r22)];
            double b2 = Dot(q2, y);
            double x2 = b2 / r22;
            double x1 = (b1 - (r12 * x2)) / n1;
            return [x1, x2];
        }
        double t = b1 / ((n1 * n1) + (r12 * r12));
        return [n1 * t, r12 * t];
    }
    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }
        return s;
    }
    private static double Norm(double[] a)
    {
        return Math.Sqrt(Dot(a, a));
    }
}
