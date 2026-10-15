using DWSSK.Formats.Core;

namespace DWSSK.Formats.Audio;

internal sealed class VorbisBook
{
    public int Dims { get; init; }
    public int Entries { get; init; }
    public required int[] Lengths { get; init; }
    public required uint[] Codes { get; init; }
    public float[]? Values { get; init; }
    public float[]? Grid { get; init; }
    public required int[] Used { get; init; }
    public float MaxAbs { get; init; }

    public void Write(BitStream w, int entry)
    {
        if (Lengths[entry] == 0)
        {
            throw new InvalidOperationException($"codebook entry {entry} has no codeword");
        }
        w.Write(Codes[entry], Lengths[entry]);
    }

    public int Nearest(ReadOnlySpan<float> target)
    {
        if (Grid is { } grid)
        {
            int entry = 0;
            int scale = 1;
            for (int d = 0; d < Dims; d++)
            {
                int pick = 0;
                for (int g = 1; g < grid.Length; g++)
                {
                    if (Math.Abs(target[d] - grid[g]) < Math.Abs(target[d] - grid[pick]))
                    {
                        pick = g;
                    }
                }
                entry += pick * scale;
                scale *= grid.Length;
            }
            if (entry < Entries && Lengths[entry] > 0)
            {
                return entry;
            }
        }
        float[] v = Values!;
        int best = Used[0];
        double bestError = double.MaxValue;
        foreach (int e in Used)
        {
            double error = 0;
            for (int d = 0; d < Dims; d++)
            {
                double diff = target[d] - v[(e * Dims) + d];
                error += diff * diff;
            }
            if (error < bestError || (error == bestError && Lengths[e] < Lengths[best]))
            {
                bestError = error;
                best = e;
            }
        }
        return best;
    }
}

internal sealed class VorbisFloor
{
    public required int[] PartitionClass { get; init; }
    public required int[] ClassDims { get; init; }
    public required int[] ClassSubs { get; init; }
    public required int[] ClassBook { get; init; }
    public required int[][] SubBooks { get; init; }
    public int Multiplier { get; init; }
    public int RangeBits { get; init; }
    public required int[] X { get; init; }
    public int Range => Multiplier switch { 1 => 256, 2 => 128, 3 => 86, _ => 64 };
}

internal sealed class VorbisResidue
{
    public int Type { get; init; }
    public int Begin { get; init; }
    public int End { get; init; }
    public int PartitionSize { get; init; }
    public int Classifications { get; init; }
    public int ClassBook { get; init; }
    public required int[][] Books { get; init; }
}

internal sealed class VorbisMapping
{
    public required (int Magnitude, int Angle)[] Coupling { get; init; }
    public required int[] Mux { get; init; }
    public required int[] SubmapFloor { get; init; }
    public required int[] SubmapResidue { get; init; }
}

internal sealed class VorbisSetup
{
    public required VorbisBook[] Books { get; init; }
    public required VorbisFloor[] Floors { get; init; }
    public required VorbisResidue[] Residues { get; init; }
    public required VorbisMapping[] Mappings { get; init; }
    public required (bool Long, int Mapping)[] Modes { get; init; }

    public static VorbisSetup Parse(byte[] packet, int channels)
    {
        BitStream r = new(packet);
        if (r.Read(8) != 5)
        {
            throw new InvalidDataException("not a Vorbis setup header");
        }
        _ = r.Read(48);
        VorbisBook[] books = new VorbisBook[r.Read(8) + 1];
        for (int i = 0; i < books.Length; i++)
        {
            books[i] = Book(r);
        }
        int times = (int)r.Read(6) + 1;
        for (int i = 0; i < times; i++)
        {
            _ = r.Read(16);
        }
        VorbisFloor[] floors = new VorbisFloor[r.Read(6) + 1];
        for (int i = 0; i < floors.Length; i++)
        {
            if (r.Read(16) != 1)
            {
                throw new NotSupportedException("only floor type 1 is encoded");
            }
            floors[i] = Floor(r);
        }
        VorbisResidue[] residues = new VorbisResidue[r.Read(6) + 1];
        for (int i = 0; i < residues.Length; i++)
        {
            residues[i] = Residue(r);
        }
        VorbisMapping[] mappings = new VorbisMapping[r.Read(6) + 1];
        for (int i = 0; i < mappings.Length; i++)
        {
            mappings[i] = Mapping(r, channels);
        }
        (bool, int)[] modes = new (bool, int)[r.Read(6) + 1];
        for (int i = 0; i < modes.Length; i++)
        {
            bool blockflag = r.Read(1) != 0;
            _ = r.Read(32);
            modes[i] = (blockflag, (int)r.Read(8));
        }
        return new VorbisSetup { Books = books, Floors = floors, Residues = residues, Mappings = mappings, Modes = modes };
    }

