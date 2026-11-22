namespace Disgaea.Formats;
internal sealed partial class NitroSequencer
{
    private const int MaxCall = 3;
    private enum Value
    {
        U8,
        U16,
        Vlv,
        Variable,
        Random,
    }
    private sealed class Track(NitroSequencer player, int id)
    {
        private readonly int[] _calls = new int[MaxCall];
        private readonly byte[] _loopCounts = new byte[MaxCall];
        private readonly List<Voice> _voices = [];
        private readonly Lfo _modulation = new();
        private int _depth;
        private bool _noteWait;
        private bool _tie;
        private bool _finishWait;
        private bool _portamento;
        private bool _compare;
        private int _panRange;
        private int _program;
        private int _volume;
        private int _expression;
        private int _pitchBend;
        private int _bendRange;
        private int _pan;
        private int _attack;
        private int _decay;
        private int _sustain;
        private int _release;
        private int _priority;
        private int _transpose;
        private int _portamentoKey;
        private int _portamentoTime;
        private int _sweepPitch;
        private int _wait;
        public int Id { get; } = id;
        public bool Allocated { get; set; }
        public int Position { get; set; } = -1;
        public int Loops { get; private set; }
        public void Init()
        {
            Position = -1;
            Loops = 0;
            _noteWait = _compare = true;
            _tie = _finishWait = _portamento = false;
            _depth = _portamentoTime = 0;
            _program = 0;
            _priority = 64;
            _volume = _expression = _panRange = 127;
            _pan = _pitchBend = _transpose = 0;
            _attack = _decay = _sustain = _release = 255;
            _bendRange = 2;
            _portamentoKey = 60;
            _sweepPitch = 0;
            _modulation.Reset();
            _wait = 0;
            _voices.Clear();
        }
        public void Start(int offset)
        {
            Position = offset;
        }
        public void Stop()
        {
            Position = -1;
            ReleaseVoices(-1);
            FreeVoices();
        }
        public void Detach(Voice voice, bool free)
        {
            if (free)
            {
                voice.Priority = 0;
                voice.Free();
            }
            _ = _voices.Remove(voice);
        }
        public void UpdateVoices(bool release)
        {
            int volume = ConvertSustain(_volume) + ConvertSustain(_expression) + ConvertSustain(player._volume);
            int pitch = (_pitchBend * (_bendRange << 6)) >> 7;
            int pan = _pan;
            if (_panRange != 127)
            {
                pan = ((pan * _panRange) + 0x40) >> 7;
            }
            volume = Math.Max(volume, -0x8000);
            pan = Math.Clamp(pan, -128, 127);
            foreach (Voice voice in _voices)
            {
                if (voice.State == 3)
                {
                    continue;
                }
                voice.UserDecay = volume;
                voice.UserPitch = pitch;
                voice.UserPan = pan;
                voice.PanRange = _panRange;
                _modulation.CopyTo(voice.Modulation);
                if (voice.Length == 0 && release)
                {
                    voice.Priority = 1;
                    voice.Release();
                }
            }
        }
        public bool Step()
        {
            foreach (Voice voice in _voices)
            {
                if (voice.Length > 0)
                {
                    voice.Length--;
                }
                if (!voice.AutoSweep && voice.SweepCounter < voice.SweepLength)
                {
                    voice.SweepCounter++;
                }
            }
            if (_finishWait)
            {
                if (_voices.Count != 0)
                {
                    return true;
                }
                _finishWait = false;
            }
            if (_wait > 0 && --_wait > 0)
            {
                return true;
            }
            while (_wait == 0 && !_finishWait)
            {
                bool run = true;
                Value? kind = null;
                int command = ReadU8();
                if (command == 0xA2)
                {
                    command = ReadU8();
                    run = _compare;
                }
                if (command == 0xA0)
                {
                    command = ReadU8();
                    kind = Value.Random;
                }
                if (command == 0xA1)
                {
                    command = ReadU8();
                    kind = Value.Variable;
                }
                if ((command & 0x80) == 0)
                {
                    int velocity = ReadU8();
                    int length = Parse(kind ?? Value.Vlv);
                    if (!run)
                    {
                        continue;
                    }
                    int key = Math.Clamp(command + _transpose, 0, 127);
                    PlayNote(key, velocity, length > 0 ? length : -1);
                    _portamentoKey = key;
                    if (_noteWait)
                    {
                        _wait = length;
                        _finishWait = length == 0;
                    }
                    continue;
                }
                switch (command & 0xF0)
                {
                    case 0x80:
                        {
                            int value = Parse(kind ?? Value.Vlv);
                            if (run && command == 0x80)
                            {
                                _wait = value;
                            }
                            else if (run && command == 0x81 && value < 0x10000)
                            {
                                _program = value;
                            }
                            break;
                        }
                    case 0x90:
                        Jump(command, run);
                        break;
                    case 0xC0:
                    case 0xD0:
                        {
                            byte value = (byte)Parse(kind ?? Value.U8);
                            if (run)
                            {
                                Control(command, value);
                            }
                            break;
                        }
                    case 0xE0:
                        {
                            short value = (short)Parse(kind ?? Value.U16);
                            if (run)
                            {
                                switch (command)
                                {
                                    case 0xE3:
                                        _sweepPitch = value;
                                        break;
                                    case 0xE1:
                                        player._tempo = (ushort)value;
                                        break;
                                    case 0xE0:
                                        _modulation.Delay = (ushort)value;
                                        break;
                                }
                            }
                            break;
                        }
                    case 0xB0:
                        {
                            int slot = ReadU8();
                            short value = (short)Parse(kind ?? Value.U16);
                            if (run)
                            {
                                Variable(command, slot, value);
                            }
                            break;
                        }
                    case 0xF0:
                        if (!run)
                        {
                            break;
                        }
                        if (command == 0xFD && _depth != 0)
                        {
                            Position = _calls[--_depth];
                        }
                        else if (command == 0xFC && _depth != 0)
                        {
                            byte count = _loopCounts[_depth - 1];
                            if (count != 0 && --count == 0)
                            {
                                _depth--;
                                break;
                            }
                            if (count == 0)
                            {
                                Loops++;
                            }
                            _loopCounts[_depth - 1] = count;
                            Position = _calls[_depth - 1];
                        }
                        else if (command == 0xFF)
                        {
                            return false;
                        }
                        break;
                }
            }
            return true;
        }
        private void Jump(int command, bool run)
        {
            switch (command)
            {
                case 0x93:
                    {
                        int index = ReadU8();
                        int offset = ReadU24();
                        if (run && player.GetTrack(index) is { } other && other != this)
                        {
                            other.Stop();
                            other.Start(offset);
                        }
                        break;
                    }
                case 0x94:
                    {
                        int offset = ReadU24();
                        if (run)
                        {
                            Position = offset;
                            Loops++;
                        }
                        break;
                    }
                case 0x95:
                    {
                        int offset = ReadU24();
                        if (run && _depth < MaxCall)
                        {
                            _calls[_depth++] = Position;
                            Position = offset;
                        }
                        break;
                    }
            }
        }
        private void Control(int command, byte value)
        {
            switch (command)
            {
                case 0xC1:
                    _volume = value;
                    break;
                case 0xD5:
                    _expression = value;
                    break;
                case 0xC2:
                    player._volume = value;
                    break;
                case 0xC5:
                    _bendRange = value;
                    break;
                case 0xC6:
                    _priority = value;
                    break;
                case 0xC7:
                    _noteWait = value != 0;
                    break;
                case 0xCF:
                    _portamentoTime = value;
                    break;
                case 0xCA:
                    _modulation.Depth = value;
                    break;
                case 0xCB:
                    _modulation.Speed = value;
                    break;
                case 0xCC:
                    _modulation.Target = value;
                    break;
                case 0xCD:
                    _modulation.Range = value;
                    break;
                case 0xD0:
                    _attack = value;
                    break;
                case 0xD1:
                    _decay = value;
                    break;
                case 0xD2:
                    _sustain = value;
                    break;
                case 0xD3:
                    _release = value;
                    break;
                case 0xD4:
                    if (_depth < MaxCall)
                    {
                        _calls[_depth] = Position;
                        _loopCounts[_depth++] = value;
                    }
                    break;
                case 0xC8:
                    _tie = value != 0;
                    ReleaseVoices(-1);
                    FreeVoices();
                    break;
                case 0xC9:
                    _portamentoKey = (byte)(value + _transpose);
                    _portamento = true;
                    break;
                case 0xCE:
                    _portamento = value != 0;
                    break;
                case 0xC3:
                    _transpose = (sbyte)value;
                    break;
                case 0xC4:
                    _pitchBend = (sbyte)value;
                    break;
                case 0xC0:
                    _pan = value - 0x40;
                    break;
            }
        }
        private void Variable(int command, int slot, short value)
        {
            if (slot >= player._variables.Length)
            {
                throw new InvalidDataException($"variable {slot} does not exist");
            }
            short current = player._variables[slot];
            switch (command)
            {
                case 0xB0:
                    current = value;
                    break;
                case 0xB1:
                    current += value;
                    break;
                case 0xB2:
                    current -= value;
                    break;
                case 0xB3:
                    current *= value;
                    break;
                case 0xB4:
                    if (value != 0)
                    {
                        current /= value;
                    }
                    break;
                case 0xB5:
                    current = value >= 0 ? (short)(current << value) : (short)(current >> -value);
                    break;
                case 0xB6:
                    {
                        bool negative = value < 0;
                        int range = negative ? -value : value;
                        int random = (player.Random() * (range + 1)) >> 16;
                        current = (short)(negative ? -random : random);
                        break;
                    }
                case 0xB8:
                    _compare = current == value;
                    break;
                case 0xB9:
                    _compare = current >= value;
                    break;
                case 0xBA:
                    _compare = current > value;
                    break;
                case 0xBB:
                    _compare = current <= value;
                    break;
                case 0xBC:
                    _compare = current < value;
                    break;
                case 0xBD:
                    _compare = current != value;
                    break;
            }
            player._variables[slot] = current;
        }
        private void PlayNote(int key, int velocity, int length)
        {
            Voice? voice = null;
            if (_tie && _voices.Count != 0)
            {
                voice = _voices[0];
                voice.Key = key;
                voice.Velocity = velocity;
            }
            if (voice is null)
            {
                if (player._bank.Find(_program, key) is not { } note)
                {
                    return;
                }
                int mask = note.Type switch
                {
                    1 => 0xFFFF,
                    2 => 0x3F00,
                    3 => 0xC000,
                    _ => 0,
                };
                if (mask == 0)
                {
                    return;
                }
                voice = player.AllocateVoice(mask, 64 + _priority, this);
                if (voice is null)
                {
                    return;
                }
                if (!voice.NoteOn(key, velocity, _tie ? -1 : length, note))
                {
                    voice.Priority = 0;
                    voice.Free();
                    return;
                }
                _voices.Insert(0, voice);
            }
            if (_attack != 0xFF)
            {
                voice.SetAttack(_attack);
            }
            if (_decay != 0xFF)
            {
                voice.SetDecay(_decay);
            }
            if (_sustain != 0xFF)
            {
                voice.SetSustain(_sustain);
            }
            if (_release != 0xFF)
            {
                voice.SetRelease(_release);
            }
            voice.SweepPitch = _sweepPitch;
            if (_portamento)
            {
                voice.SweepPitch = (short)(voice.SweepPitch + ((_portamentoKey - key) << 6));
            }
            if (_portamentoTime != 0)
            {
                int sweep = _portamentoTime * _portamentoTime * Math.Abs(voice.SweepPitch);
                voice.SweepLength = sweep >> 11;
            }
            else
            {
                voice.SweepLength = length;
                voice.AutoSweep = false;
            }
            voice.SweepCounter = 0;
        }
        private void ReleaseVoices(int release)
        {
            UpdateVoices(false);
            foreach (Voice voice in _voices)
            {
                if (!voice.Active)
                {
                    continue;
                }
                if (release >= 0)
                {
                    voice.SetRelease(release & 0xFF);
                }
                voice.Priority = 1;
                voice.Release();
            }
        }
        private void FreeVoices()
        {
            foreach (Voice voice in _voices)
            {
                voice.Free();
            }
            _voices.Clear();
        }
        private int Parse(Value kind)
        {
            switch (kind)
            {
                case Value.U8:
                    return ReadU8();
                case Value.U16:
                    return ReadU16();
                case Value.Vlv:
                    {
                        int value = 0;
                        int b;
                        do
                        {
                            b = ReadU8();
                            value = (value << 7) | (b & 0x7F);
                        }
                        while ((b & 0x80) != 0);
                        return value;
                    }
                case Value.Variable:
                    {
                        int slot = ReadU8();
                        return slot < player._variables.Length ? player._variables[slot] : throw new InvalidDataException($"variable {slot} does not exist");
                    }
                default:
                    {
                        int low = (short)ReadU16();
                        int high = (short)ReadU16();
                        return ((player.Random() * (high - low + 1)) >> 16) + low;
                    }
            }
        }
        public int ReadU8()
        {
            if ((uint)Position >= (uint)player._sequence.Length)
            {
                throw new InvalidDataException($"track {Id} reads past the end of the sequence at 0x{Position:X}");
            }
            return player._sequence[Position++];
        }
        public int ReadU16()
        {
            return ReadU8() | (ReadU8() << 8);
        }
        private int ReadU24()
        {
            return ReadU8() | (ReadU8() << 8) | (ReadU8() << 16);
        }
    }
}
