using System.Globalization;
using DWSSK.Formats.Audio;
using DWSSK.Formats.Core;
using Omega.Plugins;

namespace DWSSK.Formats.Nodes;

internal sealed class WemNode : SlotNode, IAudioAsset
{
    private (int Revision, byte[] Audio)? _audio;

    public WemNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Audio, Label(slot))
    {
    }

    private static string Label(Slot slot)
    {
        if (!slot.Loaded)
        {
            return Sizes.Describe(slot.Size);
        }
        try
        {
            WemInfo i = Wem.Info(slot.Data);
            return $"{Wem.CodecName(i)}, {i.Rate} Hz, {i.Channels} ch";
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            return Sizes.Describe(slot.Data.Length);
        }
    }

    protected override string DescribeSize()
    {
        return Label(Slot);
    }

    public ReadOnlySpan<byte> EncodedAudio
    {
        get
        {
            if (_audio is not { } c || c.Revision != Slot.Revision)
            {
                byte[] b = Slot.Data;
                _audio = c = (Slot.Revision, Wem.Info(b).Tag == 0xFFFF ? Vorbis.ToOgg(b) : Wav(b));
            }
            return c.Audio;
        }
    }

    private static byte[] Wav(byte[] wem)
    {
        (short[] samples, int rate, int nch) = Wem.Decode(wem);
        return Wem.ToWav(samples, rate, nch);
    }

    protected override void Check(byte[] payload)
    {
        _ = Wem.Info(payload);
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        WemInfo i = Wem.Info(Slot.Data);
        yield return PropertyRow.Of("Codec", Wem.CodecName(i));
        yield return PropertyRow.Of("Sample rate", $"{i.Rate} Hz");
        yield return PropertyRow.Of("Channels", i.Channels);
        yield return PropertyRow.Of("Byte order", i.Big ? "big-endian (RIFX)" : "little-endian (RIFF)");
        yield return PropertyRow.Of("Format tag", $"0x{i.Tag:X4}");
        yield return PropertyRow.Of("Block align", i.Align);
        yield return PropertyRow.Of("Audio data", Sizes.Describe(i.Data.Length));
        yield return PropertyRow.Of("Chunks", string.Join(", ", i.Chunks.Keys.Select(k => k.TrimEnd())));
        yield return PropertyRow.Of("Plays as", i.Tag switch
        {
            0xFFFF => "Ogg Vorbis, rebuilt from the Wwise packets",
            0x0166 => "WAV, decoded from XMA2",
            _ => "WAV",
        });
        yield return PropertyRow.Of("Replace from WAV", i.Tag switch
        {
            0xFFFF => "re-encoded as Wwise Vorbis with this sound's own codebooks",
            0x0166 => "re-encoded as XMA2 at 24, 32, 44.1 or 48 kHz",
            _ => $"re-encoded as {Wem.CodecName(i)}",
        });
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }

    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Export WAV",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("WAV audio", "*.wav")],
            SuggestedFileName = FileStem + ".wav",
            Execute = context =>
            {
                byte[] wav = Wav(Slot.Data);
                AtomicSave.Write(context.Path!, wav);
                context.Log($"Decoded {Slot.Name} to {Path.GetFileName(context.Path!)} ({Sizes.Describe(wav.Length)}).");
            },
        };
        if (!Slot.CanWrite)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Replace sound from WAV",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("WAV audio (16-bit PCM)", "*.wav")],
            Execute = context =>
            {
                (short[] samples, int rate, int nch) = Wem.ReadWav(File.ReadAllBytes(context.Path!));
                byte[] wem = Wem.Encode(Slot.Data, samples, rate, nch);
                Store(wem);
                context.Log($"Encoded {Path.GetFileName(context.Path!)} into {Slot.Name} as {Wem.CodecName(Wem.Info(wem))} "
                    + $"({(samples.Length / Math.Max(1, nch) / (double)rate).ToString("0.00", CultureInfo.InvariantCulture)} s, {Sizes.Describe(wem.Length)}). Use File ▸ Save to write it back.");
                context.Changed = Everything;
            },
        };
    }
}
