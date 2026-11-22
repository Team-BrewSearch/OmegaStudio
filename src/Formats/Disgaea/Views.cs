using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed class PackView(IAssetNode inner, IHostServices host, PackReader read) : RecognizedAsset(inner)
{
    public override string Kind => AssetKinds.Container;
    public override bool HasChildren => true;
    public override IEnumerable<IAssetNode> GetChildren()
    {
        Pack? pack = Open();
        return pack is null ? [] : [.. Enumerable.Range(0, pack.Count).Select(i => Child(pack, i))];
    }
    private PartNode Child(Pack pack, int index)
    {
        return pack switch
        {
            Dseq sequence when index == 0 => new SequenceNode(this, sequence, host),
            Lz10Pack when pack.NameOf(index).EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) => new MapNode(this, pack, index, host),
            _ => new PartNode(this, pack, index, host),
        };
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        if (Open() is { } pack)
        {
            yield return PropertyRow.Of("Format", pack.Format);
            yield return PropertyRow.Of("Entries", pack.Count);
        }
        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
    private Pack? Open()
    {
        try
        {
            return read(Data, Name);
        }
        catch (InvalidDataException ex)
        {
            host.Log($"{Name} did not open: {ex.Message}");
            return null;
        }
    }
}
internal class PartNode : BinaryAssetNode
{
    private readonly PackView _owner;
    private readonly Pack _pack;
    private readonly int _index;
    private readonly IHostServices _host;
    public PartNode(PackView owner, Pack pack, int index, IHostServices host)
        : base(pack.NameOf(index), $"{owner.PathText}/{pack.NameOf(index)}", AssetKinds.Auto, Sizes.Describe(pack.Get(index).Length))
    {
        _owner = owner;
        _pack = pack;
        _index = index;
        _host = host;
    }
    public PackView Owner => _owner;
    public override ReadOnlySpan<byte> Data => _pack.Get(_index);
    public override string Extension => Path.GetExtension(Name) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    public override bool CanReplaceBytes => _owner.CanReplaceBytes;
    protected override void Write(byte[] payload)
    {
        _pack.Set(_index, payload);
        _host.Defer(_pack, () => _owner.ReplaceBytes(_pack.Build()));
    }
}
internal sealed class SequenceNode : PartNode, IStreamingAudioAsset, IDescribable
{
    private const int Loops = 2;
    private const int FadeSeconds = 10;
    private readonly Dseq _sequence;
    private readonly Lock _gate = new();
    private (int Revision, NitroSong Song, (long Total, long FadeStart) Length)? _measured;
    public SequenceNode(PackView owner, Dseq sequence, IHostServices host)
        : base(owner, sequence, 0, host)
    {
        _sequence = sequence;
        Kind = AssetKinds.Audio;
    }
    public ReadOnlySpan<byte> EncodedAudio => NitroSequencer.Render(_sequence.Get(0), _sequence.Get(1), _sequence.Get(2), Loops, FadeSeconds);
    public IAudioReader OpenAudio()
    {
        (int Revision, NitroSong Song, (long Total, long FadeStart) Length) measured;
        lock (_gate)
        {
            if (_measured is not { } cached || cached.Revision != _sequence.Revision)
            {
                int revision = _sequence.Revision;
                NitroSong song = NitroSong.Read(_sequence.Get(0), _sequence.Get(1), _sequence.Get(2));
                _measured = (revision, song, NitroSequenceReader.Measure(song, Loops, FadeSeconds));
            }
            measured = _measured.Value;
        }
        return new NitroSequenceReader(measured.Song, measured.Length, FadeSeconds * NitroSequencer.SampleRate);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Content", "SSEQ sequence, played through this file's SBNK and SWAR");
        yield return PropertyRow.Of("Output", $"{NitroSequencer.SampleRate} Hz stereo, {Loops} loops and a {FadeSeconds} s fade, rendered as it plays");
    }
}
internal sealed class ImyView(IAssetNode inner) : RecognizedAsset(inner), IImageAsset
{
    private CachedValue<ImageData>? _image;
    public override string Kind => AssetKinds.Texture;
    public ImageData Image => (_image ??= new CachedValue<ImageData>(() => Imy.Read(Data).ToImage(Name), image => image.Rgba.LongLength)).Value;
    public bool CanReplaceImage => CanReplaceBytes;
    public void ReplaceImage(ImageData image)
    {
        ReplaceBytes(Imy.Read(Data).Encode(image));
        _image?.Clear();
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        Imy imy = Imy.Read(Data);
        yield return PropertyRow.Of("Content", imy.Label);
        yield return PropertyRow.Of("Size", $"{imy.Width}x{imy.Height}");
        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
}
internal sealed class WaveView(IAssetNode inner) : RecognizedAsset(inner), IAudioAsset
{
    private CachedValue<NitroWave>? _wave;
    public override string Kind => AssetKinds.Audio;
    private NitroWave Wave => (_wave ??= new CachedValue<NitroWave>(() => NitroAudio.Decode(Data), wave => wave.Wav.LongLength)).Value;
    public ReadOnlySpan<byte> EncodedAudio => Wave.Wav;
    public override IEnumerable<AssetCommand> GetCommands()
    {
        foreach (AssetCommand command in base.GetCommands())
        {
            yield return command;
        }
        if (!CanReplaceBytes)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Replace sound from WAV",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("WAV audio", "*.wav")],
            Execute = context =>
            {
                byte[] rebuilt = NitroImport.Encode(Data, File.ReadAllBytes(context.Path!));
                bool same = rebuilt.AsSpan().SequenceEqual(Data);
                ReplaceBytes(rebuilt);
                _wave?.Clear();
                context.Log(same
                    ? $"{Path.GetFileName(context.Path!)} matches {Name} exactly; its bytes are unchanged."
                    : $"Encoded {Path.GetFileName(context.Path!)} into {Name} ({Sizes.Describe(rebuilt.Length)}). Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Bytes | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        NitroWave wave = Wave;
        yield return PropertyRow.Of("Content", $"{wave.Format} {wave.Encoding}");
        yield return PropertyRow.Of("Channels", wave.Channels);
        yield return PropertyRow.Of("Sample rate", $"{wave.SampleRate} Hz");
        yield return PropertyRow.Of("Loops", wave.Loops);
        foreach (PropertyRow row in base.Describe())
        {
            yield return row;
        }
    }
}
