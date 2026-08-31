namespace Harugeki.Formats;

public sealed class RingMaterial
{
    public const uint NoTexture = 0xFFFFFFFF;
    public string Name { get; init; } = string.Empty;
    public uint TextureIndex { get; init; }
    public float[] Color { get; init; } = [1, 1, 1, 1];
    public bool HasTexture => TextureIndex != NoTexture;
    public override string ToString()
    {
        return $"{Name} tex={(HasTexture ? TextureIndex : -1)}";
    }
}
