using Omega.Plugins;
namespace Polarbit.Formats.Nodes;
internal sealed class PvrNode : EntryNode, IImageAsset
{
    public PvrNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Texture, Label(PvrTexture.Parse(entry.Payload)))
    {
    }
    private static string Label(PvrTexture texture)
    {
        return $"{texture.Width}x{texture.Height} {texture.FormatName}";
    }
    public ImageData Image => Context.Decode(Entry) ?? throw new InvalidDataException("The texture could not be decoded.");
    public bool CanReplaceImage => true;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] payload = Entry.Payload;
        Entry.Replace(PvrTexture.Parse(payload).Encode(payload, image.Rgba, image.Width, image.Height));
        Detail = Label(PvrTexture.Parse(Entry.Payload));
    }
    protected override void Check(byte[] payload)
    {
        _ = PvrTexture.Parse(payload);
    }
    protected override string DescribeSize()
    {
        return Label(PvrTexture.Parse(Entry.Payload));
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PvrTexture texture = PvrTexture.Parse(Entry.Payload);
        yield return PropertyRow.Of("Size", $"{texture.Width} x {texture.Height}");
        yield return PropertyRow.Of("Format", texture.FormatName);
        yield return PropertyRow.Of("Mip levels", texture.Levels.Length);
        yield return PropertyRow.Of("Flags", $"0x{texture.Flags:X}");
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class FontNode : EntryNode, IImageAsset
{
    public FontNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Texture, Label(PfntFont.Parse(entry.Payload)))
    {
    }
    private static string Label(PfntFont font)
    {
        return $"{font.Glyphs} glyphs, {font.Width}x{font.Height}";
    }
    public ImageData Image
    {
        get
        {
            PfntFont font = PfntFont.Parse(Entry.Payload);
            return ImageData.Create(FileStem, font.Width, font.Height, font.Decode(Entry.Payload), $"PFNT {font.Encoding}");
        }
    }
    public bool CanReplaceImage => true;
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] payload = Entry.Payload;
        Entry.Replace(PfntFont.Parse(payload).Encode(payload, image.Rgba, image.Width, image.Height));
    }
    protected override void Check(byte[] payload)
    {
        _ = PfntFont.Parse(payload);
    }
    protected override string DescribeSize()
    {
        return Label(PfntFont.Parse(Entry.Payload));
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PfntFont font = PfntFont.Parse(Entry.Payload);
        yield return PropertyRow.Of("Glyphs", font.Glyphs);
        yield return PropertyRow.Of("Characters", $"U+{font.First:X4} - U+{font.Last:X4}");
        yield return PropertyRow.Of("Line height", font.LineHeight);
        yield return PropertyRow.Of("Ascent", font.Ascent);
        yield return PropertyRow.Of("Atlas", $"{font.Width} x {font.Height}");
        yield return PropertyRow.Of("Pixels", font.Encoding);
        yield return PropertyRow.Of("Character set", font.Characters);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal static class Sounds
{
    public static AssetCommand ReplaceCommand(Func<byte[]> current, Action<byte[]> store)
    {
        return new AssetCommand
        {
            Title = "Replace sound from WAV",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("WAV audio", "*.wav")],
            Execute = context =>
            {
                byte[] psnd = current();
                PsndSound sound = PsndSound.Parse(psnd);
                byte[] rebuilt = sound.FromWav(psnd, File.ReadAllBytes(context.Path!));
                store(rebuilt);
                PsndSound now = PsndSound.Parse(rebuilt);
                context.Log($"Encoded {Path.GetFileName(context.Path!)} as {now.Codec}, {now.Rate} Hz, {now.Seconds(rebuilt):0.00} s. Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Bytes | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    public static IEnumerable<PropertyRow> Rows(byte[] psnd)
    {
        PsndSound sound = PsndSound.Parse(psnd);
        yield return PropertyRow.Of("Codec", sound.Codec);
        yield return PropertyRow.Of("Sample rate", $"{sound.Rate} Hz");
        yield return PropertyRow.Of("Channels", sound.Channels);
        yield return PropertyRow.Of("Duration", $"{sound.Seconds(psnd):0.000} s");
        if (sound.Loop is { } loop)
        {
            yield return PropertyRow.Of("Loop", $"{loop.Start} - {loop.End}{(sound.PingPong ? " (ping-pong)" : string.Empty)}");
        }
        yield return PropertyRow.Of("Header flags", $"0x{sound.Flags:X3}");
    }
}
internal sealed class SoundNode : EntryNode, IAudioAsset, ICommandSource
{
    private (int Revision, byte[] Wav)? _wav;
    public SoundNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Audio, Label(entry.Payload))
    {
    }
    private static string Label(byte[] psnd)
    {
        PsndSound sound = PsndSound.Parse(psnd);
        return $"{sound.Seconds(psnd):0.0} s {sound.Codec}";
    }
    public ReadOnlySpan<byte> EncodedAudio
    {
        get
        {
            if (_wav is not { } cached || cached.Revision != Context.Archive.Revision)
            {
                _wav = cached = (Context.Archive.Revision, PsndSound.Parse(Entry.Payload).ToWav(Entry.Payload));
            }
            return cached.Wav;
        }
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return Sounds.ReplaceCommand(() => Entry.Payload, payload =>
        {
            Entry.Replace(payload);
            Detail = Label(payload);
        });
    }
    protected override void Check(byte[] payload)
    {
        _ = PsndSound.Parse(payload);
    }
    protected override string DescribeSize()
    {
        return Label(Entry.Payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        return Sounds.Rows(Entry.Payload).Concat(EntryRows());
    }
}
internal sealed class ModuleNode : EntryNode
{
    public ModuleNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, AssetKinds.Audio, $"{PmodModule.Parse(entry.Payload).Samples.Count} samples")
    {
        ChildFactory = () =>
        {
            PmodModule module = PmodModule.Parse(Entry.Payload);
            return Enumerable.Range(0, module.Samples.Count).Select(i => (IAssetNode)new SampleNode(Entry, i, $"{PathText}/sample{i:00}"));
        };
    }
    public override string Extension => ".pmod";
    protected override void Check(byte[] payload)
    {
        _ = PmodModule.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PmodModule module = PmodModule.Parse(Entry.Payload);
        yield return PropertyRow.Of("Speed", module.Speed);
        yield return PropertyRow.Of("Tempo", $"{module.Bpm} BPM");
        yield return PropertyRow.Of("Song length", $"{module.SongLength} orders");
        yield return PropertyRow.Of("Patterns", module.Patterns);
        yield return PropertyRow.Of("Channels", module.Channels);
        yield return PropertyRow.Of("Samples", module.Samples.Count);
        yield return PropertyRow.Of("Instruments", module.Instruments);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class SampleNode(VfsEntry module, int index, string path)
    : BinaryAssetNode($"sample{index:00}", path, AssetKinds.Audio, $"sample {index}"), IAudioAsset, ICommandSource, IDescribable
{
    private byte[]? _sample;
    private byte[] Sample => _sample ??= PmodModule.Parse(module.Payload).Sample(index).ToArray();
    public override ReadOnlySpan<byte> Data => Sample;
    public override string Extension => ".psn";
    public override string FileStem => $"{Path.GetFileNameWithoutExtension(module.Name ?? $"{module.Hash:x8}")}_sample{index:00}";
    public ReadOnlySpan<byte> EncodedAudio
    {
        get
        {
            byte[] sample = Sample;
            return PsndSound.Parse(sample).ToWav(sample);
        }
    }
    protected override void Write(byte[] payload)
    {
        _ = PsndSound.Parse(payload);
        byte[] current = module.Payload;
        module.Replace(PmodModule.Parse(current).WithSample(current, index, payload));
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return Sounds.ReplaceCommand(() => Sample, ReplaceBytes);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Module", module.Name ?? $"{module.Hash:X8}");
        yield return PropertyRow.Of("Sample", index);
        foreach (PropertyRow row in Sounds.Rows(Sample))
        {
            yield return row;
        }
    }
}
