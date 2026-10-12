namespace Omega.Plugins;
public sealed class AnimationTrack
{
    public string Name { get; init; } = string.Empty;
    public float[] Times { get; init; } = [];
    public float[][] Matrices { get; init; } = [];
    public int KeyCount => Times.Length;
    public override string ToString()
    {
        return $"{Name} keys={KeyCount}";
    }
}
public sealed class AnimationClip
{
    public string Name { get; init; } = string.Empty;
    public int Frames { get; init; }
    public float Fps { get; init; } = 60f;
    public IReadOnlyList<AnimationTrack> Tracks { get; init; } = [];
    public double Duration => Fps > 0f ? Frames / Fps : 0d;
    public override string ToString()
    {
        return Name;
    }
}