    private static float Float32(uint x)
    {
        double mantissa = x & 0x1fffff;
        if ((x & 0x80000000) != 0)
        {
            mantissa = -mantissa;
        }
        return (float)Math.ScaleB(mantissa, (int)((x & 0x7fe00000) >> 21) - 788);
    }

    private static VorbisBook Book(BitStream r)
    {
        if (r.Read(24) != 0x564342)
        {
            throw new InvalidDataException("bad codebook sync");
        }
        int dims = (int)r.Read(16);
        int entries = (int)r.Read(24);
        int[] lengths = new int[entries];
        if (r.Read(1) == 0)
        {
            bool sparse = r.Read(1) != 0;
            for (int e = 0; e < entries; e++)
            {
                lengths[e] = !sparse || r.Read(1) != 0 ? (int)r.Read(5) + 1 : 0;
            }
        }
        else
        {
            int len = (int)r.Read(5) + 1;
            for (int e = 0; e < entries; len++)
            {
                int n = (int)r.Read(Vorbis.Ilog(entries - e));
                for (int k = 0; k < n && e < entries; k++)
                {
                    lengths[e++] = len;
                }
            }
        }
        int lookup = (int)r.Read(4);
        float[]? values = null;
        float[]? grid = null;
        if (lookup is 1 or 2)
        {
            float min = Float32((uint)r.Read(32));
            float delta = Float32((uint)r.Read(32));
            int valueBits = (int)r.Read(4) + 1;
            bool sequence = r.Read(1) != 0;
            long count = lookup == 1 ? Vorbis.Quantvals(entries, dims) : (long)entries * dims;
            int[] mult = new int[count];
            for (int k = 0; k < count; k++)
            {
                mult[k] = (int)r.Read(valueBits);
            }
            if (lookup == 1 && !sequence)
            {
                grid = [.. mult.Select(m => (m * delta) + min)];
            }
            values = new float[entries * dims];
            for (int e = 0; e < entries; e++)
            {
                float last = 0;
                long divisor = 1;
                for (int d = 0; d < dims; d++)
                {
                    long at = lookup == 1 ? e / divisor % count : ((long)e * dims) + d;
                    float v = (mult[at] * delta) + min + last;
                    values[(e * dims) + d] = v;
                    if (sequence)
                    {
                        last = v;
                    }
                    divisor *= count;
                }
            }
        }
        else if (lookup != 0)
        {
            throw new InvalidDataException($"codebook lookup type {lookup}");
        }
        int[] used = [.. Enumerable.Range(0, entries).Where(e => lengths[e] > 0)];
        float maxAbs = 0;
        if (values is not null)
        {
            foreach (int e in used)
            {
                for (int d = 0; d < dims; d++)
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(values[(e * dims) + d]));
                }
            }
        }
        return new VorbisBook { Dims = dims, Entries = entries, Lengths = lengths, Codes = Words(lengths), Values = values, Grid = grid, Used = used, MaxAbs = maxAbs };
    }

    private static uint[] Words(int[] lengths)
    {
        uint[] marker = new uint[33];
        uint[] words = new uint[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0)
            {
                continue;
            }
            uint entry = marker[length];
            if (length < 32 && (entry >> length) != 0)
            {
                throw new InvalidDataException("overpopulated codebook");
            }
            words[i] = entry;
            for (int j = length; j > 0; j--)
            {
                if ((marker[j] & 1) != 0)
                {
                    marker[j] = j == 1 ? marker[1] + 1 : marker[j - 1] << 1;
                    break;
                }
                marker[j]++;
            }
            for (int j = length + 1; j < 33; j++)
            {
                if ((marker[j] >> 1) == entry)
                {
                    entry = marker[j];
                    marker[j] = marker[j - 1] << 1;
                }
                else
                {
                    break;
                }
            }
        }
        for (int i = 0; i < lengths.Length; i++)
        {
            uint reversed = 0;
            for (int j = 0; j < lengths[i]; j++)
            {
                reversed = (reversed << 1) | ((words[i] >> j) & 1);
            }
            words[i] = reversed;
        }
        return words;
    }

    private static VorbisFloor Floor(BitStream r)
    {
        int[] partitionClass = new int[r.Read(5)];
        for (int i = 0; i < partitionClass.Length; i++)
        {
            partitionClass[i] = (int)r.Read(4);
        }
        int classes = partitionClass.Length == 0 ? 0 : partitionClass.Max() + 1;
        int[] dims = new int[classes];
        int[] subs = new int[classes];
        int[] master = new int[classes];
        int[][] subBooks = new int[classes][];
        for (int c = 0; c < classes; c++)
        {
            dims[c] = (int)r.Read(3) + 1;
            subs[c] = (int)r.Read(2);
            master[c] = subs[c] != 0 ? (int)r.Read(8) : -1;
            subBooks[c] = new int[1 << subs[c]];
            for (int j = 0; j < subBooks[c].Length; j++)
            {
                subBooks[c][j] = (int)r.Read(8) - 1;
            }
        }
        int multiplier = (int)r.Read(2) + 1;
        int rangeBits = (int)r.Read(4);
        List<int> x = [0, 1 << rangeBits];
        foreach (int c in partitionClass)
        {
            for (int j = 0; j < dims[c]; j++)
            {
                x.Add((int)r.Read(rangeBits));
            }
        }
        return new VorbisFloor
        {
            PartitionClass = partitionClass, ClassDims = dims, ClassSubs = subs, ClassBook = master, SubBooks = subBooks,
            Multiplier = multiplier, RangeBits = rangeBits, X = [.. x],
        };
    }

    private static VorbisResidue Residue(BitStream r)
    {
        int type = (int)r.Read(16);
        int begin = (int)r.Read(24);
        int end = (int)r.Read(24);
        int size = (int)r.Read(24) + 1;
        int classifications = (int)r.Read(6) + 1;
        int classBook = (int)r.Read(8);
        int[] cascade = new int[classifications];
        for (int c = 0; c < classifications; c++)
        {
            int low = (int)r.Read(3);
            cascade[c] = r.Read(1) != 0 ? ((int)r.Read(5) << 3) | low : low;
        }
        int[][] books = new int[classifications][];
        for (int c = 0; c < classifications; c++)
        {
            books[c] = new int[8];
            for (int p = 0; p < 8; p++)
            {
                books[c][p] = ((cascade[c] >> p) & 1) != 0 ? (int)r.Read(8) : -1;
            }
        }
        return new VorbisResidue { Type = type, Begin = begin, End = end, PartitionSize = size, Classifications = classifications, ClassBook = classBook, Books = books };
    }

    private static VorbisMapping Mapping(BitStream r, int channels)
    {
        _ = r.Read(16);
        int submaps = r.Read(1) != 0 ? (int)r.Read(4) + 1 : 1;
        (int, int)[] coupling = [];
        int bits = Vorbis.Ilog(channels - 1);
        if (r.Read(1) != 0)
        {
            coupling = new (int, int)[r.Read(8) + 1];
            for (int s = 0; s < coupling.Length; s++)
            {
                coupling[s] = ((int)r.Read(bits), (int)r.Read(bits));
            }
        }
        _ = r.Read(2);
        int[] mux = new int[channels];
        if (submaps > 1)
        {
            for (int c = 0; c < channels; c++)
            {
                mux[c] = (int)r.Read(4);
            }
        }
        int[] floor = new int[submaps];
        int[] residue = new int[submaps];
        for (int s = 0; s < submaps; s++)
        {
            _ = r.Read(8);
            floor[s] = (int)r.Read(8);
            residue[s] = (int)r.Read(8);
        }
        return new VorbisMapping { Coupling = coupling, Mux = mux, SubmapFloor = floor, SubmapResidue = residue };
    }
}

