using System.Numerics;
using DWSSK.Formats.Core;

namespace DWSSK.Formats.Audio;

internal sealed class BitStream(byte[]? data = null)
{
    private readonly byte[] _data = data ?? [];
    private readonly List<byte> _out = [];
    private long _pos;
    private long _n;

    public long Read(int width)
    {
        long v = 0;
        for (int k = 0; k < width; k++)
        {
            long byteIndex = _pos >> 3;
            if (byteIndex >= _data.Length)
            {
                throw new InvalidDataException("index out of range");
            }
            v |= (long)((_data[byteIndex] >> (int)(_pos & 7)) & 1) << k;
            _pos++;
        }
        return v;
    }

    public void Write(long v, int width)
    {
        for (int k = 0; k < width; k++)
        {
            long bit = (v >> k) & 1;
            int byteIndex = (int)(_n >> 3);
            while (_out.Count <= byteIndex)
            {
                _out.Add(0);
            }
            _out[byteIndex] |= (byte)(bit << (int)(_n & 7));
            _n++;
        }
    }

    public long Copy(BitStream src, int width)
    {
        long v = src.Read(width);
        Write(v, width);
        return v;
    }

    public byte[] Bytes()
    {
        int len = (int)((_n + 7) / 8);
        byte[] o = new byte[len];
        for (int i = 0; i < Math.Min(len, _out.Count); i++)
        {
            o[i] = _out[i];
        }
        return o;
    }
}

internal static class Vorbis
{
    private static readonly Lazy<(byte[] Lib, uint[] Offs)> s_codebooks = new(LoadCodebooks);

    private static (byte[], uint[]) LoadCodebooks()
    {
        using Stream stream = typeof(Vorbis).Assembly.GetManifestResourceStream("DWSSK.packed_codebooks_aoTuV_603.bin")
            ?? throw new FileNotFoundException("the Wwise codebook table is missing from the plugin");
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        byte[] lib = ms.ToArray();
        int table = (int)Bin.U32(lib, lib.Length - 4, false);
        int n = (lib.Length - table) / 4;
        return (lib, Bin.U32s(lib, table, n, false));
    }

    private static byte[] Codebook(long i)
    {
        (byte[] lib, uint[] offs) = s_codebooks.Value;
        if (i + 1 >= offs.Length)
        {
            throw new PyIndexException("tuple index out of range");
        }
        return Bin.Slice(lib, (int)offs[i], (int)offs[i + 1]).ToArray();
    }

    public static int Ilog(long v)
    {
        return v <= 0 ? 0 : 64 - BitOperations.LeadingZeroCount((ulong)v);
    }

    public static long Quantvals(long entries, long dims)
    {
        long vals = entries >> (int)((Ilog(entries) - 1) * (dims - 1) / dims);
        while (true)
        {
            BigInteger lo = BigInteger.Pow(vals, (int)dims);
            BigInteger hi = BigInteger.Pow(vals + 1, (int)dims);
            if (lo <= entries && entries < hi)
            {
                return vals;
            }
            vals += lo > entries ? -1 : 1;
        }
    }

    private static void RebuildCodebook(byte[] cb, BitStream output)
    {
        BitStream src = new(cb);
        long dims = src.Read(4);
        long entries = src.Read(14);
        output.Write(0x564342, 24);
        output.Write(dims, 16);
        output.Write(entries, 24);
        if (output.Copy(src, 1) != 0)
        {
            output.Copy(src, 5);
            long cur = 0;
            while (cur < entries)
            {
                cur += output.Copy(src, Ilog(entries - cur));
            }
        }
        else
        {
            int lengthBits = (int)src.Read(3);
            long sparse = src.Read(1);
            output.Write(sparse, 1);
            for (long e = 0; e < entries; e++)
            {
                if (sparse == 0 || output.Copy(src, 1) != 0)
                {
                    output.Write(src.Read(lengthBits), 5);
                }
            }
        }
        long lookup = src.Read(1);
        output.Write(lookup, 4);
        if (lookup == 1)
        {
            output.Copy(src, 32);
            output.Copy(src, 32);
            int valueBits = (int)output.Copy(src, 4) + 1;
            output.Copy(src, 1);
            long q = Quantvals(entries, dims);
            for (long k = 0; k < q; k++)
            {
                output.Copy(src, valueBits);
            }
        }
    }

    private static BitStream Header(int kind)
    {
        BitStream w = new();
        w.Write(kind, 8);
        foreach (byte c in "vorbis"u8)
        {
            w.Write(c, 8);
        }
        return w;
    }

