namespace Omega.Plugins;
/// <summary>Keyed transforms for one animated bone.</summary>
public sealed class AnimationTrack
{
    /// <summary>The bone this track drives, matched to the skeleton by name.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Key times in frames, one per entry of <see cref="Matrices"/>.</summary>
    public float[] Times { get; init; } = [];
    /// <summary>One row-major 4x4 matrix per key.</summary>
    public float[][] Matrices { get; init; } = [];
    /// <summary>How many keys the track holds.</summary>
    public int KeyCount => Times.Length;
    /// <summary>The track name and its key count, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} keys={KeyCount}";
    }
}
/// <summary>An animation the transport can play and the dope sheet can draw.</summary>
public sealed class AnimationClip
{
    /// <summary>Clip name, as the transport&apos;s picker lists it.</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Clip length in frames.</summary>
    public int Frames { get; init; }
    /// <summary>Frames per second, which turns frames into seconds.</summary>
    public float Fps { get; init; } = 60f;
    /// <summary>One track per animated bone. An empty list poses nothing.</summary>
    public IReadOnlyList<AnimationTrack> Tracks { get; init; } = [];
    /// <summary>Clip length in seconds, or zero when <see cref="Fps"/> is not positive.</summary>
    public double Duration => Fps > 0f ? Frames / Fps : 0d;
    /// <summary>The clip name.</summary>
    public override string ToString()
    {
        return Name;
    }
}
