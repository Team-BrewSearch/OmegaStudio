using System.Numerics;
namespace Virtuos.Formats;
public sealed class TrkImportPrimitive
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] Uvs { get; init; }
    public required byte[] Colors { get; init; }
    public required int[] Indices { get; init; }
    public int[]? Bones { get; init; }
    public required int TextureIndex { get; init; }
    public int? ChunkIndex { get; init; }
    public IReadOnlyList<(int Mode, int TriangleCount)>? Blocks { get; init; }
    public IReadOnlyList<bool>? TexcoordEmit { get; init; }
    public IReadOnlyList<int>? GroupBreaks { get; init; }
}
public sealed class TrkChunk
{
    public required Vector3 Position { get; init; }
    public required List<(TrkImportPrimitive Prim, int A, int B, int C)> Triangles { get; init; }
    public ushort ChunkType { get; init; } = 0x000E;
    public uint CoordScale { get; init; }
    public uint PackedParams { get; init; }
    public float? TemplateScale { get; init; }
    public byte[] TemplatePrefix { get; init; } = [];
}
