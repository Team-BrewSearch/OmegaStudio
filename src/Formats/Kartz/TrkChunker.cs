using System.Numerics;

namespace Kartz.Formats;

/// <summary>
/// Spatial chunking for a freshly imported glTF mesh.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app always uses <see cref="ChunkToAnchors"/> when the template's own chunk position
/// table can be read</b> (see <see cref="Kartz.Formats.TrkWriter.ReadChunkPositions"/>), not
/// <see cref="Chunk"/>'s independent grid search. Two multi-chunk approaches were tried and
/// rejected before this one: matching only the *count* of the original track's chunks (grid
/// search targeting the same total) loaded and mostly played, but the paired <c>.bpvs</c> and
/// the tail sections <see cref="Kartz.Formats.TrkWriter"/> copies unchanged are keyed by chunk
/// *index*, and an independently-gridded new chunk 5 can occupy a totally different world
/// region than the original chunk 5 did — confirmed as the cause of per-chunk visual
/// corruption and a strict emulator's rasterizer crash on real hardware/emulator testing.
/// Collapsing to a single chunk (so there'd be no cross-chunk visibility data to be wrong
/// about) was worse, not better: the unchanged tail data still describes the *original* chunk
/// count internally, so <c>numTotal=1</c> created a genuine structural mismatch that crashed
/// every tested emulator immediately on load, not just the strict one. Anchoring each new
/// chunk to its same-index original position is the fix that keeps both the count and the
/// approximate spatial meaning intact. <see cref="Chunk"/>'s independent grid search remains
/// only as a fallback for when a template's position table can't be read at all.
/// </para>
/// <para>
/// <b>The grid must be anchored to the mesh, not to world-space (0,0,0).</b> A cell size search
/// that floors <c>centroid / cell</c> directly can still split a mesh across a cell boundary
/// even when the cell is larger than the mesh's entire extent, whenever the bounding box
/// straddles a world-axis origin — exactly FarFarAway's case (its X extent runs -2351..895).
/// Flooring <c>(centroid - boundsMin) / cell</c> instead guarantees every point maps into cell
/// (0,0,0) once the cell size reaches the bounding box's longest extent, which is what makes
/// <c>targetCount == 1</c> actually reliable rather than merely usually-close.
/// </para>
/// </remarks>
public static class TrkChunker
{
    /// <summary>
    /// Assigns every triangle to its exact original chunk, read directly from each primitive's
    /// <see cref="TrkImportPrimitive.ChunkIndex"/> (set when the source glTF was this app's own
    /// per-chunk export — see <c>GltfWriter</c>'s remarks) — no distance-based guessing at all.
    /// This is what makes an unmodified round-trip reproduce the original chunk partitioning
    /// exactly rather than merely approximately (<see cref="ChunkToAnchors"/>'s nearest-anchor
    /// bucketing can and does occasionally place a boundary triangle in a neighboring chunk from
    /// where it started). A primitive with no <c>ChunkIndex</c> (non-Kartz glTF, or a node the
    /// user renamed) falls back to nearest-anchor bucketing for just its own triangles, so a
    /// glTF mixing authored chunk meshes with freehand additions still works.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> if no primitive carries a <c>ChunkIndex</c> at all — callers
    /// should fall back to <see cref="ChunkToAnchors"/> entirely.
    /// </returns>
    public static IReadOnlyList<TrkChunk>? ChunkByExplicitIndex(
        IReadOnlyList<TrkImportPrimitive> primitives, IReadOnlyList<Vector3> anchors,
        IReadOnlyList<ushort>? anchorTypes = null,
        IReadOnlyList<(uint CoordScale, uint PackedParams)>? anchorExtraFields = null,
        IReadOnlyList<float?>? anchorScales = null)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(anchors);

        if (!primitives.Any(p => p.ChunkIndex.HasValue))
        {
            return null;
        }