internal static class VorbisEncoder
{
    private static readonly float[] s_inverseDb =
    [
        1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f, 1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
        1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.128753e-07f, 2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
        2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f, 3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
        4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f, 6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
        7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f, 1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
        1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f, 1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
        2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f, 2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
        3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f, 4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
        5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f, 7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
        9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f, 1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
        1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f, 2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
        2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f, 3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
        4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f, 5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
        7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f, 9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
        0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f, 0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
        0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f, 0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
        0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f, 0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
        0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f, 0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
        0.00092223983f, 0.00098217216f, 0.0010459992f, 0.0011139742f, 0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
        0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f, 0.0019632195f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
        0.0025254795f, 0.0026895994f, 0.0028643847f, 0.0030505286f, 0.0032487691f, 0.0034598925f, 0.0036847358f, 0.0039241906f,
        0.0041792066f, 0.0044507950f, 0.0047400328f, 0.0050480668f, 0.0053761186f, 0.0057254891f, 0.0060975636f, 0.0064938176f,
        0.0069158225f, 0.0073652516f, 0.0078438871f, 0.0083536271f, 0.0088964928f, 0.009474637f, 0.010090352f, 0.010746080f,
        0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f, 0.014722068f, 0.015678791f, 0.016697687f, 0.017782797f,
        0.018938423f, 0.020169149f, 0.021479854f, 0.022875735f, 0.024362330f, 0.025945531f, 0.027631618f, 0.029427276f,
        0.031339626f, 0.033376252f, 0.035545228f, 0.037855157f, 0.040315199f, 0.042935108f, 0.045725273f, 0.048696758f,
        0.051861348f, 0.055231591f, 0.058820850f, 0.062643361f, 0.066714279f, 0.071049749f, 0.075666962f, 0.080584227f,
        0.085821044f, 0.091398179f, 0.097337747f, 0.10366330f, 0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
        0.14201813f, 0.15124727f, 0.16107617f, 0.17154380f, 0.18269168f, 0.19456402f, 0.20720788f, 0.22067342f,
        0.23501402f, 0.25028656f, 0.26655159f, 0.28387361f, 0.30232132f, 0.32196786f, 0.34289114f, 0.36517414f,
        0.38890521f, 0.41417847f, 0.44109412f, 0.46975890f, 0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f,
        0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f, 0.82788260f, 0.88168307f, 0.9389798f, 1f,
    ];

