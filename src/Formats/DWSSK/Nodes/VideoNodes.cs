using DWSSK.Formats.Audio;
using DWSSK.Formats.Core;
using DWSSK.Formats.Video;
using Omega.Plugins;
namespace DWSSK.Formats.Nodes;
internal sealed class BinkNode : SlotNode, IVideoAsset
{
    private (int Revision, BinkFile File)? _file;
    private (int Revision, byte[] Wav)? _soundtrack;
    public BinkNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Video)
    {
    }
    private BinkFile File
    {
        get
        {
            if (_file is not { } c || c.Revision != Slot.Revision)
            {
                _file = c = (Slot.Revision, BinkFile.Read(Slot.Data));
            }
            return c.File;
        }
    }
    public VideoInfo Video => new(File.Width, File.Height, File.RateNumerator, File.RateDenominator, File.Frames, $"Bink {File.Revision}");
    public IVideoReader OpenVideo()
    {
        return new BinkReader(File);
    }
    public ReadOnlySpan<byte> Soundtrack
    {
        get
        {
            if (_soundtrack is not { } c || c.Revision != Slot.Revision)
            {
                byte[] wav = Bink.Audio(File) is { } a ? Wem.ToWav(a.Samples, a.Rate, a.Channels) : [];
                _soundtrack = c = (Slot.Revision, wav);
            }
            return c.Wav;
        }
    }
    protected override void Check(byte[] payload)
    {
        _ = BinkFile.Read(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        BinkFile f = File;
        yield return PropertyRow.Of("Format", $"Bink video, revision '{f.Revision}'{(f.HasAlpha ? ", with alpha" : string.Empty)}");
        yield return PropertyRow.Of("Timebase", $"{f.RateNumerator}/{f.RateDenominator}");
        yield return PropertyRow.Of("Keyframes", f.Keyframes.Count(k => k));
        for (int t = 0; t < f.Tracks.Count; t++)
        {
            BinkTrack track = f.Tracks[t];
            yield return PropertyRow.Of($"Audio track {t}", $"{track.Rate} Hz, {(track.Channels == 2 ? "stereo" : "mono")}, {(track.Dct ? "DCT" : "RDFT")}, id {track.Id}");
        }
        if (f.Tracks.Count == 0)
        {
            yield return PropertyRow.Of("Audio", "none");
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