        List<List<(TrkImportPrimitive Prim, int A, int B, int C)>> buckets = new(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            buckets.Add([]);
        }

        List<TrkImportPrimitive> unassigned = [];
        foreach (TrkImportPrimitive prim in primitives)
        {
            if (prim.ChunkIndex is not { } chunk || chunk < 0 || chunk >= anchors.Count)
            {
                unassigned.Add(prim);
                continue;
            }

            for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                buckets[chunk].Add((prim, prim.Indices[i], prim.Indices[i + 1], prim.Indices[i + 2]));
            }
        }

        if (unassigned.Count > 0)
        {
            // Nearest-anchor bucketing for the leftover (unnamed) primitives only, merged into
            // the same buckets the explicit indices already populated above.
            IReadOnlyList<TrkChunk> viaAnchors =
                ChunkToAnchors(unassigned, anchors, anchorTypes, anchorExtraFields, anchorScales);
            for (int i = 0; i < anchors.Count; i++)
            {
                buckets[i].AddRange(viaAnchors[i].Triangles);
            }
        }

        List<TrkChunk> chunks = new(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            (uint coordScale, uint packedParams) = anchorExtraFields?[i] ?? (0u, 0u);
            chunks.Add(new TrkChunk
            {
                Position = anchors[i],
                Triangles = buckets[i],
                ChunkType = anchorTypes?[i] ?? 0x000E,
                CoordScale = coordScale,
                PackedParams = packedParams,
                TemplateScale = anchorScales?[i],
            });
        }

        return chunks;
    }

    /// <summary>
    /// Buckets every triangle (across all primitives) to whichever <paramref name="anchors"/>
    /// entry its centroid is nearest to, producing exactly <c>anchors.Count</c> chunks in the
    /// same order — each chunk's <see cref="TrkChunk.Position"/> is its anchor, unchanged,
    /// even for a chunk no triangle lands nearest to (an empty chunk; see
    /// <c>TrkWriter.BuildChunk</c>'s handling of zero texture groups). See the type-level
    /// remarks for why matching indices to the template's own positions matters here.
    /// </summary>
    /// <param name="primitives">The imported geometry, one entry per glTF primitive.</param>
    /// <param name="anchors">
    /// The template's own per-chunk positions, from <see cref="Kartz.Formats.TrkWriter.ReadChunkPositions"/>.
    /// One chunk comes back per anchor, in the same order.
    /// </param>
    /// <param name="anchorTypes">
    /// Per-anchor <see cref="TrkChunk.ChunkType"/>, same length and order as
    /// <paramref name="anchors"/> (from <see cref="Kartz.Formats.TrkWriter.ReadChunkTypes"/>) —
    /// preserving these matters as much as the positions do: a real track mixes "primary"
    /// chunks the paired <c>.bpvs</c> tracks with several times as many untracked "detail"
    /// chunks (confirmed on FarFarAway: 87 primary vs 301 detail out of 388 total), and
    /// stamping every emitted chunk as primary regardless of what it replaces would corrupt
    /// that distinction even though nothing in this app's own parser depends on it. Pass
    /// <see langword="null"/> to mark every chunk primary (<c>0x000E</c>) — only appropriate
    /// when there's no real per-chunk source to preserve, e.g. testing.
    /// </param>
    /// <param name="anchorExtraFields">
    /// Per-anchor <c>coord_scale</c> and <c>packed_params</c>, from
    /// <see cref="Kartz.Formats.TrkWriter.ReadChunkExtraFields"/>. Pass <see langword="null"/> to
    /// leave both zero.
    /// </param>
    /// <param name="anchorScales">
    /// Per-anchor vertex scale, from <see cref="Kartz.Formats.TrkWriter.ReadChunkVtxScales"/>.
    /// A null entry means the chunk had no geometry to measure one from, and the writer picks
    /// its own; pass <see langword="null"/> to do that for every chunk.
    /// </param>
    public static IReadOnlyList<TrkChunk> ChunkToAnchors(
        IReadOnlyList<TrkImportPrimitive> primitives, IReadOnlyList<Vector3> anchors,
        IReadOnlyList<ushort>? anchorTypes = null,
        IReadOnlyList<(uint CoordScale, uint PackedParams)>? anchorExtraFields = null,
        IReadOnlyList<float?>? anchorScales = null)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(anchors);
        if (anchors.Count == 0)
        {
            throw new ArgumentException("At least one anchor is required.", nameof(anchors));
        }

        if (anchorTypes is not null && anchorTypes.Count != anchors.Count)
        {
            throw new ArgumentException("anchorTypes must be the same length as anchors.", nameof(anchorTypes));
        }

        if (anchorExtraFields is not null && anchorExtraFields.Count != anchors.Count)
        {
            throw new ArgumentException(
                "anchorExtraFields must be the same length as anchors.", nameof(anchorExtraFields));
        }

        if (anchorScales is not null && anchorScales.Count != anchors.Count)
        {
            throw new ArgumentException("anchorScales must be the same length as anchors.", nameof(anchorScales));
        }

        List<List<(TrkImportPrimitive Prim, int A, int B, int C)>> buckets = new(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            buckets.Add([]);
        }

        foreach (TrkImportPrimitive prim in primitives)
        {
            for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
                Vector3 centroid = Centroid(prim, a, b, c);

                int best = 0;
                float bestDist = Vector3.DistanceSquared(centroid, anchors[0]);
                for (int k = 1; k < anchors.Count; k++)
                {
                    float d = Vector3.DistanceSquared(centroid, anchors[k]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = k;
                    }
                }

                buckets[best].Add((prim, a, b, c));
            }
        }

        List<TrkChunk> chunks = new(anchors.Count);
        for (int i = 0; i < anchors.Count; i++)
        {
            (uint coordScale, uint packedParams) = anchorExtraFields?[i] ?? (0u, 0u);
            chunks.Add(new TrkChunk
            {
                Position = anchors[i],
                Triangles = buckets[i],
                ChunkType = anchorTypes?[i] ?? 0x000E,
                CoordScale = coordScale,
                PackedParams = packedParams,
                TemplateScale = anchorScales?[i],
            });
        }

        return chunks;
    }

    /// <summary>Chunk count aimed for when the caller doesn't ask for a specific one.</summary>
    private const int DefaultTargetChunkCount = 100;

    public static IReadOnlyList<TrkChunk> Chunk(
        IReadOnlyList<TrkImportPrimitive> primitives, int? targetChunkCount = null)
    {
        ArgumentNullException.ThrowIfNull(primitives);

        (Vector3 min, Vector3 max) = Bounds(primitives);
        int target = targetChunkCount is > 0 ? targetChunkCount.Value : DefaultTargetChunkCount;

        if (target <= 1 || min.X > max.X)
        {
            // Guaranteed-correct fast path: everything in one bucket, no grid math or search
            // needed (and no risk of the origin-anchoring pitfall this class exists to avoid).
            return BuildChunks(primitives, min, cell: float.PositiveInfinity);
        }

        float cell = ResolveCellSize(min, max, primitives, target);
        return BuildChunks(primitives, min, cell);
    }

    private static List<TrkChunk> BuildChunks(IReadOnlyList<TrkImportPrimitive> primitives, Vector3 origin, float cell)
    {
        Dictionary<(int X, int Y, int Z), List<(TrkImportPrimitive Prim, int A, int B, int C)>> buckets = [];

        foreach (TrkImportPrimitive prim in primitives)
        {
            for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                int a = prim.Indices[i], b = prim.Indices[i + 1], c = prim.Indices[i + 2];
                (int X, int Y, int Z) key = CellKey(prim, a, b, c, origin, cell);

                if (!buckets.TryGetValue(key, out List<(TrkImportPrimitive, int, int, int)>? list))
                {
                    buckets[key] = list = [];
                }

                list.Add((prim, a, b, c));
            }
        }

        List<TrkChunk> chunks = new(buckets.Count);
        foreach (List<(TrkImportPrimitive Prim, int A, int B, int C)> triangles in buckets.Values)
        {
            Vector3 sum = Vector3.Zero;
            foreach ((TrkImportPrimitive prim, int a, int b, int c) in triangles)
            {
                sum += Centroid(prim, a, b, c);
            }

            chunks.Add(new TrkChunk { Position = sum / triangles.Count, Triangles = triangles });
        }

        return chunks;
    }

    /// <summary>
    /// Same bucketing as <see cref="BuildChunks"/>, but only counts occupied cells — used by
    /// the search below.
    /// </summary>
    private static int CountChunks(IReadOnlyList<TrkImportPrimitive> primitives, Vector3 origin, float cell)
    {
        HashSet<(int X, int Y, int Z)> buckets = [];
        foreach (TrkImportPrimitive prim in primitives)
        {
            for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                _ = buckets.Add(CellKey(prim, prim.Indices[i], prim.Indices[i + 1], prim.Indices[i + 2], origin, cell));
            }
        }

        return buckets.Count;
    }

    private static (int X, int Y, int Z) CellKey(
        TrkImportPrimitive prim, int a, int b, int c, Vector3 origin, float cell)
    {
        if (float.IsPositiveInfinity(cell))
        {
            return (0, 0, 0);
        }

        Vector3 local = Centroid(prim, a, b, c) - origin;
        return (
            (int)MathF.Floor(local.X / cell),
            (int)MathF.Floor(local.Y / cell),
            (int)MathF.Floor(local.Z / cell));
    }

    private static Vector3 Centroid(TrkImportPrimitive prim, int a, int b, int c)
    {
        return (VertexAt(prim, a) + VertexAt(prim, b) + VertexAt(prim, c)) / 3f;
    }

    private static Vector3 VertexAt(TrkImportPrimitive prim, int index)
    {
        return new Vector3(prim.Positions[index * 3], prim.Positions[(index * 3) + 1], prim.Positions[(index * 3) + 2]);
    }

    /// <summary>
    /// Binary-searches a grid cell size whose resulting chunk count lands as close as it can
    /// to <paramref name="targetCount"/>.
    /// </summary>
    private static float ResolveCellSize(
        Vector3 min, Vector3 max, IReadOnlyList<TrkImportPrimitive> primitives, int targetCount)
    {
        Vector3 extent = max - min;
        float longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        if (longest <= 1e-6f)
        {
            return 1f;
        }

        // Smaller cell -> more (or equal) occupied buckets, so CountChunks is monotonically
        // non-increasing in cell size at the scale this search operates over; track the best
        // match seen across all probes rather than trusting the final endpoint, since grid
        // alignment can make the count non-strictly-monotonic step to step.
        float lo = longest / 4000f, hi = longest * 2f;
        float best = hi;
        int bestDiff = int.MaxValue;

        for (int iter = 0; iter < 40 && bestDiff != 0; iter++)
        {
            float mid = (lo + hi) / 2f;
            int count = CountChunks(primitives, min, mid);
            int diff = Math.Abs(count - targetCount);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = mid;
            }

            if (count > targetCount)
            {
                lo = mid; // too many chunks: grow the cell
            }
            else
            {
                hi = mid; // too few (or exactly enough) chunks: shrink the cell
            }
        }

        return MathF.Max(1e-3f, best);
    }

    private static (Vector3 Min, Vector3 Max) Bounds(IReadOnlyList<TrkImportPrimitive> primitives)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (TrkImportPrimitive prim in primitives)
        {
            for (int i = 0; i < prim.Positions.Length; i += 3)
            {
                Vector3 v = new(prim.Positions[i], prim.Positions[i + 1], prim.Positions[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        return (min, max);
    }
}
