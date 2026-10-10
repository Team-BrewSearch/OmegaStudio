using System.Numerics;
namespace Virtuos.Formats;

public static class TrkChunker
{
    public static IReadOnlyList<TrkChunk>? ChunkByExplicitIndex(
        IReadOnlyList<TrkImportPrimitive> primitives, IReadOnlyList<Vector3> anchors,
        IReadOnlyList<ushort>? anchorTypes = null,
        IReadOnlyList<(uint CoordScale, uint PackedParams)>? anchorExtraFields = null,
        IReadOnlyList<float?>? anchorScales = null,
        IReadOnlyList<byte[]>? anchorPrefixes = null)
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
            IReadOnlyList<TrkChunk> viaAnchors =
                ChunkToAnchors(unassigned, anchors, anchorTypes, anchorExtraFields, anchorScales, anchorPrefixes);
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
                TemplatePrefix = anchorPrefixes?[i] ?? [],
            });
        }
        return chunks;
    }
    public static IReadOnlyList<TrkChunk> ChunkToAnchors(
        IReadOnlyList<TrkImportPrimitive> primitives, IReadOnlyList<Vector3> anchors,
        IReadOnlyList<ushort>? anchorTypes = null,
        IReadOnlyList<(uint CoordScale, uint PackedParams)>? anchorExtraFields = null,
        IReadOnlyList<float?>? anchorScales = null,
        IReadOnlyList<byte[]>? anchorPrefixes = null)
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
                TemplatePrefix = anchorPrefixes?[i] ?? [],
            });
        }
        return chunks;
    }
    private const int DefaultTargetChunkCount = 100;
    public static IReadOnlyList<TrkChunk> Chunk(
        IReadOnlyList<TrkImportPrimitive> primitives, int? targetChunkCount = null)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        (Vector3 min, Vector3 max) = Bounds(primitives);
        int target = targetChunkCount is > 0 ? targetChunkCount.Value : DefaultTargetChunkCount;
        if (target <= 1 || min.X > max.X)
        {
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
    private static float ResolveCellSize(
        Vector3 min, Vector3 max, IReadOnlyList<TrkImportPrimitive> primitives, int targetCount)
    {
        Vector3 extent = max - min;
        float longest = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        if (longest <= 1e-6f)
        {
            return 1f;
        }
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
                lo = mid;
            }
            else
            {
                hi = mid;
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
