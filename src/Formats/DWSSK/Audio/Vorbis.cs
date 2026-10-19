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
    internal static byte[] Codebook(long i)
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
    internal static BitStream Header(int kind)
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
}
