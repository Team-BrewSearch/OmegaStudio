namespace Disgaea.Formats;
internal sealed partial class NitroSequencer
{
    private const int SyncStopFlag = 1;
    private const int SyncStartFlag = 2;
    private const int SyncTimerFlag = 4;
    private const int SyncVolumeFlag = 8;
    private const int SyncPanFlag = 16;
    private const int PsgNoiseTimer = 8006;
    private static readonly int[] s_volumeShift = [0, 1, 2, 4];
    private sealed class Voice(NitroSequencer player, int id)
    {
        private int _kind;
        private int _sync;
        private int _root = 60;
        private int _initialPan;
        private int _attenuation;
        private int _attack;
        private int _sustain;
        private int _decay;
        private int _release;
        private int _pan;
        private ushort _volume;
        private ushort _timer;
        private NitroWaveData? _wave;
        private int _duty;
        private int _waveTimer;
        private bool _startPending;
        public int Id { get; } = id;
        public bool Active { get; private set; }
        public bool AutoSweep { get; set; }
        public int State { get; set; }
        public int PanRange { get; set; } = 127;
        public int Key { get; set; } = 60;
        public int Velocity { get; set; } = 127;
        public int UserPan { get; set; }
        public int UserDecay { get; set; }
        public int UserPitch { get; set; }
        public int SweepCounter { get; set; }
        public int SweepLength { get; set; }
        public int SweepPitch { get; set; }
        public int Priority { get; set; }
        public int Length { get; set; }
        public Lfo Modulation { get; } = new();
        public Track? Owner { get; private set; }
        public void SyncStop()
        {
            _sync = SyncStopFlag;
            Active = false;
        }
        public void Sync(NitroSpuChannel hardware)
        {
            if (_sync == 0)
            {
                return;
            }
            if ((_sync & SyncStopFlag) != 0)
            {
                hardware.Stop();
            }
            if ((_sync & SyncStartFlag) != 0)
            {
                hardware.SetPan(_pan);
                hardware.SetVolume(_volume & 0xFF, _volume >> 8);
                hardware.SetTimer((ushort)(0x10000 - _timer));
                switch (_kind)
                {
                    case 0:
                        hardware.StartWave(_wave!);
                        break;
                    case 1:
                        hardware.StartPsg(_duty);
                        break;
                    default:
                        hardware.StartNoise();
                        break;
                }
                _sync = 0;
                return;
            }
            if ((_sync & SyncTimerFlag) != 0)
            {
                hardware.SetTimer((ushort)(0x10000 - _timer));
            }
            if ((_sync & SyncVolumeFlag) != 0)
            {
                hardware.SetVolume(_volume & 0xFF, _volume >> 8);
            }
            if ((_sync & SyncPanFlag) != 0)
            {
                hardware.SetPan(_pan);
            }
            _sync = 0;
        }
        public void Main(NitroSpuChannel hardware)
        {
            if (!Active)
            {
                return;
            }
            if (_startPending)
            {
                _sync |= SyncStartFlag;
                _startPending = false;
            }
            else if (!hardware.Enabled)
            {
                Kill();
                return;
            }
            int volume = ConvertSustain(Velocity) + UpdateEnvelope() + UserDecay;
            int pitch = ((Key - _root) * 0x40) + UpdateSweep() + UserPitch;
            int lfo = UpdateLfo();
            int pan = 0;
            switch (Modulation.Target)
            {
                case 1:
                    if (volume > -0x8000)
                    {
                        volume += lfo;
                    }
                    break;
                case 2:
                    pan += lfo;
                    break;
                default:
                    pitch += lfo;
                    break;
            }
            pan += _initialPan;
            if (PanRange != 127)
            {
                pan = ((pan * PanRange) + 0x40) >> 7;
            }
            pan += UserPan;
            if (State == 3 && volume <= Floor)
            {
                _sync = SyncStopFlag;
                Kill();
                return;
            }
            ushort newVolume = ChannelVolume(volume);
            ushort newTimer = ChannelTimer(_waveTimer, pitch);
            if (_kind == 1)
            {
                newTimer &= 0xFFFC;
            }
            pan = Math.Clamp(pan + 0x40, 0, 127);
            if (newVolume != _volume)
            {
                _volume = newVolume;
                _sync |= SyncVolumeFlag;
            }
            if (newTimer != _timer)
            {
                _timer = newTimer;
                _sync |= SyncTimerFlag;
            }
            if (pan != _pan)
            {
                _pan = pan;
                _sync |= SyncPanFlag;
            }
        }
        public void Setup(Track owner, int priority)
        {
            Owner = owner;
            Length = SweepLength = SweepCounter = 0;
            Priority = priority;
            _volume = 127;
            _startPending = false;
            AutoSweep = true;
            Key = _root = 60;
            Velocity = PanRange = 127;
            _initialPan = UserPan = 0;
            UserDecay = UserPitch = SweepPitch = 0;
            SetAttack(127);
            SetSustain(127);
            SetDecay(127);
            SetRelease(127);
            Modulation.Reset();
        }
        public bool NoteOn(int key, int velocity, int length, NitroNote note)
        {
            int release = note.Release;
            if (release == 0xFF)
            {
                length = -1;
                release = 0;
            }
            switch (note.Type)
            {
                case 1:
                    if (note.Archive != 0 || note.Wave >= player._waves.Count || player._waves[note.Wave] is not { } wave)
                    {
                        return false;
                    }
                    _kind = 0;
                    _wave = wave;
                    _waveTimer = wave.Timer;
                    break;
                case 2:
                    if (Id is < 8 or > 13)
                    {
                        return false;
                    }
                    _kind = 1;
                    _duty = note.Wave;
                    _waveTimer = PsgNoiseTimer;
                    break;
                case 3:
                    if (Id < 14)
                    {
                        return false;
                    }
                    _kind = 2;
                    _waveTimer = PsgNoiseTimer;
                    break;
                default:
                    return false;
            }
            _attenuation = Silence;
            State = 0;
            Length = length;
            Modulation.DelayCounter = Modulation.Counter = 0;
            _startPending = true;
            Active = true;
            Key = key;
            _root = note.Key;
            Velocity = velocity;
            SetAttack(note.Attack);
            SetSustain(note.Sustain);
            SetDecay(note.Decay);
            SetRelease(release);
            _initialPan = note.Pan - 0x40;
            return true;
        }
        public void SetAttack(int attack)
        {
            _attack = attack < 109 ? 255 - attack : s_attack[127 - attack];
        }
        public void SetDecay(int decay)
        {
            _decay = DecayCoefficient(decay);
        }
        public void SetSustain(int sustain)
        {
            _sustain = sustain;
        }
        public void SetRelease(int release)
        {
            _release = DecayCoefficient(release);
        }
        public void Release()
        {
            State = 3;
        }
        public void Free()
        {
            Owner = null;
        }
        public void Kill()
        {
            if (Owner is null)
            {
                Priority = 0;
            }
            else
            {
                Owner.Detach(this, true);
            }
            _volume = 0;
            Active = false;
        }
        public int CompareVolume(Voice other)
        {
            int mine = ((_volume & 0xFF) << 4) >> s_volumeShift[_volume >> 8];
            int theirs = ((other._volume & 0xFF) << 4) >> s_volumeShift[other._volume >> 8];
            return mine == theirs ? 0 : mine < theirs ? 1 : -1;
        }
        private int UpdateEnvelope()
        {
            switch (State)
            {
                case 0:
                    _attenuation = -((-_attenuation * _attack) >> 8);
                    if (_attenuation == 0)
                    {
                        State = 1;
                    }
                    break;
                case 1:
                    int sustain = ConvertSustain(_sustain) << 7;
                    _attenuation -= _decay;
                    if (_attenuation <= sustain)
                    {
                        _attenuation = sustain;
                        State = 2;
                    }
                    break;
                case 3:
                    _attenuation -= _release;
                    break;
            }
            return _attenuation >> 7;
        }
        private int UpdateSweep()
        {
            if (SweepPitch == 0 || SweepCounter >= SweepLength)
            {
                return 0;
            }
            long result = (long)SweepPitch * (SweepLength - SweepCounter) / SweepLength;
            if (AutoSweep)
            {
                SweepCounter++;
            }
            return (int)result;
        }
        private int UpdateLfo()
        {
            long result = Modulation.Value();
            if (result != 0)
            {
                result = Modulation.Target == 1 ? result * 60 : result << 6;
                result >>= 14;
            }
            Modulation.Update();
            return (int)result;
        }
    }
}
