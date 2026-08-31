using Omega.Plugins;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// An animation clip stored as its own archive slot.
///
/// <para>
/// Deliberately not an <see cref="IAnimatedAsset"/>: a clip has nothing to pose on
/// its own. It is the model beside it that offers the clip and does the skinning —
/// see <see cref="ModelNode.Clips"/>. Selecting a clip here shows what is in it.
/// </para>
/// </summary>
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
               $"{animation.Frames} frames, {animation.Tracks.Count} tracks")
    {
        _animation = animation;
    }

    /// <summary>Converts a parsed clip into the neutral form the host's transport drives.</summary>
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