    public static (byte[] Setup, List<long> Modes) RebuildSetup(byte[] pkt, int channels)
    {
        BitStream src = new(pkt);
        BitStream output = Header(5);
        long books = output.Copy(src, 8) + 1;
        for (long k = 0; k < books; k++)
        {
            RebuildCodebook(Codebook(src.Read(10)), output);
        }
        output.Write(0, 6);
        output.Write(0, 16);
        long floors = output.Copy(src, 6) + 1;
        for (long f = 0; f < floors; f++)
        {
            output.Write(1, 16);
            long partitions = output.Copy(src, 5);
            List<long> classes = [];
            for (long p = 0; p < partitions; p++)
            {
                classes.Add(output.Copy(src, 4));
            }
            List<long> dims = [];
            long maxClass = classes.Count > 0 ? classes.Max() : -1;
            for (long c = 0; c <= maxClass; c++)
            {
                dims.Add(output.Copy(src, 3) + 1);
                long sub = output.Copy(src, 2);
                if (sub != 0)
                {
                    output.Copy(src, 8);
                }
                for (long s = 0; s < (1L << (int)sub); s++)
                {
                    output.Copy(src, 8);
                }
            }
            output.Copy(src, 2);
            int rangebits = (int)output.Copy(src, 4);
            foreach (long c in classes)
            {
                for (long d = 0; d < dims[(int)c]; d++)
                {
                    output.Copy(src, rangebits);
                }
            }
        }
        long residues = output.Copy(src, 6) + 1;
        for (long r = 0; r < residues; r++)
        {
            output.Write(src.Read(2), 16);
            output.Copy(src, 24);
            output.Copy(src, 24);
            output.Copy(src, 24);
            long nclass = output.Copy(src, 6) + 1;
            output.Copy(src, 8);
            List<long> cascade = [];
            for (long c = 0; c < nclass; c++)
            {
                long low = output.Copy(src, 3);
                cascade.Add(low | (output.Copy(src, 1) != 0 ? output.Copy(src, 5) << 3 : 0));
            }
            foreach (long c in cascade)
            {
                for (int k = 0; k < 8; k++)
                {
                    if (((c >> k) & 1) != 0)
                    {
                        output.Copy(src, 8);
                    }
                }
            }
        }
        long mappings = output.Copy(src, 6) + 1;
        int chBits = Ilog(channels - 1);
        for (long m = 0; m < mappings; m++)
        {
            output.Write(0, 16);
            long submaps = output.Copy(src, 1) != 0 ? output.Copy(src, 4) + 1 : 1;
            if (output.Copy(src, 1) != 0)
            {
                long steps = output.Copy(src, 8) + 1;
                for (long s = 0; s < steps; s++)
                {
                    output.Copy(src, chBits);
                    output.Copy(src, chBits);
                }
            }
            if (output.Copy(src, 2) != 0)
            {
                throw new InvalidDataException("mapping reserved bits set");
            }
            if (submaps > 1)
            {
                for (int c = 0; c < channels; c++)
                {
                    output.Copy(src, 4);
                }
            }
            for (long s = 0; s < submaps; s++)
            {
                output.Copy(src, 8);
                output.Copy(src, 8);
                output.Copy(src, 8);
            }
        }
        List<long> modes = [];
        long nmodes = output.Copy(src, 6) + 1;
        for (long k = 0; k < nmodes; k++)
        {
            modes.Add(output.Copy(src, 1));
            output.Write(0, 16);
            output.Write(0, 16);
            output.Copy(src, 8);
        }
        output.Write(1, 1);
        return (output.Bytes(), modes);
    }

    private static readonly uint[] s_crc = BuildCrc();

