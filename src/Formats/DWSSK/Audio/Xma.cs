using DWSSK.Formats.Core;
namespace DWSSK.Formats.Audio;
internal sealed class MsbReader(byte[] data, int start, int bits)
{
    private readonly int _end = Math.Min(data.Length, start + ((bits + 7) >> 3));
    public int Size { get; } = bits;
    public int Pos { get; set; }
    public int Left => Size - Pos;
    public byte[] Buffer => data;
    public int Start => start;
    public uint Peek(int n)
    {
        if (n <= 0)
        {
            return 0;
        }
        ulong w = 0;
        int at = start + (Pos >> 3);
        for (int i = 0; i < 8; i++)
        {
            w = (w << 8) | (at + i < _end && at + i >= 0 ? data[at + i] : 0u);
        }
        uint v = (uint)((w << (Pos & 7)) >> (64 - n));
        int over = Pos + n - Size;
        if (over > 0)
        {
            v = over >= n ? 0 : v & ~((1u << over) - 1);
        }
        return v;
    }
    public uint Read(int n)
    {
        uint v = Peek(n);
        Pos += n;
        return v;
    }
    public int Bit()
    {
        return (int)Read(1);
    }
    public int Signed(int n)
    {
        int v = (int)Read(n);
        return (v << (32 - n)) >> (32 - n);
    }
}
internal sealed class MsbWriter
{
    private byte[] _data;
    public MsbWriter(int capacity = 256)
    {
        _data = new byte[Math.Max(16, capacity)];
    }
    public int Bits { get; private set; }
    public byte[] Data => _data;
    public void Reset()
    {
        Array.Clear(_data, 0, Math.Min(_data.Length, (Bits + 7) >> 3));
        Bits = 0;
    }
    public void Put(uint value, int n)
    {
        if (Bits + n + 64 > _data.Length * 8)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, ((Bits + n) >> 3) + 64));
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                _data[Bits >> 3] |= (byte)(0x80 >> (Bits & 7));
            }
            Bits++;
        }
    }
    public void Copy(MsbReader from, int n)
    {
        while (n > 0)
        {
            int take = Math.Min(24, n);
            Put(from.Read(take), take);
            n -= take;
        }
    }
    public byte[] ToArray()
    {
        return _data.AsSpan(0, (Bits + 7) >> 3).ToArray();
    }
}
internal sealed class Vlc
{
    private readonly List<int> _zero = [];
    private readonly List<int> _one = [];
    private readonly List<int> _symbol = [];
    private readonly Dictionary<int, (uint Code, int Length)> _codes = [];
    public Vlc(ReadOnlySpan<byte> lengths, Func<int, int> symbol, int offset)
    {
        Node();
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len == 0)
            {
                continue;
            }
            uint value = (uint)(code >> (32 - len));
            int sym = symbol(i) + offset;
            int at = 0;
            for (int b = len - 1; b >= 0; b--)
            {
                List<int> branch = ((value >> b) & 1) != 0 ? _one : _zero;
                if (branch[at] < 0)
                {
                    branch[at] = Node();
                }
                at = branch[at];
            }
            _symbol[at] = sym;
            _codes.TryAdd(sym, (value, len));
            code += 1UL << (32 - len);
        }
    }
    public static Vlc Pairs(byte[] table, int offset)
    {
        byte[] lengths = new byte[table.Length / 2];
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = table[(2 * i) + 1];
        }
        return new Vlc(lengths, i => table[2 * i], offset);
    }
    private int Node()
    {
        _zero.Add(-1);
        _one.Add(-1);
        _symbol.Add(int.MinValue);
        return _zero.Count - 1;
    }
    public int Read(MsbReader r)
    {
        int at = 0;
        while (true)
        {
            at = r.Bit() != 0 ? _one[at] : _zero[at];
            if (at < 0)
            {
                throw new InvalidDataException("invalid variable-length code");
            }
            if (_symbol[at] != int.MinValue)
            {
                return _symbol[at];
            }
        }
    }
    public bool Has(int symbol)
    {
        return _codes.ContainsKey(symbol);
    }
    public int Length(int symbol)
    {
        return _codes[symbol].Length;
    }
    public void Write(MsbWriter w, int symbol)
    {
        (uint code, int len) = _codes[symbol];
        w.Put(code, len);
    }
}
internal static class Dct4
{
    private static readonly Dictionary<int, (double[] Cos, double[] Sin, int[] Rev)> s_plans = [];
    private static readonly Lock s_lock = new();
    private static (double[] Cos, double[] Sin, int[] Rev) Plan(int m)
    {
        lock (s_lock)
        {
            if (!s_plans.TryGetValue(m, out (double[], double[], int[]) plan))
            {
                double[] c = new double[m / 2];
                double[] s = new double[m / 2];
                for (int i = 0; i < m / 2; i++)
                {
                    c[i] = Math.Cos(2 * Math.PI * i / m);
                    s[i] = Math.Sin(2 * Math.PI * i / m);
                }
                int bits = System.Numerics.BitOperations.Log2((uint)m);
                int[] rev = new int[m];
                for (int i = 0; i < m; i++)
                {
                    int r = 0;
                    for (int b = 0; b < bits; b++)
                    {
                        r |= ((i >> b) & 1) << (bits - 1 - b);
                    }
                    rev[i] = r;
                }
                s_plans[m] = plan = (c, s, rev);
            }
            return plan;
        }
    }
    private static void Fft(double[] re, double[] im, bool positive)
    {
        int m = re.Length;
        (double[] cos, double[] sin, int[] rev) = Plan(m);
        for (int i = 0; i < m; i++)
        {
            int j = rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        double sign = positive ? 1 : -1;
        for (int size = 2; size <= m; size <<= 1)
        {
            int half = size >> 1;
            int step = m / size;
            for (int start = 0; start < m; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = cos[k * step];
                    double wi = sign * sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    double tr = (re[b] * wr) - (im[b] * wi);
                    double ti = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
    public static void Transform(ReadOnlySpan<double> v, Span<double> u)
    {
        int n = v.Length;
        int m = n / 2;
        double[] re = new double[m];
        double[] im = new double[m];
        for (int p = 0; p < m; p++)
        {
            double a = v[2 * p];
            double b = -v[n - 1 - (2 * p)];
            double c = Math.Cos(Math.PI * p / n);
            double s = Math.Sin(Math.PI * p / n);
            re[p] = (a * c) - (b * s);
            im[p] = (a * s) + (b * c);
        }
        Fft(re, im, true);
        for (int q = 0; q < m; q++)
        {
            double angle = Math.PI * (q + 0.25) / n;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            u[2 * q] = (re[q] * c) - (im[q] * s);
            u[n - 1 - (2 * q)] = (re[q] * s) + (im[q] * c);
        }
    }
    public static void InverseHalf(ReadOnlySpan<float> coefficients, Span<float> output, double scale)
    {
        int n = coefficients.Length;
        double[] v = new double[n];
        double[] u = new double[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = coefficients[i];
        }
        Transform(v, u);
        for (int j = 0; j < n; j++)
        {
            output[j] = (float)(-u[n - 1 - j] * scale);
        }
    }
    public static void InverseFull(ReadOnlySpan<double> coefficients, Span<double> output)
    {
        int n = coefficients.Length;
        double[] u = new double[n];
        Transform(coefficients, u);
        int h = n / 2;
        for (int i = 0; i < 2 * n; i++)
        {
            output[i] = i < h ? u[i + h] : i < 3 * h ? -u[(3 * h) - 1 - i] : -u[i - (3 * h)];
        }
    }
    public static void Forward(ReadOnlySpan<double> x, Span<double> coefficients)
    {
        int n = coefficients.Length;
        int h = n / 2;
        double[] v = new double[n];
        for (int i = 0; i < h; i++)
        {
            v[i] = -x[(3 * h) - 1 - i] - x[(3 * h) + i];
        }
        for (int i = h; i < n; i++)
        {
            v[i] = x[i - h] - x[(3 * h) - 1 - i];
        }
        Transform(v, coefficients);
    }
}
internal sealed class WmaProStream
{
    private const int MaxFrameSize = 32768;
    private const int MaxBands = 29;
    private const int MaxSubframes = 32;
    private const int BlockMinBits = 6;
    private const int BlockSizes = 8;
    private const int Log2FrameSize = 15;
    private static readonly Lazy<Vlc[]> s_vlcs = new(() =>
    [
        Vlc.Pairs(WmaProTables.ScaleTable, -60),
        Vlc.Pairs(WmaProTables.ScaleRlTable, 0),
        new Vlc(WmaProTables.Coef0Lens, i => WmaProTables.Coef0Syms[i], 0),
        Vlc.Pairs(WmaProTables.Coef1Table, 0),
        new Vlc(WmaProTables.Vec4Lens, i => WmaProTables.Vec4Syms[i], -1),
        Vlc.Pairs(WmaProTables.Vec2Table, -1),
        Vlc.Pairs(WmaProTables.Vec1Table, 0),
    ]);
    private static readonly float[][] s_windows = [.. Enumerable.Range(0, BlockSizes).Select(i =>
    {
        int n = 1 << (BlockMinBits + i);
        float[] w = new float[n];
        for (int k = 0; k < n; k++)
        {
            w[k] = MathF.Sin((float)((k + 0.5) * (Math.PI / (2.0 * n))));
        }
        return w;
    })];
    public static Vlc ScaleVlc => s_vlcs.Value[0];
    public static Vlc ScaleRlVlc => s_vlcs.Value[1];
    public static Vlc CoefVlc(int table) => s_vlcs.Value[2 + table];
    public static Vlc Vec4Vlc => s_vlcs.Value[4];
    public static Vlc Vec2Vlc => s_vlcs.Value[5];
    public static Vlc Vec1Vlc => s_vlcs.Value[6];
    private sealed class Channel
    {
        public int PrevBlockLen;
        public bool TransmitCoefs;
        public int NumSubframes;
        public readonly int[] SubframeLen = new int[MaxSubframes];
        public readonly int[] SubframeOffset = new int[MaxSubframes];
        public int CurSubframe;
        public int DecodedSamples;
        public bool Grouped;
        public int QuantStep;
        public bool ReuseSf;
        public int ScaleFactorStep;
        public int MaxScaleFactor;
        public readonly int[][] SavedScaleFactors = [new int[MaxBands], new int[MaxBands]];
        public int ScaleFactorIdx;
        public int[] ScaleFactors = [];
        public int TableIdx;
        public int Coeffs;
        public int NumVecCoeffs;
        public readonly float[] Out = new float[8192 + 4096];
    }
    private readonly Channel[] _ch;
    private readonly int _nch;
    private readonly int _samplesPerFrame = 512;
    private readonly int _maxNumSubframes = 4;
    private readonly int _subframeLenBits = 2;
    private readonly int _minSamplesPerSubframe = 128;
    private readonly int[] _numSfb = new int[BlockSizes];
    private readonly int[][] _sfbOffsets = [.. Enumerable.Range(0, BlockSizes).Select(_ => new int[MaxBands])];
    private readonly int[][][] _sfOffsets = [.. Enumerable.Range(0, BlockSizes).Select(_ => Enumerable.Range(0, BlockSizes).Select(_ => new int[MaxBands]).ToArray())];
    private readonly float[] _tmp = new float[8192];
    private readonly byte[] _frameData = new byte[MaxFrameSize + 64];
    private readonly List<float>[] _output;
    private MsbReader _pgb = new([], 0, 0);
    private MsbReader _gb = new([], 0, 0);
    private int _pbBits;
    private int _nextPacketStart;
    private int _packetOffset;
    private int _numSavedBits;
    private int _frameOffset;
    private bool _packetLoss = true;
    private bool _packetDone;
    private bool _skipFrame = true;
    private bool _parsedAllSubframes;
    private int _subframeLen;
    private int _channelsForCurSubframe;
    private readonly int[] _channelIndexes = new int[2];
    private int _numBands;
    private bool _transmitNumVecCoeffs;
    private int[] _curSfbOffsets = [];
    private int _tableIdx;
    private int _escLen;
    private int _numChgroups;
    private readonly (int NumChannels, bool Transform, bool[] TransformBand, float[] Matrix, int[] Channels)[] _chgroup =
        [.. Enumerable.Range(0, 2).Select(_ => (0, false, new bool[MaxBands], new float[4], new int[2]))];
    public WmaProStream(int sampleRate, int channels)
    {
        _nch = channels;
        _ch = [.. Enumerable.Range(0, channels).Select(_ => new Channel { PrevBlockLen = 512 })];
        _output = [.. Enumerable.Range(0, channels).Select(_ => new List<float>())];
        int rate = sampleRate > 44100 ? 48000 : sampleRate > 32000 ? 44100 : sampleRate > 24000 ? 32000 : 24000;
        int possible = 3;
        for (int i = 0; i < possible; i++)
        {
            int subframeLen = _samplesPerFrame >> i;
            int band = 1;
            _sfbOffsets[i][0] = 0;
            for (int x = 0; x < MaxBands - 1 && _sfbOffsets[i][band - 1] < subframeLen; x++)
            {
                int offset = (subframeLen * 2 * WmaProTables.CriticalFreq[x] / rate) + 2;
                offset &= ~3;
                if (offset > _sfbOffsets[i][band - 1])
                {
                    _sfbOffsets[i][band++] = offset;
                }
                if (offset >= subframeLen)
                {
                    break;
                }
            }
            _sfbOffsets[i][band - 1] = subframeLen;
            _numSfb[i] = band - 1;
        }
        for (int i = 0; i < possible; i++)
        {
            for (int b = 0; b < _numSfb[i]; b++)
            {
                int offset = ((_sfbOffsets[i][b] + _sfbOffsets[i][b + 1] - 1) << i) >> 1;
                for (int x = 0; x < possible; x++)
                {
                    int v = 0;
                    while (_sfbOffsets[x][v + 1] << x < offset)
                    {
                        v++;
                    }
                    _sfOffsets[i][x][b] = v;
                }
            }
        }
    }
    public int Channels => _nch;
    public bool PacketDone => _packetDone;
    public bool PacketLoss => _packetLoss;
    public int SkipPackets { get; set; }
    public bool EofDone { get; private set; }
    public int TrimStart { get; private set; }
    public int TrimEnd { get; private set; }
    public IReadOnlyList<List<float>> Output => _output;
    public int[] SfbOffsets(int table) => _sfbOffsets[table];
    public int NumSfb(int table) => _numSfb[table];
    private void SaveBits(MsbReader gb, int len, bool append)
    {
        int buflen;
        if (!append)
        {
            _frameOffset = gb.Pos & 7;
            _numSavedBits = _frameOffset;
            Array.Clear(_frameData, 0, Math.Min(_frameData.Length, (_pbBits >> 3) + 1));
            _pbBits = 0;
            buflen = (_numSavedBits + len + 7) >> 3;
        }
        else
        {
            buflen = (_pbBits + len + 7) >> 3;
        }
        if (len <= 0 || buflen > MaxFrameSize)
        {
            _packetLoss = true;
            return;
        }
        _numSavedBits += len;
        if (!append)
        {
            int at = gb.Pos;
            gb.Pos = at & ~7;
            PutBits(gb, _numSavedBits);
            gb.Pos = at;
        }
        else
        {
            int align = Math.Min(8 - (gb.Pos & 7), len);
            Put(gb.Read(align), align);
            len -= align;
            int at = gb.Pos;
            PutBits(gb, len);
            gb.Pos = at;
        }
        gb.Pos += len;
        _gb = new MsbReader(_frameData, 0, _numSavedBits) { Pos = _frameOffset };
    }
    private void PutBits(MsbReader from, int n)
    {
        while (n > 0)
        {
            int take = Math.Min(24, n);
            Put(from.Read(take), take);
            n -= take;
        }
    }
    private void Put(uint value, int n)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                _frameData[_pbBits >> 3] |= (byte)(0x80 >> (_pbBits & 7));
            }
            _pbBits++;
        }
    }
    public int DecodePacket(byte[] buf, int offset, int size)
    {
        if (size == 0)
        {
            _packetDone = false;
            if (EofDone)
            {
                return 0;
            }
            for (int i = 0; i < _nch; i++)
            {
                for (int k = 0; k < _samplesPerFrame; k++)
                {
                    _output[i].Add(k < _samplesPerFrame / 2 ? _ch[i].Out[k] : 0f);
                }
            }
            EofDone = true;
            _packetDone = true;
            return 0;
        }
        MsbReader gb;
        if (_packetDone || _packetLoss)
        {
            _packetDone = false;
            _nextPacketStart = size - Math.Min(size, 2048);
            int bufSize = Math.Min(size, 2048);
            gb = _pgb = new MsbReader(buf, offset, bufSize << 3);
            _ = gb.Read(6);
            int numBitsPrevFrame = (int)gb.Read(Log2FrameSize);
            _ = gb.Read(3);
            SkipPackets = (int)gb.Read(8);
            if (numBitsPrevFrame > 0)
            {
                int remainingPacketBits = gb.Size - gb.Pos;
                if (numBitsPrevFrame >= remainingPacketBits)
                {
                    numBitsPrevFrame = remainingPacketBits;
                    _packetDone = true;
                }
                SaveBits(gb, numBitsPrevFrame, true);
                if (!_packetLoss)
                {
                    _ = DecodeFrame();
                }
            }
            if (_packetLoss)
            {
                _numSavedBits = 0;
                _packetLoss = false;
            }
        }
        else
        {
            if (size < _nextPacketStart)
            {
                _packetLoss = true;
                return -1;
            }
            gb = _pgb = new MsbReader(buf, offset, (size - _nextPacketStart) << 3);
            gb.Pos = _packetOffset;
            int frameSize;
            if (gb.Left > Log2FrameSize && (frameSize = (int)gb.Peek(Log2FrameSize)) != 0 && frameSize <= gb.Left)
            {
                SaveBits(gb, frameSize, false);
                if (!_packetLoss)
                {
                    _packetDone = !DecodeFrame();
                }
            }
            else
            {
                _packetDone = true;
            }
        }
        if (gb.Left < 0)
        {
            _packetLoss = true;
        }
        if (_packetDone && !_packetLoss && gb.Left > 0)
        {
            SaveBits(gb, gb.Left, false);
        }
        _packetOffset = gb.Pos & 7;
        return _packetLoss ? -1 : gb.Pos >> 3;
    }
    private bool DecodeFrame()
    {
        try
        {
            return Frame();
        }
        catch (InvalidDataException)
        {
            _packetLoss = true;
            return false;
        }
    }
    private bool Frame()
    {
        MsbReader gb = _gb;
        int len = (int)gb.Read(Log2FrameSize);
        if (!TileHeader())
        {
            _packetLoss = true;
            return false;
        }
        if (_nch > 1 && gb.Bit() != 0 && gb.Bit() != 0)
        {
            gb.Pos += 4 * _nch * _nch;
        }
        _ = gb.Read(8);
        if (gb.Bit() != 0)
        {
            if (gb.Bit() != 0)
            {
                TrimStart = (int)gb.Read(10);
            }
            if (gb.Bit() != 0)
            {
                TrimEnd = (int)gb.Read(10);
            }
        }
        else
        {
            TrimStart = TrimEnd = 0;
        }
        _parsedAllSubframes = false;
        foreach (Channel c in _ch)
        {
            c.DecodedSamples = 0;
            c.CurSubframe = 0;
            c.ReuseSf = false;
        }
        while (!_parsedAllSubframes)
        {
            if (!Subframe())
            {
                _packetLoss = true;
                return false;
            }
        }
        if (_skipFrame)
        {
            _skipFrame = false;
        }
        else
        {
            for (int i = 0; i < _nch; i++)
            {
                for (int k = 0; k < _samplesPerFrame; k++)
                {
                    _output[i].Add(_ch[i].Out[k]);
                }
            }
        }
        foreach (Channel c in _ch)
        {
            Array.Copy(c.Out, _samplesPerFrame, c.Out, 0, _samplesPerFrame / 2);
        }
        if (len != gb.Pos - _frameOffset + 2)
        {
            _packetLoss = true;
            return false;
        }
        gb.Pos += len - (gb.Pos - _frameOffset) - 1;
        return gb.Bit() != 0;
    }
    private int SubframeLength(int offset)
    {
        MsbReader gb = _gb;
        if (offset == _samplesPerFrame - _minSamplesPerSubframe)
        {
            return _minSamplesPerSubframe;
        }
        if (gb.Left < 1)
        {
            return -1;
        }
        int shift = 0;
        if (gb.Bit() != 0)
        {
            shift = 1 + (int)gb.Read(_subframeLenBits - 1);
        }
        int len = _samplesPerFrame >> shift;
        return len < _minSamplesPerSubframe || len > _samplesPerFrame ? -1 : len;
    }
    private bool TileHeader()
    {
        MsbReader gb = _gb;
        int[] numSamples = new int[_nch];
        bool[] contains = new bool[_nch];
        int channelsForCur = _nch;
        int minChannelLen = 0;
        foreach (Channel c in _ch)
        {
            c.NumSubframes = 0;
        }
        bool fixedLayout = _maxNumSubframes == 1 || gb.Bit() != 0;
        do
        {
            for (int c = 0; c < _nch; c++)
            {
                contains[c] = numSamples[c] == minChannelLen
                    && (fixedLayout || channelsForCur == 1 || minChannelLen == _samplesPerFrame - _minSamplesPerSubframe || gb.Bit() != 0);
            }
            int subframeLen = SubframeLength(minChannelLen);
            if (subframeLen <= 0)
            {
                return false;
            }
            minChannelLen += subframeLen;
            for (int c = 0; c < _nch; c++)
            {
                Channel ch = _ch[c];
                if (contains[c])
                {
                    if (ch.NumSubframes >= MaxSubframes)
                    {
                        return false;
                    }
                    ch.SubframeLen[ch.NumSubframes] = subframeLen;
                    numSamples[c] += subframeLen;
                    ch.NumSubframes++;
                    if (numSamples[c] > _samplesPerFrame)
                    {
                        return false;
                    }
                }
                else if (numSamples[c] <= minChannelLen)
                {
                    if (numSamples[c] < minChannelLen)
                    {
                        channelsForCur = 0;
                        minChannelLen = numSamples[c];
                    }
                    channelsForCur++;
                }
            }
        }
        while (minChannelLen < _samplesPerFrame);
        foreach (Channel ch in _ch)
        {
            int offset = 0;
            for (int i = 0; i < ch.NumSubframes; i++)
            {
                ch.SubframeOffset[i] = offset;
                offset += ch.SubframeLen[i];
            }
        }
        return true;
    }
    private bool ChannelTransform()
    {
        MsbReader gb = _gb;
        _numChgroups = 0;
        if (_nch <= 1)
        {
            return true;
        }
        int remaining = _channelsForCurSubframe;
        if (gb.Bit() != 0)
        {
            return false;
        }
        for (_numChgroups = 0; remaining > 0 && _numChgroups < _channelsForCurSubframe; _numChgroups++)
        {
            ref (int NumChannels, bool Transform, bool[] TransformBand, float[] Matrix, int[] Channels) g = ref _chgroup[_numChgroups];
            g.NumChannels = remaining;
            g.Transform = false;
            int n = 0;
            for (int i = 0; i < _channelsForCurSubframe; i++)
            {
                int idx = _channelIndexes[i];
                if (!_ch[idx].Grouped)
                {
                    g.Channels[n++] = idx;
                }
                _ch[idx].Grouped = true;
            }
            if (g.NumChannels == 2)
            {
                if (gb.Bit() != 0)
                {
                    if (gb.Bit() != 0)
                    {
                        return false;
                    }
                }
                else
                {
                    g.Transform = true;
                    g.Matrix[0] = 1f;
                    g.Matrix[1] = -1f;
                    g.Matrix[2] = 1f;
                    g.Matrix[3] = 1f;
                }
            }
            if (g.Transform)
            {
                if (gb.Bit() == 0)
                {
                    for (int i = 0; i < _numBands; i++)
                    {
                        g.TransformBand[i] = gb.Bit() != 0;
                    }
                }
                else
                {
                    Array.Fill(g.TransformBand, true, 0, _numBands);
                }
            }
            remaining -= g.NumChannels;
        }
        return true;
    }
    private static readonly float[] s_fval = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private void Coeffs(int c)
    {
        MsbReader gb = _gb;
        Channel ci = _ch[c];
        int table = gb.Bit();
        Vlc vlc = CoefVlc(table);
        ushort[] run = table != 0 ? WmaProTables.Coef1Run : WmaProTables.Coef0Run;
        float[] level = table != 0 ? WmaProTables.Coef1Level : WmaProTables.Coef0Level;
        bool rlMode = false;
        int cur = 0;
        int numZeros = 0;
        float[] coeffs = ci.Out;
        int at = ci.Coeffs;
        Span<float> vals = stackalloc float[4];
        while ((_transmitNumVecCoeffs || !rlMode) && cur + 3 < ci.NumVecCoeffs)
        {
            int idx = Vec4Vlc.Read(gb);
            if (idx < 0)
            {
                for (int i = 0; i < 4; i += 2)
                {
                    idx = Vec2Vlc.Read(gb);
                    if (idx < 0)
                    {
                        uint v0 = (uint)Vec1Vlc.Read(gb);
                        if (v0 == 100)
                        {
                            v0 += LargeVal(gb);
                        }
                        uint v1 = (uint)Vec1Vlc.Read(gb);
                        if (v1 == 100)
                        {
                            v1 += LargeVal(gb);
                        }
                        vals[i] = v0;
                        vals[i + 1] = v1;
                    }
                    else
                    {
                        vals[i] = s_fval[idx >> 4];
                        vals[i + 1] = s_fval[idx & 0xF];
                    }
                }
            }
            else
            {
                vals[0] = s_fval[idx >> 12];
                vals[1] = s_fval[(idx >> 8) & 0xF];
                vals[2] = s_fval[(idx >> 4) & 0xF];
                vals[3] = s_fval[idx & 0xF];
            }
            for (int i = 0; i < 4; i++)
            {
                if (vals[i] != 0)
                {
                    coeffs[at + cur] = gb.Bit() != 0 ? vals[i] : -vals[i];
                    numZeros = 0;
                }
                else
                {
                    coeffs[at + cur] = 0;
                    rlMode |= ++numZeros > _subframeLen >> 8;
                }
                cur++;
            }
        }
        if (cur < _subframeLen)
        {
            Array.Clear(coeffs, at + cur, _subframeLen - cur);
            RunLevel(gb, vlc, level, run, coeffs, at, cur);
        }
    }
    public static uint LargeVal(MsbReader gb)
    {
        int n = 8;
        if (gb.Bit() != 0)
        {
            n += 8;
            if (gb.Bit() != 0)
            {
                n += 8;
                if (gb.Bit() != 0)
                {
                    n += 7;
                }
            }
        }
        return gb.Read(n);
    }
    private void RunLevel(MsbReader gb, Vlc vlc, float[] level, ushort[] run, float[] ptr, int at, int offset)
    {
        int mask = _subframeLen - 1;
        for (; offset < _subframeLen; offset++)
        {
            int code = vlc.Read(gb);
            if (code > 1)
            {
                offset += run[code];
                ptr[at + (offset & mask)] = gb.Bit() != 0 ? level[code] : -level[code];
            }
            else if (code == 1)
            {
                break;
            }
            else
            {
                int lv = (int)LargeVal(gb);
                if (gb.Bit() != 0)
                {
                    if (gb.Bit() != 0)
                    {
                        if (gb.Bit() != 0)
                        {
                            return;
                        }
                        offset += (int)gb.Read(_escLen) + 4;
                    }
                    else
                    {
                        offset += (int)gb.Read(2) + 1;
                    }
                }
                ptr[at + (offset & mask)] = gb.Bit() != 0 ? lv : -lv;
            }
        }
    }
    private bool ScaleFactors()
    {
        MsbReader gb = _gb;
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            Channel ch = _ch[_channelIndexes[i]];
            ch.ScaleFactors = ch.SavedScaleFactors[ch.ScaleFactorIdx == 0 ? 1 : 0];
            if (ch.ReuseSf)
            {
                int[] sfOffsets = _sfOffsets[_tableIdx][ch.TableIdx];
                for (int b = 0; b < _numBands; b++)
                {
                    ch.ScaleFactors[b] = ch.SavedScaleFactors[ch.ScaleFactorIdx][sfOffsets[b]];
                }
            }
            if (ch.CurSubframe == 0 || gb.Bit() != 0)
            {
                if (!ch.ReuseSf)
                {
                    ch.ScaleFactorStep = (int)gb.Read(2) + 1;
                    int val = 45 / ch.ScaleFactorStep;
                    for (int b = 0; b < _numBands; b++)
                    {
                        val += ScaleVlc.Read(gb);
                        ch.ScaleFactors[b] = val;
                    }
                }
                else
                {
                    for (int b = 0; b < _numBands; b++)
                    {
                        int idx = ScaleRlVlc.Read(gb);
                        int skip;
                        int val;
                        bool negative;
                        if (idx == 0)
                        {
                            uint code = gb.Read(14);
                            val = (int)(code >> 6);
                            negative = (code & 1) == 0;
                            skip = (int)((code & 0x3f) >> 1);
                        }
                        else if (idx == 1)
                        {
                            break;
                        }
                        else
                        {
                            skip = WmaProTables.ScaleRlRun[idx];
                            val = WmaProTables.ScaleRlLevel[idx];
                            negative = gb.Bit() == 0;
                        }
                        b += skip;
                        if (b >= _numBands)
                        {
                            return false;
                        }
                        ch.ScaleFactors[b] += negative ? -val : val;
                    }
                }
                ch.ScaleFactorIdx = ch.ScaleFactorIdx == 0 ? 1 : 0;
                ch.TableIdx = _tableIdx;
                ch.ReuseSf = true;
            }
            ch.MaxScaleFactor = ch.ScaleFactors[0];
            for (int b = 1; b < _numBands; b++)
            {
                ch.MaxScaleFactor = Math.Max(ch.MaxScaleFactor, ch.ScaleFactors[b]);
            }
        }
        return true;
    }
    private void InverseChannelTransform()
    {
        for (int i = 0; i < _numChgroups; i++)
        {
            (int numChannels, bool transform, bool[] tb, float[] mat, int[] chans) = _chgroup[i];
            if (!transform || numChannels != 2)
            {
                continue;
            }
            float[] a = _ch[chans[0]].Out;
            float[] b = _ch[chans[1]].Out;
            int ao = _ch[chans[0]].Coeffs;
            int bo = _ch[chans[1]].Coeffs;
            for (int band = 0; band < _numBands; band++)
            {
                int end = Math.Min(_curSfbOffsets[band + 1], _subframeLen);
                if (tb[band])
                {
                    for (int y = _curSfbOffsets[band]; y < end; y++)
                    {
                        float d0 = a[ao + y];
                        float d1 = b[bo + y];
                        float s0 = 0;
                        s0 += d0 * mat[0];
                        s0 += d1 * mat[1];
                        float s1 = 0;
                        s1 += d0 * mat[2];
                        s1 += d1 * mat[3];
                        a[ao + y] = s0;
                        b[bo + y] = s1;
                    }
                }
                else if (_nch == 2)
                {
                    for (int y = _curSfbOffsets[band]; y < end; y++)
                    {
                        a[ao + y] *= 181f / 128;
                        b[bo + y] *= 181f / 128;
                    }
                }
            }
        }
    }
    private void Window()
    {
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            Channel ch = _ch[_channelIndexes[i]];
            int winlen = ch.PrevBlockLen;
            int start = ch.Coeffs - (winlen >> 1);
            if (_subframeLen < winlen)
            {
                start += (winlen - _subframeLen) >> 1;
                winlen = _subframeLen;
            }
            float[] win = s_windows[System.Numerics.BitOperations.Log2((uint)winlen) - BlockMinBits];
            int len = winlen >> 1;
            float[] o = ch.Out;
            int dst = start + len;
            int src1 = start + len;
            for (int k = -len, j = len - 1; k < 0; k++, j--)
            {
                float s0 = o[dst + k];
                float s1 = o[src1 + j];
                float wi = win[len + k];
                float wj = win[len + j];
                o[dst + k] = (s0 * wj) - (s1 * wi);
                o[dst + j] = (s0 * wi) + (s1 * wj);
            }
            ch.PrevBlockLen = _subframeLen;
        }
    }
    private bool Subframe()
    {
        MsbReader gb = _gb;
        int offset = _samplesPerFrame;
        int subframeLen = _samplesPerFrame;
        int totalSamples = _samplesPerFrame * _nch;
        bool transmitCoeffs = false;
        foreach (Channel c in _ch)
        {
            c.Grouped = false;
            if (offset > c.DecodedSamples)
            {
                offset = c.DecodedSamples;
                subframeLen = c.SubframeLen[c.CurSubframe];
            }
        }
        _channelsForCurSubframe = 0;
        for (int i = 0; i < _nch; i++)
        {
            Channel c = _ch[i];
            totalSamples -= c.DecodedSamples;
            if (offset == c.DecodedSamples && subframeLen == c.SubframeLen[c.CurSubframe])
            {
                totalSamples -= c.SubframeLen[c.CurSubframe];
                c.DecodedSamples += c.SubframeLen[c.CurSubframe];
                _channelIndexes[_channelsForCurSubframe++] = i;
            }
        }
        if (totalSamples == 0)
        {
            _parsedAllSubframes = true;
        }
        _tableIdx = System.Numerics.BitOperations.Log2((uint)(_samplesPerFrame / subframeLen));
        _numBands = _numSfb[_tableIdx];
        _curSfbOffsets = _sfbOffsets[_tableIdx];
        offset += _samplesPerFrame >> 1;
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            _ch[_channelIndexes[i]].Coeffs = offset;
        }
        _subframeLen = subframeLen;
        _escLen = System.Numerics.BitOperations.Log2((uint)(subframeLen - 1)) + 1;
        if (gb.Bit() != 0)
        {
            int numFillBits = (int)gb.Read(2);
            if (numFillBits == 0)
            {
                int len = (int)gb.Read(4);
                numFillBits = (int)gb.Read(len) + 1;
            }
            if (gb.Pos + numFillBits > _numSavedBits)
            {
                return false;
            }
            gb.Pos += numFillBits;
        }
        if (gb.Bit() != 0)
        {
            return false;
        }
        if (!ChannelTransform())
        {
            return false;
        }
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            Channel c = _ch[_channelIndexes[i]];
            c.TransmitCoefs = gb.Bit() != 0;
            transmitCoeffs |= c.TransmitCoefs;
        }
        if (transmitCoeffs)
        {
            int quantStep = 90 * 16 >> 4;
            _transmitNumVecCoeffs = gb.Bit() != 0;
            if (_transmitNumVecCoeffs)
            {
                int numBits = System.Numerics.BitOperations.Log2((uint)((_subframeLen + 3) / 4)) + 1;
                for (int i = 0; i < _channelsForCurSubframe; i++)
                {
                    int n = (int)gb.Read(numBits) << 2;
                    if (n > _subframeLen)
                    {
                        return false;
                    }
                    _ch[_channelIndexes[i]].NumVecCoeffs = n;
                }
            }
            else
            {
                for (int i = 0; i < _channelsForCurSubframe; i++)
                {
                    _ch[_channelIndexes[i]].NumVecCoeffs = _subframeLen;
                }
            }
            int step = gb.Signed(6);
            quantStep += step;
            if (step is -32 or 31)
            {
                bool negative = step != 31;
                int quant = 0;
                while (gb.Pos + 5 < _numSavedBits && (step = (int)gb.Read(5)) == 31)
                {
                    quant += 31;
                }
                quantStep += negative ? -(quant + step) : quant + step;
            }
            if (_channelsForCurSubframe == 1)
            {
                _ch[_channelIndexes[0]].QuantStep = quantStep;
            }
            else
            {
                int modifierLen = (int)gb.Read(3);
                for (int i = 0; i < _channelsForCurSubframe; i++)
                {
                    Channel c = _ch[_channelIndexes[i]];
                    c.QuantStep = quantStep;
                    if (gb.Bit() != 0)
                    {
                        c.QuantStep += modifierLen != 0 ? (int)gb.Read(modifierLen) + 1 : 1;
                    }
                }
            }
            if (!ScaleFactors())
            {
                return false;
            }
        }
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            Channel c = _ch[_channelIndexes[i]];
            if (c.TransmitCoefs && gb.Pos < _numSavedBits)
            {
                Coeffs(_channelIndexes[i]);
            }
            else
            {
                Array.Clear(c.Out, c.Coeffs, subframeLen);
            }
        }
        if (transmitCoeffs)
        {
            InverseChannelTransform();
            double scale = 1.0 / (subframeLen >> 1) / 32768;
            for (int i = 0; i < _channelsForCurSubframe; i++)
            {
                Channel c = _ch[_channelIndexes[i]];
                int si = 0;
                for (int b = 0; b < _numBands; b++)
                {
                    int end = Math.Min(_curSfbOffsets[b + 1], _subframeLen);
                    int exp = c.QuantStep - ((c.MaxScaleFactor - c.ScaleFactors[si++]) * c.ScaleFactorStep);
                    float quant = (float)Math.Pow(10, exp / 20.0);
                    for (int k = _curSfbOffsets[b]; k < end; k++)
                    {
                        _tmp[k] = c.Out[c.Coeffs + k] * quant;
                    }
                }
                Dct4.InverseHalf(_tmp.AsSpan(0, subframeLen), c.Out.AsSpan(c.Coeffs, subframeLen), -scale);
            }
        }
        Window();
        for (int i = 0; i < _channelsForCurSubframe; i++)
        {
            Channel c = _ch[_channelIndexes[i]];
            if (c.CurSubframe >= c.NumSubframes)
            {
                return false;
            }
            c.CurSubframe++;
        }
        return true;
    }
}
internal static class Xma
{
    public const int PacketSize = 2048;
    public static (int Streams, uint ChannelMask, int SamplesEncoded, int BytesPerBlock, int PlayBegin, int PlayLength, int LoopBegin, int LoopLength, int LoopCount, int EncoderVersion, int BlockCount) Format(WemInfo i)
    {
        byte[] x = i.Extra;
        bool big = i.Big;
        return (Bin.U16(x, 0, big), Bin.U32(x, 2, big), (int)Bin.U32(x, 6, big), (int)Bin.U32(x, 10, big), (int)Bin.U32(x, 14, big),
            (int)Bin.U32(x, 18, big), (int)Bin.U32(x, 22, big), (int)Bin.U32(x, 26, big), x[30], x[31], Bin.U16(x, 32, big));
    }
    public static short[] Decode(WemInfo i)
    {
        int streams = Math.Max(1, Format(i).Streams);
        List<WmaProStream> decoders = [];
        for (int s = 0; s < streams; s++)
        {
            decoders.Add(new WmaProStream(i.Rate, (s + 1) * 2 > i.Channels ? 1 : 2));
        }
        if (decoders.Sum(d => d.Channels) != i.Channels)
        {
            throw new InvalidDataException($"XMA2 with {streams} stream(s) does not add up to {i.Channels} channels");
        }
        byte[] data = i.Data;
        int current = 0;
        int trimStart = 0;
        int trimEnd = 0;
        for (int at = 0; at + PacketSize <= data.Length; at += PacketSize)
        {
            int pos = at;
            int left = PacketSize;
            do
            {
                WmaProStream d = decoders[current];
                int used = d.EofDone ? 0 : d.DecodePacket(data, pos, left);
                if (decoders[0].TrimStart != 0)
                {
                    trimStart = decoders[0].TrimStart;
                }
                if (decoders[0].TrimEnd != 0)
                {
                    trimEnd = decoders[0].TrimEnd;
                }
                if (used < 0)
                {
                    current = 0;
                    break;
                }
                if (d.PacketDone || d.PacketLoss)
                {
                    if (d.SkipPackets != 0)
                    {
                        current = decoders.Select((x, n) => (x.SkipPackets, n)).Min().n;
                    }
                    foreach (WmaProStream x in decoders)
                    {
                        x.SkipPackets = Math.Max(0, x.SkipPackets - 1);
                    }
                }
                pos += used;
                left -= used;
            }
            while (left > 0);
        }
        foreach (WmaProStream d in decoders)
        {
            _ = d.DecodePacket(data, 0, 0);
        }
        int total = decoders.Min(d => d.Output[0].Count);
        int frames = total - 64;
        frames -= Math.Clamp(trimEnd + trimStart - 128 - 64, 0, Math.Max(0, frames));
        frames = Math.Max(0, frames);
        short[] pcm = new short[frames * i.Channels];
        int ch = 0;
        foreach (WmaProStream d in decoders)
        {
            for (int c = 0; c < d.Channels; c++, ch++)
            {
                List<float> o = d.Output[c];
                for (int k = 0; k < frames; k++)
                {
                    pcm[(k * i.Channels) + ch] = (short)Math.Clamp(MathF.Round(o[k + 64] * 32768f), -32768f, 32767f);
                }
            }
        }
        return pcm;
    }
}
