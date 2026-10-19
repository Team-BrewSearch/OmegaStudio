using System.Numerics;
using System.Runtime.InteropServices;
using DWSSK.Formats.Audio;
using DWSSK.Formats.Core;

namespace DWSSK.Formats.Video;

internal sealed class BinkAudio
{
    private const int Powers = 0;
    private const int Quantizers = 32;
    private const int Weights = 128;
    private static readonly byte[] s_rle = [2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 32, 64];
    private static readonly ushort[] s_freqs =
    [
        0, 100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320, 2700,
        3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500,
    ];
    private static readonly Lazy<float[]> s_tables = new(LoadTables);

    private readonly int _channels;
    private readonly int _frameLen;
    private readonly int _overlap;
    private readonly int _shift;
    private readonly float _root;
    private readonly int _numBands;
    private readonly int[] _bands = new int[26];
    private readonly float[] _coeffs;
    private readonly short[] _block;
    private readonly short[] _previous;
    private bool _first = true;

    public BinkAudio(BinkTrack track)
    {
        if (!track.Dct)
        {
            throw new NotSupportedException("Bink RDFT audio is not decoded; only the DCT variant is");
        }
        if (track.Channels is not (1 or 2))
        {
            throw new NotSupportedException($"Bink audio with {track.Channels} channels");
        }
        _channels = track.Channels;
        _frameLen = track.Rate >= 44100 ? 2048 : track.Rate >= 22050 ? 1024 : 512;
        _overlap = _frameLen * _channels / 16;
        _shift = BitOperations.Log2((uint)_overlap);
        _root = 2f / MathF.Sqrt(_frameLen);
        uint half = (uint)(track.Rate + 1) / 2;
        while (_numBands < s_freqs.Length && s_freqs[_numBands] < half)
        {
            _numBands++;
        }
        for (int i = 0; i < _numBands; i++)
        {
            _bands[i] = Math.Max(1, (int)((uint)(_frameLen / 2) * s_freqs[i] / half));
        }
        _bands[_numBands] = _frameLen / 2;
        _coeffs = new float[_frameLen * _channels];
        _block = new short[_frameLen * _channels];
        _previous = new short[_overlap];
    }

    public int Channels => _channels;

    private static float[] LoadTables()
    {
        using Stream stream = typeof(BinkAudio).Assembly.GetManifestResourceStream("DWSSK.bink_audio_tables.bin")
            ?? throw new FileNotFoundException("the Bink audio tables are missing from the plugin");
        byte[] raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        return MemoryMarshal.Cast<byte, float>(raw).ToArray();
    }

    public void Packet(byte[] data, int offset, int length, List<short> output)
    {
        int end = offset + length;
        int remaining = (int)(Bin.U32(data, offset, false) & ~15u) / 2;
        int pos = offset + 4;
        while (remaining > 0 && pos < end)
        {
            pos = Block(data, pos, end);
            int n = Math.Min(_block.Length - _overlap, remaining);
            output.AddRange(_block.AsSpan(0, n));
            remaining -= n;
        }
    }

    private int Block(byte[] data, int pos, int end)
    {
        float[] tables = s_tables.Value;
        Reader r = new(data, pos, end);
        for (int ch = 0; ch < _channels; ch++)
        {
            Span<float> c = _coeffs.AsSpan(ch * _frameLen, _frameLen);
            if (Coefficients(r, c, tables))
            {
                BinkDct.Inverse(c, tables.AsSpan(Weights));
            }
            else
            {
                c.Clear();
            }
        }
        for (int i = 0; i < _frameLen; i++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                _block[(i * _channels) + ch] = VorbisDecoder.Pcm(_coeffs[(ch * _frameLen) + i] * _root);
            }
        }
        if (!_first)
        {
            for (int i = 0; i < _overlap; i++)
            {
                _block[i] = (short)(_previous[i] + ((i * (_block[i] - _previous[i])) >> _shift));
            }
        }
        _first = false;
        _block.AsSpan(_block.Length - _overlap).CopyTo(_previous);
        return r.Pos;
    }

    private bool Coefficients(Reader r, Span<float> c, float[] tables)
    {
        Span<float> quant = stackalloc float[s_freqs.Length];
        for (int k = 0; k < 2; k++)
        {
            if (!r.Read(29, out uint v))
            {
                return false;
            }
            float f = (int)((v & 0x0FFFFFFF) >> 5) * tables[Powers + (int)(v & 31)];
            c[k] = (v & 0x10000000) != 0 ? -f : f;
        }
        for (int i = 0; i < _numBands; i++)
        {
            if (!r.Read(8, out uint v))
            {
                return false;
            }
            quant[i] = tables[Quantizers + (int)Math.Min(v, 95)];
        }
        int band = 0;
        float q = 0f;
        int at = 2;
        while (at < _frameLen)
        {
            if (!r.Read(1, out uint run))
            {
                return false;
            }
            int j = at + 8;
            if (run != 0)
            {
                if (!r.Read(4, out uint v))
                {
                    return false;
                }
                j = at + (s_rle[v] * 8);
            }
            j = Math.Min(j, _frameLen);
            if (!r.Read(4, out uint width))
            {
                return false;
            }
            if (width == 0)
            {
                c[at..j].Clear();
                at = j;
                while (j > 2 * _bands[band])
                {
                    q = quant[band++];
                }
                continue;
            }
            for (; at < j; at++)
            {
                if (at == 2 * _bands[band])
                {
                    q = quant[band++];
                }
                if (!r.Read((int)width, out uint v))
                {
                    return false;
                }
                c[at] = v == 0 ? 0f : (int)v * (r.Bit() != 0 ? -q : q);
            }
        }
        return true;
    }

    private sealed class Reader(byte[] data, int pos, int end)
    {
        private uint _buf = Bin.U32(data, pos, false) >> 2;
        private int _bits = 30;

        public int Pos { get; private set; } = pos + 4;

        public bool Read(int n, out uint v)
        {
            if (_bits >= n)
            {
                v = _buf & ((1u << n) - 1);
                _buf >>= n;
                _bits -= n;
                return true;
            }
            if (Pos >= end)
            {
                v = 0;
                return false;
            }
            uint w = Bin.U32(data, Pos, false);
            Pos += 4;
            v = (_buf | (w << _bits)) & ((1u << n) - 1);
            _buf = w >> (n - _bits);
            _bits += 32 - n;
            return true;
        }

        public uint Bit()
        {
            if (_bits == 0)
            {
                _buf = Pos + 4 <= data.Length ? Bin.U32(data, Pos, false) : 0;
                Pos += 4;
                _bits = 32;
            }
            uint b = _buf & 1;
            _buf >>= 1;
            _bits--;
            return b;
        }
    }
}
