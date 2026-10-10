using Virtuos.Formats.Binary;
using Virtuos.Formats.Gltf;
using Omega.Plugins.Gltf;
using System.Buffers.Binary;
using System.Numerics;
namespace Virtuos.Formats;

public static class CarWriter
{
    public static byte[] Build(ReadOnlySpan<byte> template, IReadOnlyList<TrkChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));
        }
        CarModel model = CarModel.Parse(template);
        byte[][] blobs = new byte[chunks.Count][];
        for (int i = 0; i < chunks.Count; i++)
        {
            blobs[i] = chunks[i].Triangles.Count == 0 && i < model.ChunkOffsets.Count
                ? CopyChunk(template, model, i)
                : TrkWriter.BuildChunk(chunks[i], i);
        }
        int table = (int)BinaryRead.U32(template, model.TableField);
        int body = table + (blobs.Length * 4);
        foreach (byte[] blob in blobs)
        {
            body += blob.Length;
        }
        ReadOnlySpan<byte> tail = template[model.TailStart..];
        byte[] output = new byte[body + tail.Length];
        template[..table].CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)blobs.Length);
        int at = table + (blobs.Length * 4);
        for (int i = 0; i < blobs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(table + (i * 4)), (uint)at);
            blobs[i].CopyTo(output.AsSpan(at));
            at += blobs[i].Length;
        }
        tail.CopyTo(output.AsSpan(at));
        int shift = at - model.TailStart;
        for (int field = 0; field + 4 <= table; field += 4)
        {
            uint pointer = BinaryRead.U32(template, field);
            if (pointer >= model.TailStart && pointer <= template.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(field), (uint)(pointer + shift));
            }
        }
        return output;
    }
    public static (byte[] Bytes, int Triangles) Convert(
        IGltfSource source, ReadOnlySpan<byte> template, IReadOnlyList<SskTexture> textures, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentNullException.ThrowIfNull(textures);
        CarModel model = CarModel.Parse(template);
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
    private static bool Unchanged(ReadOnlySpan<byte> template, ReadOnlySpan<byte> rebuilt)
    {
        try
        {
            return SameGeometry(CarModel.Parse(template).Build([]), CarModel.Parse(rebuilt).Build([]));
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
    private static byte[] CopyChunk(ReadOnlySpan<byte> template, CarModel model, int index)
    {
        int start = model.ChunkOffsets[index];
        int end = index + 1 < model.ChunkOffsets.Count ? model.ChunkOffsets[index + 1] : model.TailStart;
        return template[start..end].ToArray();
    }
}