    private static uint[] BuildCrc()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }
            t[i] = c;
        }
        return t;
    }

    private static uint OggCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ s_crc[((crc >> 24) ^ b) & 0xFF];
        }
        return crc;
    }

    private static byte[] OggPage(byte[] packet, long seq, long granule, int flags)
    {
        int full = packet.Length / 255;
        if (full + 1 > 255)
        {
            throw new InvalidDataException("bytes must be in range(0, 256)");
        }
        ByteWriter page = new(packet.Length + 64);
        page.Add("OggS"u8);
        page.Add(0);
        page.Add((byte)flags);
        page.U32((uint)(ulong)granule, false);
        page.U32((uint)((ulong)granule >> 32), false);
        page.U32(0x5757, false);
        page.U32((uint)seq, false);
        page.U32(0, false);
        page.Add((byte)(full + 1));
        page.Fill(255, full);
        page.Add((byte)(packet.Length % 255));
        page.Add(packet);
        page.PatchU32(22, OggCrc(page.Span), false);
        return page.ToArray();
    }

    public static short[] DecodeOgg(byte[] ogg)
    {
        using MemoryStream stream = new(ogg);
        using NVorbis.VorbisReader reader = new(stream, false) { ClipSamples = false };
        List<short> pcm = new((int)Math.Min(int.MaxValue / 2, reader.TotalSamples * reader.Channels));
        float[] buffer = new float[4096 * reader.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (int k = 0; k < read; k++)
            {
                pcm.Add((short)Math.Clamp(MathF.Truncate(buffer[k] * 32768f), -32768f, 32767f));
            }
        }
        return [.. pcm];
    }

    public static byte[] ToOgg(byte[] b)
    {
        WemInfo i = Wem.Info(b);
        bool big = i.Big;
        int nch = i.Channels;
        (int fo, int fs) = i.Fmt;
        if (fs != 0x42)
        {
            throw new NotSupportedException($"Wwise Vorbis with a 0x{fs:x}-byte fmt");
        }
        int v = fo + 0x18;
        long samples = Bin.U32(b, v, big);
        long modSignal = Bin.U32(b, v + 4, big);
        int setupOff = (int)Bin.U32(b, v + 0x10, big);
        int audioOff = (int)Bin.U32(b, v + 0x14, big);
        int bs0 = Bin.U8(b, v + 0x28);
        int bs1 = Bin.U8(b, v + 0x29);
        bool modPackets = modSignal is not (0x4A or 0x4B or 0x69 or 0x70);
        byte[] data = i.Data;
        (byte[] Packet, int Next) Packet(int o)
        {
            int n = Bin.U16(data, o, big);
            return (Bin.Slice(data, o + 2, o + 2 + n).ToArray(), o + 2 + n);
        }
        BitStream ident = Header(1);
        (long Val, int Width)[] identFields =
        [
            (0, 32), (nch, 8), (i.Rate, 32), (0, 32), (Bin.U32(b, fo + 8, big) * 8L, 32), (0, 32), (bs0, 4), (bs1, 4), (1, 1),
        ];
        foreach ((long val, int width) in identFields)
        {
            ident.Write(val, width);
        }
        BitStream comment = Header(3);
        byte[] vendor = "converted from Audiokinetic Wwise by dwssk wem.py"u8.ToArray();
        comment.Write(vendor.Length, 32);
        foreach (byte c in vendor)
        {
            comment.Write(c, 8);
        }
        comment.Write(0, 32);
        comment.Write(1, 1);
        (byte[] setup, List<long> modes) = RebuildSetup(Packet(setupOff).Packet, nch);
        int modeBits = Ilog(modes.Count - 1);
        List<byte[]> pages = [OggPage(ident.Bytes(), 0, 0, 2), OggPage(comment.Bytes(), 1, 0, 0), OggPage(setup, 2, 0, 0)];
        List<byte[]> pkts = [];
        int at = audioOff;
        while (at < data.Length)
        {
            (byte[] p, int next) = Packet(at);
            pkts.Add(p);
            at = next;
        }
        long granule = 0;
        long? prev = null;
        for (int k = 0; k < pkts.Count; k++)
        {
            byte[] p = pkts[k];
            if (modPackets)
            {
                BitStream src = new(p);
                BitStream output = new();
                output.Write(0, 1);
                long mode0 = output.Copy(src, modeBits);
                if (modes[(int)mode0] != 0)
                {
                    long nxt = k + 1 < pkts.Count && pkts[k + 1].Length > 0 ? modes[(int)new BitStream(pkts[k + 1]).Read(modeBits)] : 0;
                    output.Write(prev ?? 0, 1);
                    output.Write(nxt, 1);
                }
                output.Copy(src, 8 - modeBits);
                for (int c = 1; c < p.Length; c++)
                {
                    output.Write(p[c], 8);
                }
                p = output.Bytes();
            }
            long mode = new BitStream(p).Read(1 + modeBits) >> 1;
            long size = 1L << (modes[(int)mode] != 0 ? bs1 : bs0);
            if (prev is { } pv)
            {
                granule += ((1L << (pv != 0 ? bs1 : bs0)) + size) / 4;
            }
            prev = modes[(int)mode];
            bool last = k == pkts.Count - 1;
            pages.Add(OggPage(p, 3 + k, last ? Math.Min(granule, samples) : granule, last ? 4 : 0));
        }
        return [.. pages.SelectMany(x => x)];
    }
}
