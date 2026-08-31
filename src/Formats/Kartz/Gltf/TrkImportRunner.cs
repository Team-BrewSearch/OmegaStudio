using Omega.Plugins.Gltf;
using System.Numerics;

namespace Kartz.Formats.Gltf;

/// <summary>Converts a glTF file into replacement <c>.trk</c> bytes for an existing track.</summary>
public static class TrkImportRunner
{
    /// <summary>
    /// Reads <paramref name="source"/>'s geometry, resolves its materials against the
    /// track's existing texture slots, chunks the result to match the track's own chunk
    /// layout, and builds a full <c>.trk</c> blob using <paramref name="template"/> as a
    /// header and tail template. Nothing is modified: the caller applies the result.
    /// </summary>
    /// <returns>
    /// The new <c>.trk</c> bytes; whether the target's own chunk position table could be read
    /// and used (see <see cref="TrkWriter.ReadChunkPositions"/>); and how many triangles were
    /// written, which is reported here rather than counted by re-decoding the finished blob.
    /// When the layout flag is <see langword="false"/> the result fell back to a single chunk —
    /// the caller should then also replace the paired <c>.bpvs</c> with
    /// <see cref="BpvsWriter.BuildMinimalSingleSection"/>, since a single chunk no longer
    /// matches the template's own tail-section chunk count (see <see cref="TrkChunker"/>'s
    /// remarks for why that mismatch matters).
    /// </returns>
    /// <param name="source">The glTF, opened by the host at the container level.</param>
    /// <param name="template">The track's current bytes, used as the header and tail template.</param>
    /// <param name="textures">The track's existing texture slots, which are kept.</param>
    /// <param name="warn">Where non-fatal notes about the conversion are reported.</param>
    public static (byte[] Bytes, bool MatchedOriginalLayout, int Triangles) ConvertToTrk(
        IGltfSource source,
        ReadOnlySpan<byte> template,
        IReadOnlyList<SskTexture> textures,
        Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        List<TrkImportPrimitive> primitives = TrkGltfReader.Read(source, textures, warn);
        Vector3[] anchors = TrkWriter.ReadChunkPositions(template);
        ushort[] anchorTypes = TrkWriter.ReadChunkTypes(template);
        (uint CoordScale, uint PackedParams)[] anchorExtras = TrkWriter.ReadChunkExtraFields(template);
        float?[] anchorScales = TrkWriter.ReadChunkVtxScales(template);

        IReadOnlyList<TrkChunk> chunks;
        bool matchedLayout;

        if (anchors.Length > 0 && anchorTypes.Length == anchors.Length
            && anchorExtras.Length == anchors.Length && anchorScales.Length == anchors.Length)
        {
            // An explicit per-primitive chunk index — from this plugin's own per-chunk glTF
            // export — is exact, with no nearest-anchor guessing at all, and is always
            // preferred when present. ChunkToAnchors remains the fallback for glTF files
            // that do not carry it. See TrkChunker.ChunkByExplicitIndex's remarks.
            chunks = TrkChunker.ChunkByExplicitIndex(primitives, anchors, anchorTypes, anchorExtras, anchorScales)
                ?? TrkChunker.ChunkToAnchors(primitives, anchors, anchorTypes, anchorExtras, anchorScales);
            matchedLayout = true;
        }
        else
        {
            warn("Could not read the original track's own chunk layout; falling back to a single chunk.");
            chunks = TrkChunker.Chunk(primitives, targetChunkCount: 1);
            matchedLayout = false;
        }

        int triangles = 0;
        foreach (TrkChunk chunk in chunks)
        {
            triangles += chunk.Triangles.Count;
        }

        return (TrkWriter.Build(template, chunks), matchedLayout, triangles);
    }
}
