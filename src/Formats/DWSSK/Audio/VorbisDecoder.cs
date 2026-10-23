using System.Numerics;
using System.Runtime.InteropServices;
using DWSSK.Formats.Core;
namespace DWSSK.Formats.Audio;
internal static class VorbisDecoder
{
    private const int Lookup1 = 1026;
    private const int FloorTable = 0;
    private const int TrigTable = 256;
    private const int WindowTable = 2306;
    private const float Scale = 1f / 512;
    private static readonly int[] s_quant = [256, 128, 86, 64];
    private static readonly float s_pi1 = BitConverter.Int32BitsToSingle(0x3F6C835E);
    private static readonly float s_pi2 = BitConverter.Int32BitsToSingle(0x3F3504F3);
    private static readonly float s_pi3 = BitConverter.Int32BitsToSingle(0x3EC3EF15);
    private static readonly byte[] s_bitrev = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];
    private static readonly Lazy<float[]> s_tables = new(LoadTables);
    private static float[] LoadTables()
    {
        using Stream stream = typeof(VorbisDecoder).Assembly.GetManifestResourceStream("DWSSK.vorbis_tables.bin")
            ?? throw new FileNotFoundException("the Vorbis decoder tables are missing from the plugin");
        byte[] raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        return MemoryMarshal.Cast<byte, float>(raw).ToArray();
    }
    private static ReadOnlySpan<float> Trig => s_tables.Value.AsSpan(TrigTable, 2050);
    public static short[] Decode(WemInfo info, byte[] wem)
    {
        (int fo, int fs) = info.Fmt;
        if (fs != 0x42)
        {
            throw new NotSupportedException($"Wwise Vorbis with a 0x{fs:x}-byte fmt");
        }
        bool big = info.Big;
        int v = fo + 0x18;
        byte[] data = info.Data;
        int setupAt = (int)Bin.U32(wem, v + 0x10, big);
        int audioAt = (int)Bin.U32(wem, v + 0x14, big);
        int nch = info.Channels;
        if (nch is < 1 or > 255)
        {
            throw new InvalidDataException($"Wwise Vorbis declares {nch} channels, and Vorbis carries 1 to 255");
        }
        Setup setup = ReadSetup(new Reader(data, setupAt + 2, Bin.U16(data, setupAt, big)), nch, 1 << wem[v + 0x28], 1 << wem[v + 0x29]);
        Dsp dsp = new(setup, nch, Bin.U16(wem, fo + 0x32, big));
        short[] pcm = new short[Math.Max(1024, Math.Min(Math.Min(Bin.U32(wem, v, big), data.Length * 4L) * nch, Array.MaxLength))];
        int frames = 0;
        for (int at = audioAt; at + 2 <= data.Length;)
        {
            int size = Bin.U16(data, at, big);
            Synthesis(dsp, new Reader(data, at + 2, size), at + 2 + size >= data.Length);
            int n = dsp.Current - dsp.Returned;
            if (dsp.Returned < dsp.Current)
            {
                if ((frames + n) * nch > pcm.Length)
                {
                    Array.Resize(ref pcm, Math.Max(pcm.Length * 2, (frames + n) * nch));
                }
                for (int c = 0; c < nch; c++)
                {
                    Lap(dsp, c, pcm.AsSpan((frames * nch) + c), nch, dsp.Returned, dsp.Returned + n);
                }
                dsp.Returned += n;
                frames += n;
            }
            at += 2 + size;
        }
        Array.Resize(ref pcm, frames * nch);
        return pcm;
    }
    private sealed class Reader(byte[] data, int start, int size)
    {
        private int _at = start;
        private int _bit;
        public int Left { get; private set; } = size;
        private uint Byte(int k)
        {
            int i = _at + k;
            return (uint)i < (uint)data.Length ? data[i] : 0u;
        }
        public uint Look(int n)
        {
            int end = n + _bit;
            uint v = Byte(0) >> _bit;
            if (end > 8)
            {
                v |= Byte(1) << (8 - _bit);
                if (end > 16)
                {
                    v |= Byte(2) << (16 - _bit);
                    if (end > 24)
                    {
                        v |= Byte(3) << (24 - _bit);
                        if (end > 32 && _bit != 0)
                        {
                            v |= Byte(4) << (32 - _bit);
                        }
                    }
                }
            }
            return n >= 32 ? v : v & ((1u << n) - 1);
        }
        public void Skip(int n)
        {
            n += _bit;
            _at += n >> 3;
            Left -= n >> 3;
            _bit = n & 7;
        }
        public int Read(int n)
        {
            uint v = Look(n);
            Skip(n);
            return (int)v;
        }
    }
    private sealed class Book
    {
        public int Dim;
        public int MaxLength;
        public int Type;
        public int QMin;
        public int QMinP;
        public int QDel;
        public int QDelP;
        public int QBits;
        public int QPack;
        public int[] QVal = [];
        public uint[] Tree = [];
    }
    private sealed class Floor
    {
        public required int[] Partitions;
        public required int[] ClassDim;
        public required int[] ClassSubs;
        public required int[] ClassBook;
        public required int[][] SubBooks;
        public required int Mult;
        public required int[] Posts;
        public required int[] Order;
        public required int[] Low;
        public required int[] High;
    }
    private sealed class Residue
    {
        public int Type;
        public int Begin;
        public int End;
        public int Grouping;
        public int Partitions;
        public int GroupBook;
        public int Stages;
        public required byte[] StageMasks;
        public required byte[] StageBooks;
    }
    private sealed class Mapping
    {
        public int Submaps = 1;
        public int[]? Mux;
        public int[] Magnitude = [];
        public int[] Angle = [];
        public required int[] SubFloor;
        public required int[] SubResidue;
    }
    private sealed class Setup
    {
        public required int[] Blocks;
        public required Book[] Books;
        public required Floor[] Floors;
        public required Residue[] Residues;
        public required Mapping[] Mappings;
        public required (int BlockFlag, int Mapping)[] Modes;
    }
    private sealed class Dsp(Setup setup, int channels, int trim)
    {
        public Setup Setup { get; } = setup;
        public int[][] Pcm { get; } = [.. Enumerable.Range(0, channels).Select(_ => new int[setup.Blocks[1] / 2])];
        public float[][] Right { get; } = [.. Enumerable.Range(0, channels).Select(_ => new float[setup.Blocks[1] / 4])];
        public int[][] Fit { get; } = [.. Enumerable.Range(0, channels).Select(_ => new int[setup.Floors.Max(f => f.Posts.Length)])];
        public int LW;
        public int W;
        public int Returned = -1;
        public int Current = -1;
        public int SkipSamples;
        public int Trim = trim;
        public Span<float> Floats(int c)
        {
            return MemoryMarshal.Cast<int, float>(Pcm[c].AsSpan());
        }
    }
    private static int Ilog(int v)
    {
        return 32 - BitOperations.LeadingZeroCount((uint)v);
    }
    private static int Sraw(int x, int s)
    {
        return (s & 63) >= 32 ? x >> 31 : x >> (s & 63);
    }
    private static int Slw(int x, int s)
    {
        return (s & 63) >= 32 ? 0 : x << (s & 63);
    }
    private static int Shift(int x, int s)
    {
        return s > 0 ? Sraw(x, s) : Slw(x, -s);
    }
    private static (int Mantissa, int Point) Unpack(uint x)
    {
        int mant = (int)(x & 0x1FFFFF);
        int point = (int)((x >> 21) & 0x3FF) - 788;
        if (mant == 0)
        {
            return (0, -9999);
        }
        while ((mant & 0x40000000) == 0)
        {
            mant <<= 1;
            point--;
        }
        return ((x & 0x80000000) != 0 ? -mant : mant, point);
    }
    private static int Quantvals(int entries, int dim)
    {
        int vals = entries >> ((Ilog(entries) - 1) * (dim - 1) / dim);
        while (true)
        {
            int acc = 1;
            int acc1 = 1;
            for (int i = 0; i < dim; i++)
            {
                acc *= vals;
                acc1 *= vals + 1;
            }
            if (acc <= entries && acc1 > entries)
            {
                return vals;
            }
            vals += acc > entries ? -1 : 1;
        }
    }
    private static Setup ReadSetup(Reader r, int channels, int block0, int block1)
    {
        if (block0 < 64 || block1 < block0 || block1 > 0x2000)
        {
            throw new InvalidDataException($"Vorbis block sizes {block0} and {block1}");
        }
        if (block0 < 256 || block1 > 4096)
        {
            throw new NotSupportedException($"the game has no window for Vorbis block sizes {block0} and {block1}");
        }
        Book[] books = new Book[r.Read(8) + 1];
        for (int i = 0; i < books.Length; i++)
        {
            int id = r.Read(10);
            books[i] = ReadBook(new Reader(Vorbis.Codebook(id), 0, 0x36A));
        }
        Floor[] floors = new Floor[r.Read(6) + 1];
        for (int i = 0; i < floors.Length; i++)
        {
            floors[i] = ReadFloor(r, books.Length);
        }
        Residue[] residues = new Residue[r.Read(6) + 1];
        for (int i = 0; i < residues.Length; i++)
        {
            residues[i] = ReadResidue(r, books.Length);
        }
        Mapping[] mappings = new Mapping[r.Read(6) + 1];
        for (int i = 0; i < mappings.Length; i++)
        {
            mappings[i] = ReadMapping(r, channels, floors.Length, residues.Length);
        }
        (int, int)[] modes = new (int, int)[r.Read(6) + 1];
        for (int i = 0; i < modes.Length; i++)
        {
            modes[i] = (r.Read(1), r.Read(8));
            if (modes[i].Item2 >= mappings.Length)
            {
                throw new InvalidDataException("Vorbis mode names a missing mapping");
            }
        }
        return new Setup { Blocks = [block0, block1], Books = books, Floors = floors, Residues = residues, Mappings = mappings, Modes = modes };
    }
    private static Book ReadBook(Reader r)
    {
        Book b = new() { Dim = r.Read(4) };
        int entries = r.Read(14);
        byte[] lengths = new byte[entries];
        if (r.Read(1) == 0)
        {
            int bits = r.Read(3);
            bool sparse = r.Read(1) != 0;
            for (int i = 0; i < entries; i++)
            {
                if (sparse && r.Read(1) == 0)
                {
                    continue;
                }
                int length = r.Read(bits) + 1;
                lengths[i] = (byte)length;
                b.MaxLength = Math.Max(b.MaxLength, length);
            }
        }
        else
        {
            int length = r.Read(5) + 1;
            for (int i = 0; i < entries;)
            {
                int num = r.Read(Ilog(entries - i));
                for (int j = 0; j < num && i < entries; j++, i++)
                {
                    lengths[i] = (byte)length;
                }
                b.MaxLength = length;
                length++;
            }
        }
        int quantvals = 0;
        ushort[] packed = [];
        if (r.Read(1) != 0)
        {
            (b.QMin, b.QMinP) = Unpack((uint)r.Read(32));
            (b.QDel, b.QDelP) = Unpack((uint)r.Read(32));
            b.QBits = r.Read(4) + 1;
            _ = r.Read(1);
            b.QDel >>= b.QBits;
            b.QDelP += b.QBits;
            quantvals = Quantvals(entries, b.Dim);
            int total1 = ((b.QBits * b.Dim) + 8) / 8;
            int total2 = ((b.QBits + 7) / 8) + (((b.Dim * Ilog(quantvals - 1)) + 8) / 8);
            if (total1 <= 4 && total1 <= total2)
            {
                b.Type = 1;
                packed = new ushort[quantvals];
                for (int i = 0; i < quantvals; i++)
                {
                    packed[i] = (ushort)r.Read(b.QBits);
                }
            }
            else
            {
                b.Type = 2;
                b.QPack = Ilog(quantvals - 1);
                b.QVal = new int[quantvals];
                for (int i = 0; i < quantvals; i++)
                {
                    b.QVal[i] = b.QBits <= 8 ? (byte)r.Read(b.QBits) : (ushort)r.Read(b.QBits);
                }
            }
        }
        b.Tree = MakeTree(b, lengths, quantvals, packed);
        return b;
    }
    private static uint Pack(Book b, int entry, int quantvals, ushort[] packed)
    {
        int ret = 0;
        for (int j = 0; b.Type != 0 && j < b.Dim; j++)
        {
            int off = entry % quantvals;
            entry /= quantvals;
            ret |= b.Type == 1 ? Slw(packed[off], b.QBits * j) : Slw(off, b.QPack * j);
        }
        return (uint)(b.Type == 0 ? entry : ret);
    }
    private static uint[] MakeTree(Book b, byte[] lengths, int quantvals, ushort[] packed)
    {
        int used = lengths.Count(l => l != 0);
        if (lengths.Length < 2 || used < 2)
        {
            throw new NotSupportedException("Vorbis codebook with fewer than two entries");
        }
        uint[] r = new uint[used * 2];
        uint[] marker = new uint[33];
        uint top = 0;
        int count = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0)
            {
                continue;
            }
            uint entry = marker[length];
            uint chase = 0;
            if (count != 0 && entry == 0)
            {
                throw new InvalidDataException("overpopulated Vorbis codebook");
            }
            int j = 0;
            for (; j < length - 1; j++)
            {
                uint bit = (entry >> (length - j - 1)) & 1;
                if (chase >= top)
                {
                    top++;
                    r[chase * 2] = top;
                    r[(chase * 2) + 1] = 0;
                }
                else if (r[(chase * 2) + bit] == 0)
                {
                    r[(chase * 2) + bit] = top;
                }
                chase = r[(chase * 2) + bit];
            }
            uint last = (entry >> (length - j - 1)) & 1;
            if (chase >= top)
            {
                top++;
                r[(chase * 2) + 1] = 0;
            }
            r[(chase * 2) + last] = Pack(b, i, quantvals, packed) | 0x80000000;
            count++;
            for (j = length; j > 0; j--)
            {
                if ((marker[j] & 1) != 0)
                {
                    marker[j] = marker[j - 1] << 1;
                    break;
                }
                marker[j]++;
            }
            for (j = length + 1; j < 33; j++)
            {
                if (marker[j] >> 1 != entry)
                {
                    break;
                }
                entry = marker[j];
                marker[j] = marker[j - 1] << 1;
            }
        }
        return r;
    }
    private static uint Entry(Book b, Reader r)
    {
        uint bits = r.Look(b.MaxLength);
        uint chase = 0;
        int i = 0;
        for (; i < b.MaxLength; i++)
        {
            chase = b.Tree[(chase * 2) + ((bits >> i) & 1)];
            if ((chase & 0x80000000) != 0)
            {
                break;
            }
        }
        r.Skip(i + 1);
        return chase & 0x7FFFFFFF;
    }
    private static int Scalar(Book b, Reader r)
    {
        return b.Type != 0 ? -1 : (int)Entry(b, r);
    }
    private static void Map(Book b, Reader r, Span<int> v)
    {
        uint entry = Entry(b, r);
        if (b.Type == 1)
        {
            uint mask = (1u << b.QBits) - 1;
            for (int i = 0; i < b.Dim; i++)
            {
                v[i] = (int)(entry & mask);
                entry >>= b.QBits;
            }
        }
        else
        {
            uint mask = (1u << b.QPack) - 1;
            for (int i = 0; i < b.Dim; i++)
            {
                v[i] = b.QVal[entry & mask];
                entry >>= b.QPack;
            }
        }
        int add = Shift(b.QMin, -8 - b.QMinP);
        int shift = -8 - b.QDelP;
        for (int i = 0; i < b.Dim; i++)
        {
            v[i] = add + Shift(b.QDel * v[i], shift);
        }
    }
    private static Floor ReadFloor(Reader r, int books)
    {
        int[] partitions = new int[r.Read(5)];
        int classes = 0;
        for (int i = 0; i < partitions.Length; i++)
        {
            partitions[i] = r.Read(4);
            classes = Math.Max(classes, partitions[i] + 1);
        }
        int[] dim = new int[classes];
        int[] subs = new int[classes];
        int[] book = new int[classes];
        int[][] subBooks = new int[classes][];
        for (int c = 0; c < classes; c++)
        {
            dim[c] = r.Read(3) + 1;
            subs[c] = r.Read(2);
            book[c] = subs[c] != 0 ? r.Read(8) : 0;
            subBooks[c] = new int[1 << subs[c]];
            for (int k = 0; k < subBooks[c].Length; k++)
            {
                subBooks[c][k] = r.Read(8) - 1;
            }
            if (book[c] >= books || subBooks[c].Any(s => s >= books))
            {
                throw new InvalidDataException("Vorbis floor names a missing codebook");
            }
        }
        int mult = r.Read(2) + 1;
        int range = r.Read(4);
        int[] posts = new int[partitions.Sum(p => dim[p]) + 2];
        posts[1] = 1 << range;
        for (int i = 2; i < posts.Length; i++)
        {
            posts[i] = r.Read(range);
        }
        int[] order = [.. Enumerable.Range(0, posts.Length)];
        Array.Sort((int[])posts.Clone(), order);
        int[] low = new int[posts.Length - 2];
        int[] high = new int[posts.Length - 2];
        for (int j = 0; j < low.Length; j++)
        {
            int lx = 0;
            int hx = posts[1];
            int x = posts[j + 2];
            high[j] = 1;
            for (int k = 0; k < j + 2; k++)
            {
                if (posts[k] > lx && posts[k] < x)
                {
                    low[j] = k;
                    lx = posts[k];
                }
                if (posts[k] < hx && posts[k] > x)
                {
                    high[j] = k;
                    hx = posts[k];
                }
            }
        }
        return new Floor
        {
            Partitions = partitions, ClassDim = dim, ClassSubs = subs, ClassBook = book, SubBooks = subBooks,
            Mult = mult, Posts = posts, Order = order, Low = low, High = high,
        };
    }
    private static Residue ReadResidue(Reader r, int books)
    {
        Residue res = new()
        {
            Type = r.Read(2), Begin = r.Read(24), End = r.Read(24), Grouping = r.Read(24) + 1, Partitions = r.Read(6) + 1,
            GroupBook = r.Read(8), StageMasks = [], StageBooks = [],
        };
        res.StageMasks = new byte[res.Partitions];
        res.StageBooks = new byte[res.Partitions * 8];
        for (int j = 0; j < res.Partitions; j++)
        {
            int mask = r.Read(3);
            if (r.Read(1) != 0)
            {
                mask |= r.Read(5) << 3;
            }
            res.StageMasks[j] = (byte)mask;
        }
        for (int j = 0; j < res.Partitions; j++)
        {
            for (int k = 0; k < 8; k++)
            {
                res.StageBooks[(j * 8) + k] = 0xFF;
                if (((res.StageMasks[j] >> k) & 1) != 0)
                {
                    res.StageBooks[(j * 8) + k] = (byte)r.Read(8);
                    res.Stages = Math.Max(res.Stages, k + 1);
                }
            }
        }
        if (res.GroupBook >= books || res.StageBooks.Any(b => b != 0xFF && b >= books))
        {
            throw new InvalidDataException("Vorbis residue names a missing codebook");
        }
        return res;
    }
    private static Mapping ReadMapping(Reader r, int channels, int floors, int residues)
    {
        int submaps = r.Read(1) != 0 ? r.Read(4) + 1 : 1;
        int[] magnitude = [];
        int[] angle = [];
        if (r.Read(1) != 0)
        {
            int steps = r.Read(8) + 1;
            magnitude = new int[steps];
            angle = new int[steps];
            for (int i = 0; i < steps; i++)
            {
                magnitude[i] = r.Read(Ilog(channels - 1));
                angle[i] = r.Read(Ilog(channels - 1));
                if (magnitude[i] == angle[i] || magnitude[i] >= channels || angle[i] >= channels)
                {
                    throw new InvalidDataException("bad Vorbis channel coupling");
                }
            }
        }
        if (r.Read(2) != 0)
        {
            throw new InvalidDataException("reserved Vorbis mapping bits are set");
        }
        int[]? mux = null;
        if (submaps > 1)
        {
            mux = new int[channels];
            for (int i = 0; i < channels; i++)
            {
                mux[i] = r.Read(4);
            }
        }
        int[] subFloor = new int[submaps];
        int[] subResidue = new int[submaps];
        for (int i = 0; i < submaps; i++)
        {
            _ = r.Read(8);
            subFloor[i] = r.Read(8);
            subResidue[i] = r.Read(8);
            if (subFloor[i] >= floors || subResidue[i] >= residues)
            {
                throw new InvalidDataException("Vorbis mapping names a missing floor or residue");
            }
        }
        return new Mapping { Submaps = submaps, Mux = mux, Magnitude = magnitude, Angle = angle, SubFloor = subFloor, SubResidue = subResidue };
    }
    private static void Synthesis(Dsp d, Reader r, bool last)
    {
        int[] blocks = d.Setup.Blocks;
        int mode = r.Read(1);
        d.LW = d.W;
        d.W = d.Setup.Modes[mode].BlockFlag;
        int previous = blocks[d.LW];
        for (int c = 0; c < d.Pcm.Length; c++)
        {
            Span<float> pcm = d.Floats(c);
            for (int i = 0; i < previous / 4; i++)
            {
                d.Right[c][i] = pcm[(2 * i) + 1];
            }
        }
        if (d.Returned != -1)
        {
            d.Returned = 0;
            d.Current = (previous / 4) + (blocks[d.W] / 4);
            if (d.SkipSamples != 0)
            {
                if (d.SkipSamples > d.Current)
                {
                    d.Returned = d.Current;
                    d.SkipSamples = (ushort)(d.SkipSamples - d.Current);
                    if (d.SkipSamples >= blocks[1] / 2)
                    {
                        return;
                    }
                }
                else
                {
                    d.Returned = d.SkipSamples;
                    d.SkipSamples = 0;
                }
            }
            if (last)
            {
                d.Current = Math.Max(d.Current - d.Trim, d.Returned);
            }
        }
        else
        {
            d.Returned = 0;
            d.Current = 0;
            if (d.SkipSamples >= blocks[1] / 2)
            {
                return;
            }
        }
        MappingInverse(d, r, d.Setup.Mappings[d.Setup.Modes[mode].Mapping]);
    }
    private static void MappingInverse(Dsp d, Reader r, Mapping map)
    {
        Setup s = d.Setup;
        int n = s.Blocks[d.W];
        int channels = d.Pcm.Length;
        int[]?[] memo = new int[]?[channels];
        bool[] nonzero = new bool[channels];
        for (int i = 0; i < channels; i++)
        {
            Floor floor = s.Floors[map.SubFloor[map.Submaps > 1 ? map.Mux![i] : 0]];
            memo[i] = FloorFit(floor, s, r, d.Fit[i]);
            nonzero[i] = memo[i] is not null;
            Array.Clear(d.Pcm[i], 0, n / 2);
        }
        for (int i = 0; i < map.Magnitude.Length; i++)
        {
            if (nonzero[map.Magnitude[i]] || nonzero[map.Angle[i]])
            {
                nonzero[map.Magnitude[i]] = true;
                nonzero[map.Angle[i]] = true;
            }
        }
        for (int sub = 0; sub < map.Submaps; sub++)
        {
            List<int[]> bundle = [];
            List<bool> flags = [];
            for (int j = 0; j < channels; j++)
            {
                if (map.Mux is null || map.Mux[j] == sub)
                {
                    flags.Add(nonzero[j]);
                    bundle.Add(d.Pcm[j]);
                }
            }
            ResidueInverse(d, r, s.Residues[map.SubResidue[sub]], bundle, flags);
        }
        for (int i = map.Magnitude.Length - 1; i >= 0; i--)
        {
            int[] pm = d.Pcm[map.Magnitude[i]];
            int[] pa = d.Pcm[map.Angle[i]];
            for (int j = 0; j < n / 2; j++)
            {
                int mag = pm[j];
                int ang = pa[j];
                if (mag > 0)
                {
                    if (ang > 0)
                    {
                        pa[j] = mag - ang;
                    }
                    else
                    {
                        pa[j] = mag;
                        pm[j] = mag + ang;
                    }
                }
                else if (ang > 0)
                {
                    pa[j] = mag + ang;
                }
                else
                {
                    pa[j] = mag;
                    pm[j] = mag - ang;
                }
            }
        }
        for (int i = 0; i < channels; i++)
        {
            Floor floor = s.Floors[map.SubFloor[map.Submaps > 1 ? map.Mux![i] : 0]];
            FloorApply(floor, d.Pcm[i], memo[i], n / 2);
        }
        for (int i = 0; i < channels; i++)
        {
            Mdct(n, d.Floats(i));
        }
    }
    private static int[]? FloorFit(Floor f, Setup s, Reader r, int[] fit)
    {
        int quant = s_quant[f.Mult - 1];
        if (r.Read(1) != 1)
        {
            return null;
        }
        fit[0] = r.Read(Ilog(quant - 1));
        fit[1] = r.Read(Ilog(quant - 1));
        int j = 2;
        foreach (int cls in f.Partitions)
        {
            int dim = f.ClassDim[cls];
            int subs = f.ClassSubs[cls];
            int cval = 0;
            if (subs != 0)
            {
                cval = Scalar(s.Books[f.ClassBook[cls]], r);
                if (cval == -1)
                {
                    return null;
                }
            }
            for (int k = 0; k < dim; k++)
            {
                int book = f.SubBooks[cls][cval & ((1 << subs) - 1)];
                cval >>= subs;
                fit[j + k] = 0;
                if (book >= 0)
                {
                    fit[j + k] = Scalar(s.Books[book], r);
                    if (fit[j + k] == -1)
                    {
                        return null;
                    }
                }
            }
            j += dim;
        }
        for (int i = 2; i < f.Posts.Length; i++)
        {
            int lo = f.Low[i - 2];
            int hi = f.High[i - 2];
            int y0 = fit[lo] & 0x7FFF;
            int dy = (fit[hi] & 0x7FFF) - y0;
            int off = Math.Abs(dy) * (f.Posts[i] - f.Posts[lo]) / (f.Posts[hi] - f.Posts[lo]);
            int predicted = dy < 0 ? y0 - off : y0 + off;
            int hiroom = quant - predicted;
            int loroom = predicted;
            int room = Math.Min(hiroom, loroom) << 1;
            int val = fit[i];
            if (val == 0)
            {
                fit[i] = predicted | 0x8000;
                continue;
            }
            if (val >= room)
            {
                val = hiroom > loroom ? val - loroom : -1 - (val - hiroom);
            }
            else
            {
                val = (val & 1) != 0 ? -((val + 1) >> 1) : val >> 1;
            }
            fit[i] = val + predicted;
            fit[lo] &= 0x7FFF;
            fit[hi] &= 0x7FFF;
        }
        return fit;
    }
    private static void FloorApply(Floor f, int[] pcm, int[]? fit, int n)
    {
        if (fit is null)
        {
            Array.Clear(pcm, 0, n);
            return;
        }
        ReadOnlySpan<float> table = s_tables.Value.AsSpan(FloorTable, 256);
        int lx = 0;
        int ly = fit[0] * f.Mult;
        for (int j = 1; j < f.Posts.Length; j++)
        {
            int current = f.Order[j];
            int hy = fit[current] & 0x7FFF;
            if (hy != fit[current])
            {
                continue;
            }
            hy *= f.Mult;
            int hx = f.Posts[current];
            int dy = hy - ly;
            int adx = hx - lx;
            int ady = Math.Abs(dy);
            int step = dy / adx;
            int sy = dy < 0 ? step - 1 : step + 1;
            ady -= Math.Abs(step * adx);
            int x = lx;
            int y = ly;
            int err = 0;
            pcm[x] = BitConverter.SingleToInt32Bits(pcm[x] * table[y]);
            while (++x < hx)
            {
                err += ady;
                if (err >= adx)
                {
                    err -= adx;
                    y += sy;
                }
                else
                {
                    y += step;
                }
                pcm[x] = BitConverter.SingleToInt32Bits(pcm[x] * table[y]);
            }
            lx = hx;
            ly = hy;
        }
    }
    private static void ResidueInverse(Dsp d, Reader r, Residue res, List<int[]> input, List<bool> nonzero)
    {
        Book[] books = d.Setup.Books;
        Book phrase = books[res.GroupBook];
        int per = phrase.Dim;
        int pcmend = d.Setup.Blocks[d.W];
        int ch = input.Count;
        Span<int> v = stackalloc int[16];
        if (res.Type < 2)
        {
            int n = Math.Min(res.End, pcmend >> 1) - res.Begin;
            if (n <= 0)
            {
                return;
            }
            int partvals = n / res.Grouping;
            int[][] used = [.. input.Where((_, i) => nonzero[i])];
            if (used.Length == 0)
            {
                return;
            }
            byte[][] word = [.. used.Select(_ => new byte[(partvals + per - 1) / per * per])];
            for (int s = 0; s < res.Stages; s++)
            {
                for (int i = 0; i < partvals;)
                {
                    if (s == 0)
                    {
                        Divisors(word[0], i, per, res.Partitions);
                        for (int j = 1; j < used.Length; j++)
                        {
                            Array.Copy(word[j - 1], i, word[j], i, per);
                        }
                        for (int j = 0; j < used.Length; j++)
                        {
                            int temp = Scalar(phrase, r);
                            if (r.Left < 0)
                            {
                                return;
                            }
                            Split(word[j], i, per, temp);
                        }
                    }
                    for (int k = 0; k < per && i < partvals; k++, i++)
                    {
                        for (int j = 0; j < used.Length; j++)
                        {
                            int idx = (sbyte)word[j][i];
                            if ((res.StageMasks[idx] & (1 << s)) == 0)
                            {
                                continue;
                            }
                            Book book = books[res.StageBooks[(idx * 8) + s]];
                            int[] a = used[j];
                            int at = res.Begin + (i * res.Grouping);
                            for (int x = 0; x < res.Grouping;)
                            {
                                Map(book, r, v);
                                for (int y = 0; y < book.Dim; y++)
                                {
                                    a[at + x++] += v[y];
                                }
                            }
                        }
                    }
                }
            }
            return;
        }
        {
            int n = Math.Min(res.End, pcmend * ch >> 1) - res.Begin;
            if (n <= 0 || !nonzero.Contains(true))
            {
                return;
            }
            if (ch < 2)
            {
                throw new NotSupportedException("the game decodes type 2 Vorbis residue only for two or more channels");
            }
            int partvals = n / res.Grouping;
            byte[] word = new byte[(partvals + per - 1) / per * per];
            int beginoff = res.Begin / ch;
            int spp = res.Grouping / ch;
            for (int s = 0; s < res.Stages; s++)
            {
                for (int i = 0, off = 0; i < partvals;)
                {
                    if (s == 0)
                    {
                        Divisors(word, i, per, res.Partitions);
                        Split(word, i, per, Scalar(phrase, r));
                    }
                    for (int k = 0; k < per && i < partvals; k++, i++, off += spp)
                    {
                        int idx = (sbyte)word[i];
                        if ((res.StageMasks[idx] & (1 << s)) == 0)
                        {
                            continue;
                        }
                        Book book = books[res.StageBooks[(idx * 8) + s]];
                        int chptr = 0;
                        for (int x = beginoff + off; x < beginoff + off + spp;)
                        {
                            Map(book, r, v);
                            for (int y = 0; y < book.Dim; y++)
                            {
                                input[chptr][x] += v[y];
                                x += chptr;
                                chptr ^= 1;
                            }
                        }
                    }
                }
            }
        }
    }
    private static void Divisors(byte[] word, int i, int per, int partitions)
    {
        word[i + per - 1] = 1;
        for (int k = per - 2; k >= 0; k--)
        {
            word[i + k] = (byte)(word[i + k + 1] * partitions);
        }
    }
    private static void Split(byte[] word, int i, int per, int temp)
    {
        for (int k = 0; k < per; k++)
        {
            int div = (sbyte)word[i + k];
            uint q = div == 0 ? 0 : (uint)temp / (uint)div;
            word[i + k] = (byte)q;
            temp -= div * (sbyte)word[i + k];
        }
    }
    private static void Mdct(int n, Span<float> x)
    {
        int shift = 4;
        while ((n & (1 << shift)) == 0)
        {
            shift++;
        }
        shift = 13 - shift;
        int step = 2 << shift;
        Presymmetry(x, n >> 1, step);
        Butterflies(x, n >> 1, shift);
        BitReverse(x, n, shift);
        Step7(x, n, step);
        Step8(x, n, step >> 2);
    }
    private static void Presymmetry(Span<float> x, int n2, int step)
    {
        ReadOnlySpan<float> t = Trig;
        int n4 = n2 >> 1;
        int a = n2 - 3;
        int k = 0;
        do
        {
            float r0 = x[a];
            float r2 = x[a + 2];
            x[a] = (r0 * t[k]) + (r2 * t[k + 1]);
            x[a + 2] = (r2 * t[k]) - (r0 * t[k + 1]);
            k += step;
            a -= 4;
        }
        while (a >= n4);
        do
        {
            float r0 = x[a];
            float r2 = x[a + 2];
            x[a] = (r0 * t[k + 1]) + (r2 * t[k]);
            x[a + 2] = (r2 * t[k + 1]) - (r0 * t[k]);
            k -= step;
            a -= 4;
        }
        while (a >= 0);
        a = n2 - 4;
        int b = 0;
        k = 0;
        do
        {
            float ri0 = x[a];
            float ri2 = x[a + 2];
            float ro0 = x[b];
            float ro2 = x[b + 2];
            x[a] = (ro2 * t[k + 1]) - (ro0 * t[k]);
            x[a + 2] = (ro0 * t[k + 1]) + (ro2 * t[k]);
            k += step;
            x[b] = (ri2 * t[k]) - (ri0 * t[k + 1]);
            x[b + 2] = (ri0 * t[k]) + (ri2 * t[k + 1]);
            a -= 4;
            b += 4;
        }
        while (a >= n4);
    }
    private static void Butterflies(Span<float> x, int points, int shift)
    {
        int stages = 8 - shift;
        for (int i = 0; --stages > 0; i++)
        {
            for (int j = 0; j < 1 << i; j++)
            {
                ButterflyGeneric(x[((points >> i) * j)..], points >> i, 4 << (i + shift));
            }
        }
        for (int j = 0; j < points; j += 32)
        {
            Butterfly32(x[j..]);
        }
    }
    private static void ButterflyGeneric(Span<float> x, int points, int step)
    {
        ReadOnlySpan<float> t = Trig;
        int x1 = points - 4;
        int x2 = (points >> 1) - 4;
        int k = 0;
        do
        {
            float r0 = x[x1] - x[x1 + 1];
            x[x1] += x[x1 + 1];
            float r1 = x[x1 + 3] - x[x1 + 2];
            x[x1 + 2] += x[x1 + 3];
            float r2 = x[x2 + 1] - x[x2];
            x[x1 + 1] = x[x2 + 1] + x[x2];
            float r3 = x[x2 + 3] - x[x2 + 2];
            x[x1 + 3] = x[x2 + 3] + x[x2 + 2];
            x[x2] = (r1 * t[k]) + (r0 * t[k + 1]);
            x[x2 + 2] = (r0 * t[k]) - (r1 * t[k + 1]);
            x[x2 + 1] = (r2 * t[k]) + (r3 * t[k + 1]);
            x[x2 + 3] = (r3 * t[k]) - (r2 * t[k + 1]);
            k += step;
            x1 -= 4;
            x2 -= 4;
        }
        while (k < 1024);
        do
        {
            float r0 = x[x1] - x[x1 + 1];
            x[x1] += x[x1 + 1];
            float r1 = x[x1 + 2] - x[x1 + 3];
            x[x1 + 2] += x[x1 + 3];
            float r2 = x[x2] - x[x2 + 1];
            x[x1 + 1] = x[x2 + 1] + x[x2];
            float r3 = x[x2 + 3] - x[x2 + 2];
            x[x1 + 3] = x[x2 + 3] + x[x2 + 2];
            x[x2] = (r0 * t[k]) - (r1 * t[k + 1]);
            x[x2 + 2] = (r1 * t[k]) + (r0 * t[k + 1]);
            x[x2 + 1] = (r3 * t[k]) - (r2 * t[k + 1]);
            x[x2 + 3] = (r2 * t[k]) + (r3 * t[k + 1]);
            k -= step;
            x1 -= 4;
            x2 -= 4;
        }
        while (k > 0);
    }
    private static void Butterfly8(Span<float> x)
    {
        float r0 = x[0] + x[1];
        float r1 = x[0] - x[1];
        float r2 = x[2] + x[3];
        float r3 = x[2] - x[3];
        float r4 = x[4] + x[5];
        float r5 = x[4] - x[5];
        float r6 = x[6] + x[7];
        float r7 = x[6] - x[7];
        x[0] = r5 + r3;
        x[1] = r7 - r1;
        x[2] = r5 - r3;
        x[3] = r7 + r1;
        x[4] = r4 - r0;
        x[5] = r6 - r2;
        x[6] = r4 + r0;
        x[7] = r6 + r2;
    }
    private static void Butterfly16(Span<float> x)
    {
        float r0 = x[8] - x[9];
        x[8] += x[9];
        float r1 = x[10] - x[11];
        x[10] += x[11];
        float r2 = x[1] - x[0];
        x[9] = x[1] + x[0];
        float r3 = x[3] - x[2];
        x[11] = x[3] + x[2];
        x[0] = s_pi2 * (r0 - r1);
        x[1] = s_pi2 * (r2 + r3);
        x[2] = s_pi2 * (r0 + r1);
        x[3] = s_pi2 * (r3 - r2);
        r2 = x[12] - x[13];
        x[12] += x[13];
        r3 = x[14] - x[15];
        x[14] += x[15];
        r0 = x[4] - x[5];
        x[13] = x[5] + x[4];
        r1 = x[7] - x[6];
        x[15] = x[7] + x[6];
        x[4] = r2;
        x[5] = r1;
        x[6] = r3;
        x[7] = r0;
        Butterfly8(x);
        Butterfly8(x[8..]);
    }
    private static void Butterfly32(Span<float> x)
    {
        float r0 = x[16] - x[17];
        x[16] += x[17];
        float r1 = x[18] - x[19];
        x[18] += x[19];
        float r2 = x[1] - x[0];
        x[17] = x[1] + x[0];
        float r3 = x[3] - x[2];
        x[19] = x[3] + x[2];
        x[0] = (r0 * s_pi3) - (r1 * s_pi1);
        x[2] = (r1 * s_pi3) + (r0 * s_pi1);
        x[1] = (r2 * s_pi1) + (r3 * s_pi3);
        x[3] = (r3 * s_pi1) - (r2 * s_pi3);
        r0 = x[20] - x[21];
        x[20] += x[21];
        r1 = x[22] - x[23];
        x[22] += x[23];
        r2 = x[5] - x[4];
        x[21] = x[5] + x[4];
        r3 = x[7] - x[6];
        x[23] = x[7] + x[6];
        x[4] = s_pi2 * (r0 - r1);
        x[5] = s_pi2 * (r3 + r2);
        x[6] = s_pi2 * (r0 + r1);
        x[7] = s_pi2 * (r3 - r2);
        r0 = x[24] - x[25];
        x[24] += x[25];
        r1 = x[26] - x[27];
        x[26] += x[27];
        r2 = x[9] - x[8];
        x[25] = x[9] + x[8];
        r3 = x[11] - x[10];
        x[27] = x[11] + x[10];
        x[8] = (r0 * s_pi1) - (r1 * s_pi3);
        x[10] = (r1 * s_pi1) + (r0 * s_pi3);
        x[9] = (r2 * s_pi3) + (r3 * s_pi1);
        x[11] = (r3 * s_pi3) - (r2 * s_pi1);
        r0 = x[28] - x[29];
        x[28] += x[29];
        r1 = x[30] - x[31];
        x[30] += x[31];
        r2 = x[12] - x[13];
        x[29] = x[13] + x[12];
        r3 = x[15] - x[14];
        x[31] = x[15] + x[14];
        x[12] = r0;
        x[13] = r3;
        x[14] = r1;
        x[15] = r2;
        Butterfly16(x);
        Butterfly16(x[16..]);
    }
    private static void BitReverse(Span<float> x, int n, int shift)
    {
        int bit = 0;
        int w = n >> 1;
        do
        {
            int b = s_bitrev[bit >> 8] | (s_bitrev[(bit >> 4) & 15] << 4) | (s_bitrev[bit & 15] << 8);
            bit++;
            int xx = b >> shift;
            w -= 2;
            if (w > xx)
            {
                (x[xx], x[w]) = (x[w], x[xx]);
                (x[xx + 1], x[w + 1]) = (x[w + 1], x[xx + 1]);
            }
        }
        while (w > 0);
    }
    private static void Step7(Span<float> x, int n, int step)
    {
        ReadOnlySpan<float> t = Trig;
        int w0 = 0;
        int w1 = n >> 1;
        int k = step >= 4 ? step >> 1 : Lookup1;
        int top = k + 1024;
        do
        {
            w1 -= 2;
            float r0 = x[w0] + x[w1];
            float r1 = x[w1 + 1] - x[w0 + 1];
            float r2 = (r0 * t[k + 1]) + (r1 * t[k]);
            float r3 = (r1 * t[k + 1]) - (r0 * t[k]);
            k += step;
            Lap7(x, w0, w1, r2, r3);
            w0 += 2;
        }
        while (k < top);
        do
        {
            w1 -= 2;
            float r0 = x[w0] + x[w1];
            float r1 = x[w1 + 1] - x[w0 + 1];
            k -= step;
            float r2 = (r0 * t[k]) + (r1 * t[k + 1]);
            float r3 = (r1 * t[k]) - (r0 * t[k + 1]);
            Lap7(x, w0, w1, r2, r3);
            w0 += 2;
        }
        while (w0 < w1);
    }
    private static void Lap7(Span<float> x, int w0, int w1, float r2, float r3)
    {
        float r0 = 0.5f * (x[w0 + 1] + x[w1 + 1]);
        float r1 = 0.5f * (x[w0] - x[w1]);
        r2 *= 0.5f;
        r3 *= 0.5f;
        x[w0] = r0 + r2;
        x[w0 + 1] = r1 + r3;
        x[w1] = r0 - r2;
        x[w1 + 1] = r3 - r1;
    }
    private static void Step8(Span<float> x, int n, int step)
    {
        ReadOnlySpan<float> t = Trig;
        int end = n >> 1;
        if (step != 1)
        {
            int k = step >= 4 ? step >> 1 : Lookup1;
            for (int i = 0; i < end; i += 2)
            {
                float r0 = x[i];
                float r1 = -x[i + 1];
                x[i] = (r0 * t[k]) + (r1 * t[k + 1]);
                x[i + 1] = (r1 * t[k]) - (r0 * t[k + 1]);
                k += step;
            }
            return;
        }
        int tk = 0;
        int vk = Lookup1;
        float t0 = 0.5f * t[tk++];
        float t1 = 0.5f * t[tk++];
        for (int i = 0; i < end; i += 4)
        {
            float r0 = x[i];
            float r1 = -x[i + 1];
            float v0 = 0.5f * t[vk++];
            float v1 = 0.5f * t[vk++];
            t0 += v0;
            t1 += v1;
            x[i] = (r0 * t0) + (r1 * t1);
            x[i + 1] = (r1 * t0) - (r0 * t1);
            r0 = x[i + 2];
            r1 = -x[i + 3];
            t0 = 0.5f * t[tk++];
            t1 = 0.5f * t[tk++];
            v0 += t0;
            v1 += t1;
            x[i + 2] = (r0 * v0) + (r1 * v1);
            x[i + 3] = (r1 * v0) - (r0 * v1);
        }
    }
    private static int Window(int n)
    {
        return WindowTable + (n >> 1) - 128;
    }
    private static void Lap(Dsp d, int c, Span<short> output, int step, int start, int end)
    {
        int n0 = d.Setup.Blocks[0];
        int n1 = d.Setup.Blocks[1];
        bool lw = d.LW != 0;
        bool w = d.W != 0;
        ReadOnlySpan<float> pcm = d.Floats(c);
        ReadOnlySpan<float> right = d.Right[c];
        ReadOnlySpan<float> win = s_tables.Value;
        int l = w && lw ? n1 >> 1 : n0 >> 1;
        int r = lw ? n1 >> 2 : n0 >> 2;
        int wl = w && lw ? Window(n1) : Window(n0);
        int wr = wl + (w && lw ? n1 >> 1 : n0 >> 1);
        int preLap = lw && !w ? (n1 >> 2) - (n0 >> 2) : 0;
        int halfLap = lw && w ? n1 >> 2 : n0 >> 2;
        int postLap = !lw && w ? (n1 >> 2) - (n0 >> 2) : 0;
        int o = 0;
        int n;
        int off;
        int post;
        if (preLap != 0)
        {
            n = Math.Min(end, preLap);
            off = Math.Min(start, preLap);
            post = r - n;
            r -= off;
            start -= off;
            end -= n;
            while (r > post)
            {
                output[o] = Pcm(Scale * right[--r]);
                o += step;
            }
        }
        n = Math.Min(end, halfLap);
        off = Math.Min(start, halfLap);
        post = r - n;
        r -= off;
        l -= off * 2;
        start -= off;
        wr -= off;
        wl += off;
        end -= n;
        while (r > post)
        {
            l -= 2;
            output[o] = Pcm(Scale * ((right[--r] * win[--wr]) + (pcm[l] * win[wl++])));
            o += step;
        }
        n = Math.Min(end, halfLap);
        off = Math.Min(start, halfLap);
        post = r + n;
        r += off;
        l += off * 2;
        start -= off;
        end -= n;
        wr -= off;
        wl += off;
        while (r < post)
        {
            output[o] = Pcm(Scale * ((right[r++] * win[--wr]) - (pcm[l] * win[wl++])));
            o += step;
            l += 2;
        }
        if (postLap != 0)
        {
            n = Math.Min(end, postLap);
            off = Math.Min(start, postLap);
            post = l + (n * 2);
            l += off * 2;
            while (l < post)
            {
                output[o] = Pcm(-Scale * pcm[l]);
                o += step;
                l += 2;
            }
        }
    }
    internal static short Pcm(float v)
    {
        return (short)Math.Clamp(float.IsNaN(v) ? int.MinValue : (int)v, short.MinValue, short.MaxValue);
    }
}