    private const double Precision = 6;
    private const double Mask = 30;
    private const double Silence = 3e-5;

    public static byte[] Encode(byte[] orig, short[] x, int rate, int nch)
    {
        WemInfo i = Wem.Info(orig);
        bool big = i.Big;
        if (i.Fmt.Size != 0x42)
        {
            throw new NotSupportedException($"Wwise Vorbis with a 0x{i.Fmt.Size:x}-byte fmt");
        }
        int v = i.Fmt.Offset + 0x18;
        int setupOff = (int)Bin.U32(orig, v + 0x10, big);
        int audioOff = (int)Bin.U32(orig, v + 0x14, big);
        int bs0 = orig[v + 0x28];
        int bs1 = orig[v + 0x29];
        bool modPackets = Bin.U32(orig, v + 4, big) is not (0x4A or 0x4B or 0x69 or 0x70);
        byte[] setupRaw = i.Data.AsSpan(setupOff + 2, Bin.U16(i.Data, setupOff, big)).ToArray();
        VorbisSetup setup = VorbisSetup.Parse(Vorbis.RebuildSetup(setupRaw, nch).Setup, nch);
        // ponytail: long blocks only, so sharp attacks can pre-echo; add a transient detector that switches to the short-block mode if that is audible.
        int mode = Array.FindIndex(setup.Modes, m => m.Long);
        mode = mode < 0 ? 0 : mode;
        int blocksize = 1 << (setup.Modes[mode].Long ? bs1 : bs0);
        VorbisMapping mapping = setup.Mappings[setup.Modes[mode].Mapping];
        if (mapping.Coupling.Length > 0 || mapping.SubmapFloor.Length > 1)
        {
            throw new NotSupportedException("this Vorbis setup couples channels or splits them into submaps, which the encoder does not write");
        }
        VorbisFloor floor = setup.Floors[mapping.SubmapFloor[0]];
        VorbisResidue residue = setup.Residues[mapping.SubmapResidue[0]];
        int n = blocksize / 2;
        int frames = x.Length / Math.Max(1, nch);
        int blocks = ((frames + n - 1) / n) + 1;
        double[] window = new double[blocksize];
        for (int k = 0; k < blocksize; k++)
        {
            double s = Math.Sin(Math.PI * (k + 0.5) / blocksize);
            window[k] = Math.Sin(Math.PI / 2 * s * s);
        }
        int modeBits = Vorbis.Ilog(setup.Modes.Length - 1);
        List<byte[]> packets = [];
        double[] frame = new double[blocksize];
        double[][] spectra = [.. Enumerable.Range(0, nch).Select(_ => new double[n])];
        for (int b = 0; b < blocks; b++)
        {
            for (int c = 0; c < nch; c++)
            {
                for (int k = 0; k < blocksize; k++)
                {
                    int t = (b * n) - n + k;
                    frame[k] = t >= 0 && t < frames ? x[(t * nch) + c] / 32768.0 * window[k] : 0;
                }
                Dct4.Forward(frame, spectra[c]);
                for (int k = 0; k < n; k++)
                {
                    spectra[c][k] *= 2.0 / n;
                }
            }
            BitStream w = new();
            if (!modPackets)
            {
                w.Write(0, 1);
            }
            w.Write(mode, modeBits);
            if (setup.Modes[mode].Long && !modPackets)
            {
                w.Write(1, 1);
                w.Write(1, 1);
            }
            List<(float[] Residue, bool Used)> channels = [];
            for (int c = 0; c < nch; c++)
            {
                channels.Add(WriteFloor(w, setup, floor, spectra[c], n));
            }
            WriteResidue(w, setup, residue, channels, n);
            packets.Add(w.Bytes());
        }
        return Assemble(orig, i, setupRaw, packets, frames, rate, blocks, n);
    }

