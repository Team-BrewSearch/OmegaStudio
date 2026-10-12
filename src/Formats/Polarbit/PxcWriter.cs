using System.Buffers.Binary;
using System.Numerics;
using Omega.Plugins.Gltf;
namespace Polarbit.Formats;
internal static class PxcWriter
{
    public static (byte[] Payload, List<string> Notes) Replace(byte[] original, GltfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PxcScene scene = PxcScene.Parse(original);
        List<PxcObject> targets = [.. scene.Objects.Where(o => o.Mesh is { Chunk: "MESH" })];
        Le.Need(targets.Count > 0, "This scene has no MESH objects to replace; track scenery (LICM) cannot be imported.");
        List<string> notes = [];
        Dictionary<PxcObject, List<EngineMesh>> assigned = [];
        List<EngineMesh> leftovers = [];
        foreach (GltfMesh source in document.Meshes)
        {
            EngineMesh mesh = GltfInput.Convert(source);
            if (mesh.TriangleCount == 0)
            {
                continue;
            }
            PxcObject? named = targets.Find(o => string.Equals(o.Label, source.Name, StringComparison.OrdinalIgnoreCase) || (o.Name.Length > 0 && string.Equals(o.Name, source.Name, StringComparison.OrdinalIgnoreCase)));
            if (named is not null && !assigned.ContainsKey(named))
            {
                assigned[named] = [mesh];
            }
            else
            {
                leftovers.Add(mesh);
            }
        }
        Le.Need(assigned.Count + leftovers.Count > 0, "The glTF file has no triangles to import.");
        foreach (PxcObject target in targets.Where(t => !assigned.ContainsKey(t)))
        {
            if (leftovers.Count == 0)
            {
                break;
            }
            assigned[target] = [leftovers[0]];
            leftovers.RemoveAt(0);
        }
        if (leftovers.Count > 0)
        {
            PxcObject last = targets.Last(assigned.ContainsKey);
            assigned[last].AddRange(leftovers);
            notes.Add($"the file has more meshes than the scene's {targets.Count} mesh objects; {leftovers.Count} were merged into '{last.Label}'");
        }
        if (assigned.Count < targets.Count)
        {
            notes.Add($"{targets.Count - assigned.Count} mesh object(s) had no glTF mesh and were kept");
        }
        byte[] buffer = original;
        foreach ((PxcObject target, List<EngineMesh> meshes) in assigned.OrderByDescending(x => x.Key.Mesh!.ChunkOffset))
        {
            buffer = Splice(buffer, scene, target, GltfInput.Merge(meshes), scene.Getaway);
        }
        PxcScene check = PxcScene.Parse(buffer);
        Le.Need(check.Objects.Count == scene.Objects.Count, "The rebuilt scene did not read back.");
        return (buffer, notes);
    }
    private static byte[] Splice(byte[] b, PxcScene scene, PxcObject target, EngineMesh mesh, bool getaway)
    {
        PxcMesh old = target.Mesh!;
        Le.Need(old.Textures.Count > 0 && old.VertexBlocks.Count == 1, $"'{target.Label}' has no material slot to draw the new mesh with.");
        PxcVertexBlock format = old.VertexBlocks[0];
        int count = mesh.VertexCount, indexCount = mesh.Groups.Sum(g => g.Indices.Length);
        Le.Need(count <= ushort.MaxValue && indexCount <= ushort.MaxValue,
            $"'{mesh.Name}' has {count} vertices and {indexCount / 3} triangles; a PXC mesh holds at most {ushort.MaxValue} of each index. Split or decimate it.");
        float[] inverse = PxcScene.Inverse(target.WorldMatrix);
        float[] m = target.WorldMatrix, t = target.WorldTranslation;
        float[] transpose = [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]];
        int[] local = new int[count * 3];
        using MemoryStream chunk = new();
        using BinaryWriter w = new(chunk);
        w.Write(new byte[28]);
        if (getaway)
        {
            w.Write(1u);
            w.Write(1u);
        }
        w.Write((uint)format.Format);
        w.Write((uint)count);
        w.Write(0u);
        for (int v = 0; v < count; v++)
        {
            Vector3 p = mesh.Positions[v];
            (float x, float y, float z) = PxcScene.Apply(inverse, [0, 0, 0], p.X - t[0], p.Y - t[1], p.Z - t[2]);
            local[v * 3] = Le.ToFixed(x);
            local[(v * 3) + 1] = Le.ToFixed(y);
            local[(v * 3) + 2] = Le.ToFixed(z);
            w.Write(local[v * 3]);
            w.Write(local[(v * 3) + 1]);
            w.Write(local[(v * 3) + 2]);
            if (format.HasNormal)
            {
                Vector3 n = mesh.Normals[v];
                (float nx, float ny, float nz) = PxcScene.Apply(transpose, [0, 0, 0], n.X, n.Y, n.Z);
                float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                length = length > 0 ? length : 1;
                w.Write(Le.ToFixed(nx / length));
                w.Write(Le.ToFixed(ny / length));
                w.Write(Le.ToFixed(nz / length));
            }
            if (format.HasColour)
            {
                w.Write(mesh.Colors, v * 4, 4);
            }
            w.Write(Le.ToFixed(mesh.Uvs[v].X));
            w.Write(Le.ToFixed(mesh.Uvs[v].Y));
        }
        w.Write(4u);
        w.Write((uint)indexCount);
        w.Write(5u);
        List<(int Material, int First, int Count)> ranges = [];
        int written = 0;
        foreach ((string material, int[] indices) in mesh.Groups)
        {
            ranges.Add((GltfInput.Slot(material, ranges.Count, old.Textures), written, indices.Length));
            written += indices.Length;
            foreach (int index in indices)
            {
                w.Write((ushort)index);
            }
        }
        w.Write(b, old.MaterialsOffset, old.RangesOffset - old.MaterialsOffset);
        w.Write((uint)ranges.Count);
        foreach ((int material, int first, int n) in ranges)
        {
            w.Write((ushort)5);
            w.Write((ushort)material);
            w.Write((ushort)n);
            w.Write((ushort)first);
            if (getaway)
            {
                w.Write(new byte[8]);
            }
        }
        w.Flush();
        byte[] body = chunk.ToArray();
        int[] bounds = Bounds(local);
        for (int i = 0; i < 7; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4 * i), bounds[i]);
        }
        int oldStart = old.ChunkOffset, oldEnd = old.ChunkOffset + 8 + (int)Le.U32(b, old.ChunkOffset + 4);
        byte[] result = new byte[b.Length - (oldEnd - oldStart) + 8 + body.Length];
        b.AsSpan(0, oldStart).CopyTo(result);
        "MESH"u8.CopyTo(result.AsSpan(oldStart));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(oldStart + 4), (uint)body.Length);
        body.CopyTo(result, oldStart + 8);
        b.AsSpan(oldEnd).CopyTo(result.AsSpan(oldStart + 8 + body.Length));
        for (int i = 0; i < 7; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(target.PlacementOffset + (4 * i)), bounds[i]);
        }
        int delta = result.Length - b.Length;
        for (PxcObject? o = target; o is not null; o = o.Parent >= 0 ? scene.Objects[o.Parent] : null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o.Offset + 4), (uint)(Le.U32(result, o.Offset + 4) + delta));
        }
        return result;
    }
    private static int[] Bounds(int[] p)
    {
        long[] lo = [long.MaxValue, long.MaxValue, long.MaxValue], hi = [long.MinValue, long.MinValue, long.MinValue];
        for (int v = 0; v < p.Length; v += 3)
        {
            for (int a = 0; a < 3; a++)
            {
                lo[a] = Math.Min(lo[a], p[v + a]);
                hi[a] = Math.Max(hi[a], p[v + a]);
            }
        }
        long[] centre = [.. Enumerable.Range(0, 3).Select(a => (long)Math.Floor((lo[a] + hi[a] + 1) / 2.0))];
        long[] half = [.. Enumerable.Range(0, 3).Select(a => (hi[a] - lo[a] + 1) / 2)];
        long radius = (long)Math.Round(Math.Sqrt(half.Sum(h => (double)h * h)));
        return [(int)radius, (int)centre[0], (int)centre[1], (int)centre[2], (int)half[0], (int)half[1], (int)half[2]];
    }
}
