using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed class NitroSong
{
    private NitroSong(byte[] sequence, NitroBank bank, NitroWaveData?[] waves)
    {
        Sequence = sequence;
        Bank = bank;
        Waves = waves;
    }
    public byte[] Sequence { get; }
    public NitroBank Bank { get; }
    public IReadOnlyList<NitroWaveData?> Waves { get; }
    public static NitroSong Read(ReadOnlySpan<byte> sseq, ReadOnlySpan<byte> sbnk, ReadOnlySpan<byte> swar)
    {
        BinaryRead.Need(sseq, 0, 0x1C);
        if (!sseq.StartsWith("SSEQ"u8) || !sseq.Slice(0x10, 4).SequenceEqual("DATA"u8))
        {
            throw new InvalidDataException("not an SSEQ sequence");
        }
        uint start = BinaryRead.U32(sseq, 0x18);
        if (start > sseq.Length)
        {
            throw new InvalidDataException("the SSEQ data offset lies outside the file");
        }
        Swar archive = Swar.Read(swar, "bank");
        NitroWaveData?[] waves = new NitroWaveData?[archive.Count];
        for (int i = 0; i < waves.Length; i++)
        {
            waves[i] = new NitroWaveData(archive.Get(i)[0x18..]);
        }
        return new NitroSong(sseq[(int)start..].ToArray(), NitroBank.Read(sbnk), waves);
    }
}
internal sealed class NitroSequenceReader : IAudioReader
{
    private const double MaxSeconds = 1200;
    private readonly NitroSong _song;
    private readonly long _fadeStart;
    private readonly long _fadeLength;
    private NitroSequencer _player;
    private long _position;
    public NitroSequenceReader(NitroSong song, int loops, double fadeSeconds)
        : this(song, Measure(song, loops, fadeSeconds), (long)(fadeSeconds * NitroSequencer.SampleRate))
    {
    }
    public NitroSequenceReader(NitroSong song, (long Total, long FadeStart) length, long fadeLength)
    {
        ArgumentNullException.ThrowIfNull(song);
        _song = song;
        TotalFrames = length.Total;
        _fadeStart = length.FadeStart;
        _fadeLength = fadeLength;
        _player = new NitroSequencer(song);
    }
    public int SampleRate => NitroSequencer.SampleRate;
    public int Channels => 2;
    public long TotalFrames { get; }
    public static (long Total, long FadeStart) Measure(NitroSong song, int loops, double fadeSeconds)
    {
        return new NitroSequencer(song).Measure(loops, (long)(fadeSeconds * NitroSequencer.SampleRate), (long)(MaxSeconds * NitroSequencer.SampleRate));
    }
    public int Read(Span<short> interleaved)
    {
        int frames = (int)Math.Min(interleaved.Length / 2, TotalFrames - _position);
        if (frames <= 0)
        {
            return 0;
        }
        Span<short> output = interleaved[..(frames * 2)];
        _player.Render(output);
        if (_fadeStart >= 0)
        {
            for (int i = 0; i < frames; i++)
            {
                long into = _position + i - _fadeStart;
                if (into < 0)
                {
                    continue;
                }
                long remaining = _fadeLength - into;
                output[i * 2] = (short)(output[i * 2] * remaining / _fadeLength);
                output[(i * 2) + 1] = (short)(output[(i * 2) + 1] * remaining / _fadeLength);
            }
        }
        _position += frames;
        return frames;
    }
    public void Seek(long frame)
    {
        frame = Math.Clamp(frame, 0, TotalFrames);
        if (frame < _position)
        {
            _player = new NitroSequencer(_song);
            _position = 0;
        }
        _player.Skip(frame - _position);
        _position = frame;
    }
    public void Dispose()
    {
    }
}
