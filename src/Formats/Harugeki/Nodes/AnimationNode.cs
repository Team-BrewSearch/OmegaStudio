using Omega.Plugins;
namespace Harugeki.Formats.Nodes;
internal sealed class AnimationNode : SlotNode, IDescribable
{
    private readonly RingAnimation _animation;
    public AnimationNode(RingNode slot, string header)
        : this(slot, header, RingAnimation.Parse(slot.Span, $"anim{slot.Index:00}"))
    {
    }
    private AnimationNode(RingNode slot, string header, RingAnimation animation)
        : base(slot,
               $"{header} {animation.Name}",
               AssetKinds.Animation,
               $"{animation.Frames} frames, {animation.Tracks.Count} tracks",
               animation.Name)
    {
        _animation = animation;
    }
    public static AnimationClip ToClip(RingAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        return new AnimationClip
        {
            Name = animation.Name,
            Frames = animation.Frames,
            Fps = RingAnimation.Fps,
            Tracks = [.. animation.Tracks.Select(track => new AnimationTrack
            {
                Name = track.Name,
                Times = track.Times,
                Matrices = track.Matrices,
            })],
        };
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _animation.Name);
        yield return PropertyRow.Of("Frames", _animation.Frames);
        yield return PropertyRow.Of("Duration", $"{_animation.Duration:0.00} s");
        yield return PropertyRow.Of("Tracks", _animation.Tracks.Count);
        yield return PropertyRow.Of(
            "Keys/track", _animation.Tracks.Count > 0 ? _animation.Tracks[0].Times.Length : 0);
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
