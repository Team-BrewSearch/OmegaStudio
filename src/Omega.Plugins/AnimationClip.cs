namespace Omega.Plugins;

/// <summary>
/// One animated node's keys: a name matching a bone, and a matrix per key.
/// </summary>
public sealed class AnimationTrack
{
    /// <summary>The bone this track drives, matched to the skeleton by name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Key times, in frames. Must be the same length as <see cref="Matrices"/>.</summary>
    public float[] Times { get; init; } = [];

    /// <summary>Row-major 4x4 per key.</summary>
    public float[][] Matrices { get; init; } = [];

    /// <summary>How many keys this track holds.</summary>
    public int KeyCount => Times.Length;

    /// <summary>The track's name and key count, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Name} keys={KeyCount}";
    }
}

/// <summary>
/// An animation clip, as the dope sheet and the transport see it. Sampling it is
/// the plugin's job — see <see cref="IAnimatedAsset.TryPose"/>.
/// </summary>
public sealed class AnimationClip
{
    /// <summary>The clip's name, as the transport's picker shows it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Clip length in frames.</summary>
    public int Frames { get; init; }

    /// <summary>Frames per second, so the transport can show seconds.</summary>
    public float Fps { get; init; } = 60f;

    /// <summary>One track per animated bone. An empty list is a clip that poses nothing.</summary>
    public IReadOnlyList<AnimationTrack> Tracks { get; init; } = [];

    /// <summary>Clip length in seconds.</summary>
    public double Duration => Fps > 0f ? Frames / Fps : 0d;

    /// <summary>The clip's name, for diagnostics.</summary>
    public override string ToString()
    {
        return Name;
    }
}
