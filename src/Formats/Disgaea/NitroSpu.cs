namespace Disgaea.Formats;
internal sealed class NitroWaveData
{
    public NitroWaveData(ReadOnlySpan<byte> block)
    {
        if (block.Length < 0x0C)
        {
            throw new InvalidDataException("a wave block is shorter than its 12-byte header");
        }
        Type = block[0] & 3;
        Loops = block[1] != 0;
        Timer = (ushort)(block[4] | (block[5] << 8));
        LoopStart = (block[6] | (block[7] << 8)) * 4;
        Length = (int)Math.Min(int.MaxValue / 8, (uint)(block[8] | (block[9] << 8) | (block[10] << 16) | (block[11] << 24))) * 4;
        Data = block[0x0C..].ToArray();
    }
    public int Type { get; }
    public bool Loops { get; }
    public ushort Timer { get; }
    public int LoopStart { get; }
    public int Length { get; }
    public byte[] Data { get; }
}
internal sealed class NitroSpuChannel
{
    private static readonly short[] s_psg =
    [
        -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, -0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF, 0x7FFF,
        -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF, -0x7FFF,
    ];
    private static readonly int[] s_shift = [4, 3, 2, 0];
    private int _kind;
    private int _duty;
    private byte[] _data = [];
    private int _loopStart;
    private int _length;
    private bool _repeat;
    private uint _timer;
    private uint _reload;
    private int _position;
    private int _sample;
    private int _adpcm;
    private int _index;
    private int _loopAdpcm;
    private int _loopIndex;
    private int _byte;
    private int _noise;
    private int _volume;
    private int _shift;
    private int _pan;
    private int _origin;
    private long _steps;
    private bool _skipped;
    public bool Enabled { get; private set; }
    public void SetVolume(int multiplier, int divider)
    {
        _volume = multiplier == 127 ? 128 : multiplier;
        _shift = s_shift[divider & 3];
    }
    public void SetPan(int pan)
    {
        _pan = pan == 127 ? 128 : pan;
    }
    public void SetTimer(ushort register)
    {
        _reload = register;
    }
    public void StartWave(NitroWaveData wave)
    {
        _kind = wave.Type;
        _data = wave.Data;
        _loopStart = wave.LoopStart;
        _length = wave.Length;
        _repeat = wave.Loops;
        Start(-3);
    }
    public void StartPsg(int duty)
    {
        _kind = 3;
        _duty = duty & 7;
        Start(-1);
    }
    public void StartNoise()
    {
        _kind = 4;
        Start(-1);
    }
    public void Stop()
    {
        Enabled = false;
    }
    public void Mix(ref long left, ref long right)
    {
        if (!Enabled || (_kind < 3 && _loopStart + _length < 16))
        {
            return;
        }
        _timer += 512;
        while (_timer >> 16 != 0)
        {
            _timer = _reload + (_timer - 0x10000);
            Next();
            if (!Enabled)
            {
                break;
            }
        }
        long value = (long)(_sample << _shift) * _volume;
        left += (value * (128 - _pan)) >> 10;
        right += (value * _pan) >> 10;
    }
    public void Skip(long samples)
    {
        if (!Enabled || (_kind < 3 && _loopStart + _length < 16))
        {
            return;
        }
        long total = _timer + (512L * samples);
        long steps = 0;
        if (total >= 0x10000)
        {
            long period = 0x10000 - _reload;
            steps = ((total - 0x10000) / period) + 1;
            total -= steps * period;
        }
        _timer = (uint)total;
        if (steps == 0)
        {
            return;
        }
        _skipped = true;
        if (_kind < 3)
        {
            (long end, long loop) = Bounds();
            if (_origin + _steps + steps >= end && !_repeat)
            {
                _sample = 0;
                Enabled = false;
                return;
            }
        }
        _steps += steps;
    }
    public void Catchup()
    {
        if (!_skipped || !Enabled)
        {
            _skipped = false;
            return;
        }
        _skipped = false;
        long steps = _steps;
        if (_kind < 3)
        {
            (long end, long loop) = Bounds();
            long first = end - _origin;
            if (steps > first)
            {
                steps = first + ((steps - first) % Math.Max(1, end - loop));
            }
        }
        else
        {
            steps %= _kind == 3 ? 8 : 0x7FFF;
        }
        uint timer = _timer;
        Start(_origin);
        for (long i = 0; i < steps; i++)
        {
            Next();
        }
        _timer = timer;
    }
    private (long End, long Loop) Bounds()
    {
        long end = _loopStart + _length;
        return _kind switch
        {
            0 => (end, _loopStart),
            1 => ((end + 1) >> 1, _loopStart >> 1),
            _ => (end << 1, (long)_loopStart << 1),
        };
    }
    private void Start(int position)
    {
        _timer = _reload;
        _origin = position;
        _position = position;
        _steps = 0;
        _skipped = false;
        _sample = 0;
        _noise = 0x7FFF;
        Enabled = true;
    }
    private void Next()
    {
        _steps++;
        switch (_kind)
        {
            case 0:
                Pcm8();
                break;
            case 1:
                Pcm16();
                break;
            case 2:
                Adpcm();
                break;
            case 3:
                _position++;
                _sample = s_psg[(_duty * 8) + (_position & 7)];
                break;
            default:
                if ((_noise & 1) != 0)
                {
                    _noise = (_noise >> 1) ^ 0x6000;
                    _sample = -0x7FFF;
                }
                else
                {
                    _noise >>= 1;
                    _sample = 0x7FFF;
                }
                break;
        }
    }
    private bool AtEnd(int bytes)
    {
        if (bytes < _loopStart + _length)
        {
            return false;
        }
        if (!_repeat)
        {
            _sample = 0;
            Enabled = false;
        }
        return true;
    }
    private void Pcm8()
    {
        if (++_position < 0)
        {
            return;
        }
        if (AtEnd(_position))
        {
            if (!Enabled)
            {
                return;
            }
            _position = _loopStart;
        }
        _sample = (sbyte)Read(_position) << 8;
    }
    private void Pcm16()
    {
        if (++_position < 0)
        {
            return;
        }
        if (AtEnd(_position << 1))
        {
            if (!Enabled)
            {
                return;
            }
            _position = _loopStart >> 1;
        }
        _sample = (short)(Read(_position << 1) | (Read((_position << 1) + 1) << 8));
    }
    private void Adpcm()
    {
        if (++_position < 8)
        {
            if (_position == 0)
            {
                _adpcm = (short)(Read(0) | (Read(1) << 8));
                _index = Math.Min(Read(2) & 0x7F, 88);
                _loopAdpcm = _adpcm;
                _loopIndex = _index;
            }
            return;
        }
        if (AtEnd(_position >> 1))
        {
            if (!Enabled)
            {
                return;
            }
            _position = _loopStart << 1;
            _adpcm = _loopAdpcm;
            _index = _loopIndex;
            _byte = Read(_loopStart);
        }
        else
        {
            _byte = (_position & 1) == 0 ? Read(_position >> 1) : _byte >> 4;
            int step = NitroAudio.Steps[_index];
            int diff = step >> 3;
            diff += (_byte & 1) != 0 ? step >> 2 : 0;
            diff += (_byte & 2) != 0 ? step >> 1 : 0;
            diff += (_byte & 4) != 0 ? step : 0;
            _adpcm = (_byte & 8) != 0 ? Math.Max(_adpcm - diff, -0x7FFF) : Math.Min(_adpcm + diff, 0x7FFF);
            _index = Math.Clamp(_index + NitroAudio.IndexShift[_byte & 7], 0, 88);
            if (_position == _loopStart << 1)
            {
                _loopAdpcm = _adpcm;
                _loopIndex = _index;
            }
        }
        _sample = _adpcm;
    }
    private int Read(int offset)
    {
        return (uint)offset < (uint)_data.Length ? _data[offset] : 0;
    }
}
