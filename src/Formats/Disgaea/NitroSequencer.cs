using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed partial class NitroSequencer
{
    public const int SampleRate = 32728;
    private const int CyclesPerSample = 1024;
    private const int CyclesPerTick = 64 * 2728;
    private const int Count = 16;
    private const int Floor = -723;
    private const int Silence = Floor * 128;
    private static readonly byte[] s_order = [4, 5, 6, 7, 2, 0, 3, 1, 8, 9, 10, 11, 14, 12, 15, 13];
    private readonly byte[] _sequence;
    private readonly NitroBank _bank;
    private readonly IReadOnlyList<NitroWaveData?> _waves;
    private readonly NitroSpuChannel[] _spu = [.. Enumerable.Range(0, Count).Select(_ => new NitroSpuChannel())];
    private readonly Voice[] _voices;
    private readonly Track[] _tracks;
    private readonly int[] _trackIds = new int[Count];
    private readonly short[] _variables = new short[32];
    private int _tempo = 120;
    private int _tempoCounter = 240;
    private int _volume = 127;
    private uint _random = 0x12345678;
    private long _cycles;
    public NitroSequencer(NitroSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        _sequence = song.Sequence;
        _bank = song.Bank;
        _waves = song.Waves;
        _voices = [.. Enumerable.Range(0, Count).Select(i => new Voice(this, i))];
        _tracks = [.. Enumerable.Range(0, Count).Select(i => new Track(this, i))];
        Array.Fill(_trackIds, -1);
        Array.Fill(_variables, (short)-1);
        Prepare();
        Tick();
    }
    public static byte[] Render(ReadOnlySpan<byte> sseq, ReadOnlySpan<byte> sbnk, ReadOnlySpan<byte> swar, int loops = 2, double fadeSeconds = 10)
    {
        using NitroSequenceReader stream = new(NitroSong.Read(sseq, sbnk, swar), loops, fadeSeconds);
        short[] samples = new short[stream.TotalFrames * 2];
        int done = 0;
        while (done < samples.Length)
        {
            int frames = stream.Read(samples.AsSpan(done));
            if (frames == 0)
            {
                break;
            }
            done += frames * 2;
        }
        return Wav(samples);
    }
    public void Render(Span<short> interleaved)
    {
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            long left = 0;
            long right = 0;
            foreach (NitroSpuChannel channel in _spu)
            {
                channel.Mix(ref left, ref right);
            }
            interleaved[i] = Output(left);
            interleaved[i + 1] = Output(right);
            Advance(1);
        }
    }
    public void Skip(long frames)
    {
        while (frames > 0)
        {
            long step = Math.Min(frames, FramesToTick());
            foreach (NitroSpuChannel channel in _spu)
            {
                channel.Skip(step);
            }
            frames -= step;
            Advance(step);
        }
        foreach (NitroSpuChannel channel in _spu)
        {
            channel.Catchup();
        }
    }
    public (long Total, long FadeStart) Measure(int loops, long fadeFrames, long maxFrames)
    {
        long frame = 0;
        long fadeStart = -1;
        while (frame < maxFrames)
        {
            long step = Math.Min(maxFrames - frame, FramesToTick());
            foreach (NitroSpuChannel channel in _spu)
            {
                channel.Skip(step);
            }
            frame += step;
            if (!Advance(step))
            {
                continue;
            }
            if (fadeStart < 0 && Looped(loops))
            {
                fadeStart = frame;
            }
            if (fadeStart >= 0 && frame >= fadeStart + fadeFrames)
            {
                return (fadeStart + fadeFrames, fadeStart);
            }
            if (Finished())
            {
                return (frame, fadeStart);
            }
        }
        return (maxFrames, fadeStart);
    }
    private long FramesToTick()
    {
        return (CyclesPerTick - _cycles + CyclesPerSample - 1) / CyclesPerSample;
    }
    private bool Advance(long frames)
    {
        _cycles += frames * CyclesPerSample;
        if (_cycles < CyclesPerTick)
        {
            return false;
        }
        _cycles -= CyclesPerTick;
        Tick();
        return true;
    }
    private static short Output(long mixed)
    {
        long value = ((mixed * 128) >> 7) >> 8;
        return (short)(Math.Clamp(value, short.MinValue, short.MaxValue) & 0xFFC0);
    }
    private bool Looped(int loops)
    {
        bool any = false;
        foreach (int id in _trackIds)
        {
            if (id < 0)
            {
                continue;
            }
            Track track = _tracks[id];
            if (track.Position >= 0 && track.Loops < loops)
            {
                return false;
            }
            any |= track.Loops >= loops;
        }
        return any;
    }
    private bool Finished()
    {
        return _trackIds.All(id => id < 0 || _tracks[id].Position < 0) && _spu.All(c => !c.Enabled) && _voices.All(v => !v.Active);
    }
    private void Prepare()
    {
        int first = AllocateTrack();
        Track main = _tracks[first];
        main.Init();
        main.Start(0);
        _trackIds[0] = first;
        if (main.ReadU8() == 0xFE)
        {
            int mask = main.ReadU16() >> 1;
            for (int track = 1; mask != 0; track++, mask >>= 1)
            {
                if ((mask & 1) == 0)
                {
                    continue;
                }
                int id = AllocateTrack();
                if (id < 0)
                {
                    break;
                }
                _tracks[id].Init();
                _trackIds[track] = id;
            }
        }
        else
        {
            main.Position--;
        }
    }
    private int AllocateTrack()
    {
        for (int i = 0; i < Count; i++)
        {
            if (!_tracks[i].Allocated)
            {
                _tracks[i].Allocated = true;
                return i;
            }
        }
        return -1;
    }
    private Track? GetTrack(int index)
    {
        return (uint)index < Count && _trackIds[index] >= 0 ? _tracks[_trackIds[index]] : null;
    }
    private void StopTrack(int index)
    {
        if (GetTrack(index) is { } track)
        {
            track.Stop();
            track.Allocated = false;
            _trackIds[index] = -1;
        }
    }
    private void Tick()
    {
        for (int i = 0; i < Count; i++)
        {
            _voices[i].Sync(_spu[i]);
        }
        int ticks = 0;
        while (_tempoCounter >= 240)
        {
            _tempoCounter -= 240;
            ticks++;
        }
        for (int t = 0; t < ticks; t++)
        {
            for (int i = 0; i < Count; i++)
            {
                if (GetTrack(i) is { Position: >= 0 } track && !track.Step())
                {
                    StopTrack(i);
                }
            }
        }
        _tempoCounter += (ushort)((_tempo * 256) >> 8);
        for (int i = 0; i < Count; i++)
        {
            GetTrack(i)?.UpdateVoices(true);
        }
        for (int i = 0; i < Count; i++)
        {
            _voices[i].Main(_spu[i]);
        }
    }
    private Voice? AllocateVoice(int mask, int priority, Track owner)
    {
        Voice? best = null;
        foreach (byte candidate in s_order)
        {
            if ((mask & (1 << candidate)) == 0)
            {
                continue;
            }
            Voice voice = _voices[candidate];
            if (best is null || (voice.Priority <= best.Priority && (voice.Priority != best.Priority || best.CompareVolume(voice) < 0)))
            {
                best = voice;
            }
        }
        if (best is null || priority < best.Priority)
        {
            return null;
        }
        best.Owner?.Detach(best, false);
        best.SyncStop();
        best.Setup(owner, priority);
        return best;
    }
    private ushort Random()
    {
        _random = (_random * 1664525) + 1013904223;
        return (ushort)(_random >> 16);
    }
    private static int ConvertSustain(int value)
    {
        return s_sustain[(value & 0x80) != 0 ? 0x7F : value];
    }
    private static ushort ChannelVolume(int value)
    {
        value = Math.Clamp(value, Floor, 0);
        int divider = value switch
        {
            < -240 => 3,
            < -120 => 2,
            < -60 => 1,
            _ => 0,
        };
        return (ushort)(s_volume[value - Floor] | (divider << 8));
    }
    private static ushort ChannelTimer(int timer, int pitch)
    {
        int octave = 0;
        int normalized = -pitch;
        while (normalized < 0)
        {
            octave--;
            normalized += 768;
        }
        while (normalized >= 768)
        {
            octave++;
            normalized -= 768;
        }
        ulong result = (s_pitch[normalized] + 0x10000UL) * (ulong)timer;
        int shift = octave - 16;
        if (shift <= 0)
        {
            result >>= -shift;
        }
        else if (shift < 32)
        {
            if ((result & (~0UL << (32 - shift))) != 0)
            {
                return 0xFFFF;
            }
            result <<= shift;
        }
        else
        {
            return 0xFFFF;
        }
        return (ushort)Math.Clamp(result, 0x10UL, 0xFFFFUL);
    }
    private static ushort DecayCoefficient(int value)
    {
        if ((value & 0x80) != 0)
        {
            value = 0;
        }
        return value switch
        {
            127 => 0xFFFF,
            126 => 0x3C00,
            < 50 => (ushort)((value * 2) + 1),
            _ => (ushort)(0x1E00 / (126 - value)),
        };
    }
    private static int Sine(int x)
    {
        return x switch
        {
            < 0x20 => s_sine[x],
            < 0x40 => s_sine[0x40 - x],
            < 0x60 => -s_sine[x - 0x40],
            _ => -s_sine[0x20 - (x - 0x60)],
        };
    }
    private static byte[] Wav(short[] samples)
    {
        byte[] wav = new byte[44 + (samples.Length * 2)];
        Span<byte> span = wav;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 4);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], samples[i]);
        }
        return wav;
    }
    private sealed class Lfo
    {
        public int Target;
        public int Speed = 16;
        public int Depth;
        public int Range = 1;
        public int Delay;
        public int DelayCounter;
        public int Counter;
        public void CopyTo(Lfo other)
        {
            other.Target = Target;
            other.Speed = Speed;
            other.Depth = Depth;
            other.Range = Range;
            other.Delay = Delay;
        }
        public void Reset()
        {
            Target = 0;
            Speed = 16;
            Depth = 0;
            Range = 1;
            Delay = 0;
        }
        public int Value()
        {
            return Depth == 0 || DelayCounter < Delay ? 0 : Sine((Counter & 0xFFFF) >> 8) * Depth * Range;
        }
        public void Update()
        {
            if (DelayCounter < Delay)
            {
                DelayCounter++;
                return;
            }
            int high = ((Counter & 0xFFFF) + (Speed << 6)) >> 8;
            while (high >= 0x80)
            {
                high -= 0x80;
            }
            Counter = (((Counter + (Speed << 6)) & 0xFF) | (high << 8)) & 0xFFFF;
        }
    }
}