    private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int off = Math.Abs(dy) * (x - x0) / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    private static void RenderLine(int x0, int y0, int x1, int y1, int[] v, int n)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int b = dy / adx;
        int sy = dy < 0 ? b - 1 : b + 1;
        int ady = Math.Abs(dy) - (Math.Abs(b) * adx);
        int y = y0;
        int err = 0;
        if (x0 < n)
        {
            v[x0] = y;
        }
        for (int x = x0 + 1; x < x1 && x < n; x++)
        {
            err += ady;
            if (err >= adx)
            {
                err -= adx;
                y += sy;
            }
            else
            {
                y += b;
            }
            v[x] = y;
        }
    }

    private static (float[] Residue, bool Used) WriteFloor(BitStream w, VorbisSetup setup, VorbisFloor floor, double[] spectrum, int n)
    {
        int[] xs = floor.X;
        int posts = xs.Length;
        int range = floor.Range;
        int mult = floor.Multiplier;
        int[] order = [.. Enumerable.Range(0, posts).OrderBy(p => xs[p])];
        double peak = 0;
        foreach (double c in spectrum)
        {
            peak = Math.Max(peak, Math.Abs(c));
        }
        if (peak < Silence)
        {
            w.Write(0, 1);
            return (new float[n], false);
        }
        int[] want = new int[posts];
        double[] targets = new double[posts];
        for (int j = 0; j < posts; j++)
        {
            int lo = j == 0 ? 0 : xs[order[j - 1]];
            int hi = j == posts - 1 ? n : Math.Min(n, xs[order[j + 1]]);
            double energy = 0;
            int bins = 0;
            for (int k = Math.Max(0, lo); k < hi && k < n; k++)
            {
                energy += spectrum[k] * spectrum[k];
                bins++;
            }
            targets[j] = Math.Max(Math.Sqrt(energy / Math.Max(1, bins)), Silence) / Precision;
        }
        double loudest = targets.Max();
        for (int j = 0; j < posts; j++)
        {
            double target = Math.Max(targets[j], loudest * Math.Pow(10, -Mask / 20));
            int d = 0;
            while (d < range - 1 && s_inverseDb[Math.Min(255, d * mult)] < target)
            {
                d++;
            }
            want[order[j]] = d;
        }
        int[] y = new int[posts];
        int[] final = new int[posts];
        bool[] step2 = new bool[posts];
        y[0] = final[0] = want[0];
        y[1] = final[1] = want[1];
        step2[0] = step2[1] = true;
        for (int p = 2; p < posts; p++)
        {
            int lo = -1;
            int hi = -1;
            for (int q = 0; q < p; q++)
            {
                if (xs[q] < xs[p] && (lo < 0 || xs[q] > xs[lo]))
                {
                    lo = q;
                }
                if (xs[q] > xs[p] && (hi < 0 || xs[q] < xs[hi]))
                {
                    hi = q;
                }
            }
            int predicted = RenderPoint(xs[lo], final[lo], xs[hi], final[hi], xs[p]);
            int headroom = Math.Min(range - predicted, predicted);
            int val = want[p] - predicted;
            val = val < 0 ? (val < -headroom ? headroom - val - 1 : -1 - (val << 1)) : val >= headroom ? val + headroom : val << 1;
            y[p] = val;
            final[p] = want[p];
            if (val != 0)
            {
                step2[lo] = step2[hi] = step2[p] = true;
            }
        }
        w.Write(1, 1);
        int bits = Vorbis.Ilog(range - 1);
        w.Write(y[0], bits);
        w.Write(y[1], bits);
        int offset = 2;
        foreach (int cls in floor.PartitionClass)
        {
            int cdim = floor.ClassDims[cls];
            int cbits = floor.ClassSubs[cls];
            int csub = (1 << cbits) - 1;
            int cval = 0;
            int[] pick = new int[cdim];
            for (int k = 0; k < cdim; k++)
            {
                int sub = -1;
                for (int l = 0; l <= csub; l++)
                {
                    int book = floor.SubBooks[cls][l];
                    int max = book < 0 ? 1 : setup.Books[book].Entries;
                    if (y[offset + k] < max && (book < 0 || setup.Books[book].Lengths[y[offset + k]] > 0))
                    {
                        sub = l;
                        break;
                    }
                }
                if (sub < 0)
                {
                    throw new InvalidDataException($"floor value {y[offset + k]} has no codeword in this setup");
                }
                pick[k] = sub;
                cval |= sub << (cbits * k);
            }
            if (cbits > 0)
            {
                setup.Books[floor.ClassBook[cls]].Write(w, cval);
            }
            for (int k = 0; k < cdim; k++)
            {
                int book = floor.SubBooks[cls][pick[k]];
                if (book >= 0)
                {
                    setup.Books[book].Write(w, y[offset + k]);
                }
            }
            offset += cdim;
        }
        int[] curve = new int[n];
        int lx = 0;
        int ly = final[0] * mult;
        int hx = 0;
        int hy = ly;
        for (int j = 1; j < posts; j++)
        {
            int p = order[j];
            if (step2[p])
            {
                hy = final[p] * mult;
                hx = xs[p];
                RenderLine(lx, ly, hx, hy, curve, n);
                lx = hx;
                ly = hy;
            }
        }
        if (hx < n)
        {
            RenderLine(hx, hy, n, hy, curve, n);
        }
        float[] residue = new float[n];
        for (int k = 0; k < n; k++)
        {
            residue[k] = (float)(spectrum[k] / s_inverseDb[Math.Clamp(curve[k], 0, 255)]);
        }
        return (residue, true);
    }

    private static int Index(int type, int offset, int psize, int dims, int vector, int d)
    {
        return type == 0 ? offset + vector + (d * (psize / dims)) : offset + (vector * dims) + d;
    }

    private static (int Bits, double Error, int[][] Entries) Quantize(VorbisSetup setup, VorbisResidue residue, int cls, float[] values, int type, int offset)
    {
        int psize = residue.PartitionSize;
        float[] left = values.AsSpan(offset, psize).ToArray();
        int bits = 0;
        int[][] entries = new int[8][];
        float[] target = new float[8];
        for (int pass = 0; pass < 8; pass++)
        {
            int b = residue.Books[cls][pass];
            if (b < 0)
            {
                continue;
            }
            VorbisBook book = setup.Books[b];
            int dims = book.Dims;
            int vectors = psize / dims;
            entries[pass] = new int[vectors];
            if (target.Length < dims)
            {
                target = new float[dims];
            }
            for (int j = 0; j < vectors; j++)
            {
                for (int d = 0; d < dims; d++)
                {
                    target[d] = left[Index(type, 0, psize, dims, j, d)];
                }
                int e = book.Nearest(target.AsSpan(0, dims));
                entries[pass][j] = e;
                bits += book.Lengths[e];
                for (int d = 0; d < dims; d++)
                {
                    left[Index(type, 0, psize, dims, j, d)] -= book.Values![(e * dims) + d];
                }
            }
        }
        double error = 0;
        foreach (float r in left)
        {
            error = Math.Max(error, Math.Abs(r));
        }
        return (bits, error, entries);
    }

    private static void WriteResidue(BitStream w, VorbisSetup setup, VorbisResidue residue, List<(float[] Residue, bool Used)> channels, int n)
    {
        List<float[]> vectors;
        int size;
        if (residue.Type == 2)
        {
            if (channels.All(c => !c.Used))
            {
                return;
            }
            int nch = channels.Count;
            float[] merged = new float[n * nch];
            for (int k = 0; k < n; k++)
            {
                for (int c = 0; c < nch; c++)
                {
                    merged[(k * nch) + c] = channels[c].Residue[k];
                }
            }
            vectors = [merged];
            size = n * nch;
        }
        else
        {
            vectors = [.. channels.Where(c => c.Used).Select(c => c.Residue)];
            size = n;
        }
        int type = residue.Type == 0 ? 0 : 1;
        int begin = Math.Min(residue.Begin, size);
        int end = Math.Min(residue.End, size);
        int psize = residue.PartitionSize;
        int count = (end - begin) / psize;
        VorbisBook classBook = setup.Books[residue.ClassBook];
        int perWord = classBook.Dims;
        int[][] classes = new int[vectors.Count][];
        int[][][][] picks = new int[vectors.Count][][][];
        for (int c = 0; c < vectors.Count; c++)
        {
            classes[c] = new int[count + perWord];
            picks[c] = new int[count][][];
            for (int p = 0; p < count; p++)
            {
                int bestClass = 0;
                (int Bits, double Error, int[][] Entries) best = (int.MaxValue, double.MaxValue, []);
                for (int cls = 0; cls < residue.Classifications; cls++)
                {
                    (int bits, double error, int[][] entries) = Quantize(setup, residue, cls, vectors[c], type, begin + (p * psize));
                    bool fits = error <= 0.5 + 1e-6;
                    bool bestFits = best.Error <= 0.5 + 1e-6;
                    if ((fits && (!bestFits || bits < best.Bits)) || (!fits && !bestFits && error < best.Error))
                    {
                        best = (bits, error, entries);
                        bestClass = cls;
                    }
                }
                classes[c][p] = bestClass;
                picks[c][p] = best.Entries;
            }
        }
        for (int pass = 0; pass < 8; pass++)
        {
            int p = 0;
            while (p < count)
            {
                if (pass == 0)
                {
                    for (int c = 0; c < vectors.Count; c++)
                    {
                        int word = 0;
                        for (int k = 0; k < perWord; k++)
                        {
                            word = (word * residue.Classifications) + classes[c][p + k];
                        }
                        classBook.Write(w, word);
                    }
                }
                for (int k = 0; k < perWord && p < count; k++, p++)
                {
                    for (int c = 0; c < vectors.Count; c++)
                    {
                        int b = residue.Books[classes[c][p]][pass];
                        if (b >= 0)
                        {
                            foreach (int e in picks[c][p][pass])
                            {
                                setup.Books[b].Write(w, e);
                            }
                        }
                    }
                }
            }
        }
    }

    private static byte[] Assemble(byte[] orig, WemInfo i, byte[] setupRaw, List<byte[]> packets, int frames, int rate, int blocks, int n)
    {
        bool big = i.Big;
        ByteWriter data = new();
        data.U16((uint)setupRaw.Length, big);
        data.Add(setupRaw);
        int audioOff = data.Length;
        int maxPacket = 0;
        foreach (byte[] p in packets)
        {
            if (p.Length > 0xFFFF)
            {
                throw new InvalidDataException("a Vorbis packet came out larger than 64 KB");
            }
            data.U16((uint)p.Length, big);
            data.Add(p);
            maxPacket = Math.Max(maxPacket, p.Length);
        }
        int extra = ((blocks - 1) * n) - frames;
        byte[] fmt = Bin.Slice(orig, i.Fmt.Offset, i.Fmt.Offset + i.Fmt.Size).ToArray();
        Bin.PutU32(fmt, 4, (uint)rate, big);
        Bin.PutU32(fmt, 8, (uint)(frames == 0 ? 0 : (long)data.Length * rate / frames), big);
        Bin.PutU32(fmt, 0x18, (uint)frames, big);
        Bin.PutU32(fmt, 0x1C, (uint)audioOff, big);
        Bin.PutU32(fmt, 0x20, (uint)data.Length, big);
        Bin.PutU16(fmt, 0x24, 0, big);
        Bin.PutU16(fmt, 0x26, (uint)extra, big);
        Bin.PutU32(fmt, 0x28, 0, big);
        Bin.PutU32(fmt, 0x2C, (uint)audioOff, big);
        Bin.PutU16(fmt, 0x30, (uint)maxPacket, big);
        Bin.PutU16(fmt, 0x32, (uint)extra, big);
        return Wem.Rebuild(orig, new Dictionary<string, byte[]> { ["fmt "] = fmt, ["data"] = data.ToArray() });
    }
}
