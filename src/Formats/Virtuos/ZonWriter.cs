using System.Numerics;
using Omega.Plugins;
using Omega.Plugins.Gltf;
using Virtuos.Formats.Gltf;
namespace Virtuos.Formats;
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
                : Rebuild(template, model, chunks[i], i);
        }
        return ZonModel.Assemble(blobs, template[model.TrailerStart..], model.Header);
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
        byte[][] prefixes = TrkWriter.ReadChunkPrefixes(template, model.ChunkOffsets);
        IReadOnlyList<TrkChunk> chunks =
            TrkChunker.ChunkByExplicitIndex(primitives, anchors, types, extras, scales, prefixes)
            ?? TrkChunker.ChunkToAnchors(primitives, anchors, types, extras, scales, prefixes);
        int triangles = 0;
        foreach (TrkChunk chunk in chunks)
        {
            triangles += chunk.Triangles.Count;
        }
        byte[] rebuilt = Build(template, chunks);
        return (Unchanged(template, rebuilt) ? template.ToArray() : rebuilt, triangles);
    }
    private static byte[] Rebuild(
        ReadOnlySpan<byte> template, ZonModel model, TrkChunk chunk, int index)
    {
        byte[] built = TrkWriter.BuildChunk(chunk, index);
        if (index >= model.ChunkOffsets.Count)
        {
            return built;
        }
        int start = model.ChunkOffsets[index];
        ChunkLayout layout = ChunkLayout.Of(template, start) ?? ChunkLayout.Narrow;
        if (layout == ChunkLayout.Narrow)
        {
            return built;
        }
        int head = layout.ListStart(template, start) - start;
        int builtList = (int)BinaryRead.U32(built, 4);
        int builtHead = built.Length - builtList;
        byte[] output = new byte[head + builtList];
        template.Slice(start, head).CopyTo(output);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)builtList);
        built.AsSpan(builtHead, builtList).CopyTo(output.AsSpan(head));
        return output;
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
