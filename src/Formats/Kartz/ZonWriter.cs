using Kartz.Formats.Gltf;
using Omega.Plugins.Gltf;
using System.Numerics;
namespace Kartz.Formats;

public static class ZonWriter
{
    public static byte[] Build(ReadOnlySpan<byte> template, IReadOnlyList<TrkChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));
        }
        ZonModel model = ZonModel.Parse(template);
        byte[][] blobs = new byte[chunks.Count][];
        for (int i = 0; i < chunks.Count; i++)
        {
            blobs[i] = chunks[i].Triangles.Count == 0 && i < model.ChunkOffsets.Count
                ? CopyChunk(template, model, i)
                : TrkWriter.BuildChunk(chunks[i], i);
        }
        return ZonModel.Assemble(blobs, template[model.TrailerStart..]);
    }
    public static (byte[] Bytes, int Triangles) Convert(
        IGltfSource source, ReadOnlySpan<byte> template, IReadOnlyList<SskTexture> textures, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentNullException.ThrowIfNull(textures);
        ZonModel model = ZonModel.Parse(template);
        List<TrkImportPrimitive> primitives = TrkGltfReader.Read(source, textures, warn);
        Vector3[] anchors = new Vector3[model.ChunkOffsets.Count];
        ushort[] types = TrkWriter.ReadChunkTypes(template, model.ChunkOffsets);
        (uint CoordScale, uint PackedParams)[] extras =
            TrkWriter.ReadChunkExtraFields(template, model.ChunkOffsets);
        float?[] scales = TrkWriter.ReadChunkVtxScales(template, model.ChunkOffsets);
        IReadOnlyList<TrkChunk> chunks =
            TrkChunker.ChunkByExplicitIndex(primitives, anchors, types, extras, scales)
            ?? TrkChunker.ChunkToAnchors(primitives, anchors, types, extras, scales);
        int triangles = 0;
        foreach (TrkChunk chunk in chunks)
        {
            triangles += chunk.Triangles.Count;
        }
        byte[] rebuilt = Build(template, chunks);
        return (Unchanged(template, rebuilt) ? template.ToArray() : rebuilt, triangles);
    }
    private static bool Unchanged(ReadOnlySpan<byte> template, ReadOnlySpan<byte> rebuilt)
    {
        try
        {
            return SameGeometry(
                ZonModel.Parse(template).Build([]), ZonModel.Parse(rebuilt).Build([]));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
    private static bool SameGeometry(SskTrack left, SskTrack right)
    {
        return left.Positions.AsSpan().SequenceEqual(right.Positions)
            && left.Normals.AsSpan().SequenceEqual(right.Normals)
            && left.Uvs.AsSpan().SequenceEqual(right.Uvs)
            && left.Colors.AsSpan().SequenceEqual(right.Colors)
            && left.Indices.AsSpan().SequenceEqual(right.Indices);
    }
    private static byte[] CopyChunk(ReadOnlySpan<byte> template, ZonModel model, int index)
    {
        int start = model.ChunkOffsets[index];
        int end = index + 1 < model.ChunkOffsets.Count ? model.ChunkOffsets[index + 1] : model.TrailerStart;
        return template[start..end].ToArray();
    }
}
