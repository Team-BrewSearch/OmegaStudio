using System.Numerics;
using Omega.Plugins.Gltf;
namespace Virtuos.Formats.Gltf;
public static class TrkImportRunner
{
    public static (byte[] Bytes, bool MatchedOriginalLayout, int Triangles) ConvertToTrk(
        IGltfSource source,
        ReadOnlySpan<byte> template,
        IReadOnlyList<SskTexture> textures,
        Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        if (!TrkWriter.CanWrite(template))
        {
            throw new InvalidDataException(
                "this game lays its track header out differently, and writing one back is not "
                + "worked out yet; the track still draws and exports");
        }
        List<TrkImportPrimitive> primitives = TrkGltfReader.Read(source, textures, warn);
        Vector3[] anchors = TrkWriter.ReadChunkPositions(template);
        ushort[] anchorTypes = TrkWriter.ReadChunkTypes(template);
        (uint CoordScale, uint PackedParams)[] anchorExtras = TrkWriter.ReadChunkExtraFields(template);
        float?[] anchorScales = TrkWriter.ReadChunkVtxScales(template);
        byte[][] anchorPrefixes = TrkWriter.ReadChunkPrefixes(template);
        IReadOnlyList<TrkChunk> chunks;
        bool matchedLayout;
        if (anchors.Length > 0 && anchorTypes.Length == anchors.Length
            && anchorExtras.Length == anchors.Length && anchorScales.Length == anchors.Length)
        {
            chunks = TrkChunker.ChunkByExplicitIndex(primitives, anchors, anchorTypes, anchorExtras, anchorScales, anchorPrefixes)
                ?? TrkChunker.ChunkToAnchors(primitives, anchors, anchorTypes, anchorExtras, anchorScales, anchorPrefixes);
            matchedLayout = true;
        }
        else
        {
            throw new InvalidDataException(
                "the original track's chunk layout could not be read, and the data after its chunks refers to "
                + "them by index, so the import was refused rather than rebuilt with a different layout");
        }
        int triangles = 0;
        foreach (TrkChunk chunk in chunks)
        {
            triangles += chunk.Triangles.Count;
        }
        return (TrkWriter.Build(template, chunks), matchedLayout, triangles);
    }
}
