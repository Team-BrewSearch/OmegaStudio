using System.Globalization;
using System.Numerics;
using Omega.Plugins;
namespace SSNCR.Formats;

internal sealed record TrmPlacement(int Frame, TrmSkin? Skin);

internal static class TrmScene
{
    public static string JointName(TrmModel model, int i)
    {
        uint hash = model.Frames[i].NameHash;
        return hash == 0
            ? string.Create(CultureInfo.InvariantCulture, $"frame{i}")
            : string.Create(CultureInfo.InvariantCulture, $"frame{i}_{hash:X8}");
    }

    public static SceneGeometry Build(TrmModel model, Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, ImageData>> textures)
        => Build(model, textures, out _);

    public static SceneGeometry Build(TrmModel model, Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, ImageData>> textures, out List<TrmPlacement> placements)
    {
        placements = [];
        Matrix4x4[] world = model.WorldMatrices();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrmAtomic a in model.Atomics)
            foreach (TrmMaterial m in a.Materials)
                if (m.Texture is { Length: > 0 } t) names.Add(t);
        IReadOnlyDictionary<string, ImageData> found = textures(names);
        List<ImageData> images = [];
        Dictionary<string, int> slot = new(StringComparer.OrdinalIgnoreCase);
        foreach (string n in names.Order(StringComparer.OrdinalIgnoreCase))
            if (found.TryGetValue(n, out ImageData? img)) { slot[n] = images.Count; images.Add(img); }
        var alpha = images.Select(Alpha).ToArray();

        IEnumerable<TrmInstance> placed = model.Instances.Count > 0
            ? model.Instances
            : model.Atomics.Select((_, i) => new TrmInstance(-1, i, 0));
        bool rigged = model.Atomics.Any(a => a.Skin is not null) || model.NumAnimations > 0;
        List<SceneMesh> meshes = [];
        int k = 0;
        foreach (TrmInstance inst in placed)
        {
            TrmAtomic atomic = model.Atomics[inst.Atomic];
            Matrix4x4 xf = atomic.Skin is null && inst.Frame >= 0 ? world[inst.Frame] : Matrix4x4.Identity;
            // a rigid atomic in a rig rides its frame as a one-bone skin, so exported clips move it
            int rideFrame = rigged && atomic.Skin is null ? inst.Frame : -1;
            string meshName = model.InstanceNames.TryGetValue(k, out uint named) ? $"atomic{inst.Atomic}_{k}_{named:X8}" : $"atomic{inst.Atomic}_{k}";
            k++;
            meshes.Add(BuildMesh(atomic, xf, slot, alpha, meshName, rideFrame));
            placements.Add(new TrmPlacement(atomic.Skin is null ? inst.Frame : -1, atomic.Skin));
        }

        var joints = new List<SceneJoint>();
        if (rigged)
        {
            TrmSkin? skin = model.Atomics.Select(a => a.Skin).FirstOrDefault(s => s is not null);
            if (model.Atomics.Count(a => a.Skin is not null) > 1) throw new InvalidDataException("model has more than one skinned atomic");
            for (int i = 0; i < model.Frames.Count; i++)
            {
                float[] bind = new float[16];
                MatrixOps.CopyTo(world[i], bind);
                float[]? inverse = null;
                if (skin is not null && i < skin.InverseBind.Length && !skin.InverseBind[i].IsIdentity)
                {
                    inverse = new float[16];
                    MatrixOps.CopyTo(skin.InverseBind[i], inverse);
                }
                joints.Add(new SceneJoint(JointName(model, i), model.Frames[i].Parent, bind) { InverseBind = inverse });
            }
        }
        var scene = new SceneGeometry
        {
            Meshes = meshes, Textures = images, Joints = joints,
            Clips = rigged ? [.. model.Animations.Select(a => TrmPoser.Clip(model, a, i => JointName(model, i)))] : [],
        };
        scene.Validate();
        return scene;
    }

    public static void Pose(TrmModel model, SceneGeometry scene, List<TrmPlacement> placements, TrmAnimation anim, float seconds,
        out float[] positions, out float[] normals)
    {
        Matrix4x4[] bind = model.WorldMatrices();
        Matrix4x4[] now = TrmPoser.World(model, anim, seconds);
        var rigid = new Matrix4x4[bind.Length];
        for (int i = 0; i < bind.Length; i++) rigid[i] = MatrixOps.InvertOrIdentity(bind[i]) * now[i];
        int total = scene.Meshes.Sum(m => m.VertexCount * 3);
        positions = new float[total];
        normals = new float[total];
        int at = 0;
        for (int mi = 0; mi < scene.Meshes.Count; mi++)
        {
            SceneMesh mesh = scene.Meshes[mi];
            TrmPlacement pl = placements[mi];
            Matrix4x4[]? palette = null;
            if (pl.Skin is { } skin)
            {
                palette = new Matrix4x4[bind.Length];
                for (int b = 0; b < bind.Length; b++)
                {
                    Matrix4x4 inv = b < skin.InverseBind.Length && !skin.InverseBind[b].IsIdentity ? skin.InverseBind[b] : MatrixOps.InvertOrIdentity(bind[b]);
                    palette[b] = inv * now[b];
                }
            }
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var p = new Vector3(mesh.Positions[3 * v], mesh.Positions[3 * v + 1], mesh.Positions[3 * v + 2]);
                Vector3 n = mesh.Normals.Length > 0 ? new Vector3(mesh.Normals[3 * v], mesh.Normals[3 * v + 1], mesh.Normals[3 * v + 2]) : Vector3.Zero;
                Vector3 op, on;
                if (palette is not null && mesh.Joints is { } j && mesh.Weights is { } w)
                {
                    op = Vector3.Zero; on = Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float wt = w[4 * v + k];
                        if (wt == 0f) continue;
                        Matrix4x4 m = palette[j[4 * v + k]];
                        op += Vector3.Transform(p, m) * wt;
                        on += Vector3.TransformNormal(n, m) * wt;
                    }
                }
                else if (pl.Frame >= 0)
                {
                    op = Vector3.Transform(p, rigid[pl.Frame]);
                    on = Vector3.TransformNormal(n, rigid[pl.Frame]);
                }
                else { op = p; on = n; }
                if (on.LengthSquared() > 0) on = Vector3.Normalize(on);
                positions[at] = op.X; positions[at + 1] = op.Y; positions[at + 2] = op.Z;
                normals[at] = on.X; normals[at + 1] = on.Y; normals[at + 2] = on.Z;
                at += 3;
            }
        }
    }

    private enum TexAlpha { Opaque, Cutout, Translucent }

    private static TexAlpha Alpha(ImageData img)
    {
        TexAlpha a = TexAlpha.Opaque;
        for (int i = 3; i < img.Rgba.Length; i += 4)
        {
            byte v = img.Rgba[i];
            if (v == 255) continue;
            if (v != 0) return TexAlpha.Translucent;
            a = TexAlpha.Cutout;
        }
        return a;
    }

    private static SceneMesh BuildMesh(TrmAtomic a, Matrix4x4 xf, Dictionary<string, int> slot, TexAlpha[] alpha, string name, int rideFrame)
    {
        int n = a.Attributes.Length;
        int pa = Array.IndexOf(a.Attributes, (byte)9), na = Array.IndexOf(a.Attributes, (byte)10);
        int ca = Array.IndexOf(a.Attributes, (byte)11), ta = Array.IndexOf(a.Attributes, (byte)13);
        bool hasN = na >= 0 && a.Normals is not null, hasC = ca >= 0 && a.Colors is not null, hasT = ta >= 0 && a.Uv0 is not null;
        TrmSkin? skin = a.Skin;
        List<float> pos = [], nrm = [], uv = [], wts = [];
        List<byte> col = [];
        List<int> jnt = [];
        Dictionary<(int, int, int, int), int> seen = [];

        int Vertex(int[] c, int v)
        {
            int b = v * n;
            int p = c[b + pa];
            var key = (p, hasN ? c[b + na] : -1, hasC ? c[b + ca] : -1, hasT ? c[b + ta] : -1);
            if (seen.TryGetValue(key, out int id)) return id;
            id = pos.Count / 3;
            seen[key] = id;
            Vector3 P = Vector3.Transform(new Vector3(a.Positions![3 * p], a.Positions[3 * p + 1], a.Positions[3 * p + 2]), xf);
            pos.Add(P.X); pos.Add(P.Y); pos.Add(P.Z);
            if (hasN)
            {
                int q = key.Item2;
                Vector3 N = Vector3.TransformNormal(new Vector3(a.Normals![3 * q], a.Normals[3 * q + 1], a.Normals[3 * q + 2]), xf);
                N = N.LengthSquared() > 0 ? Vector3.Normalize(N) : N;
                nrm.Add(N.X); nrm.Add(N.Y); nrm.Add(N.Z);
            }
            if (hasC) for (int i = 0; i < 4; i++) col.Add(a.Colors![4 * key.Item3 + i]);
            if (hasT) { uv.Add(a.Uv0![2 * key.Item4]); uv.Add(a.Uv0[2 * key.Item4 + 1]); }
            if (skin is not null)
            {
                for (int i = 0; i < 4; i++)
                {
                    bool live = i < skin.MaxWeights;
                    jnt.Add(live ? skin.Indices[p * skin.MaxWeights + i] : 0);
                    wts.Add(live ? skin.Weights[p * skin.MaxWeights + i] : 0f);
                }
            }
            return id;
        }

        List<int> indices = [];
        List<SceneSubmesh> subs = [];
        foreach (IGrouping<int, TrmStrip> group in a.Strips.GroupBy(s => s.Material).OrderBy(g => g.Key))
        {
            int first = indices.Count;
            foreach (TrmStrip s in group)
            {
                int verts = s.Corners.Length / n;
                if (s.IsList)
                {
                    for (int v = 0; v + 2 < verts; v += 3)
                        Tri(Vertex(s.Corners, v), Vertex(s.Corners, v + 1), Vertex(s.Corners, v + 2));
                    continue;
                }
                for (int v = 0; v + 2 < verts; v++)
                {
                    int x = Vertex(s.Corners, v), y = Vertex(s.Corners, v + 1), z = Vertex(s.Corners, v + 2);
                    if ((v & 1) == 0) Tri(x, y, z); else Tri(y, x, z);
                }
            }
            TrmMaterial mat = a.Materials[group.Key];
            int tex = mat.Texture is { } t && slot.TryGetValue(t, out int ti) ? ti : -1;
            bool vertexAlpha = false;
            if (hasC)
                for (int i = first; i < indices.Count && !vertexAlpha; i++)
                    vertexAlpha = col[4 * indices[i] + 3] < 254;
            TexAlpha texAlpha = tex >= 0 ? alpha[tex] : TexAlpha.Opaque;
            subs.Add(new SceneSubmesh(mat.Texture ?? $"material{group.Key}", first, indices.Count - first, tex,
                AlphaClip: texAlpha == TexAlpha.Cutout && !vertexAlpha)
            {
                AlphaBlend = texAlpha == TexAlpha.Translucent || vertexAlpha,
            });
        }

        void Tri(int x, int y, int z)
        {
            if (x == y || y == z || x == z) return;
            indices.Add(x); indices.Add(z); indices.Add(y);
        }

        return new SceneMesh
        {
            Name = name,
            Positions = [.. pos],
            Normals = [.. nrm],
            Uvs = [.. uv],
            Colors = [.. col],
            Indices = [.. indices],
            Joints = skin is not null ? [.. jnt] : rideFrame >= 0 ? [.. Enumerable.Range(0, 4 * (pos.Count / 3)).Select(i => i % 4 == 0 ? rideFrame : 0)] : null,
            Weights = skin is not null ? [.. wts] : rideFrame >= 0 ? [.. Enumerable.Range(0, 4 * (pos.Count / 3)).Select(i => i % 4 == 0 ? 1f : 0f)] : null,
            Submeshes = subs,
        };
    }
}

internal sealed class TexLibrary(IReadOnlyList<PackEntry> entries, Func<PackEntry, byte[]> load)
{
    private readonly object _gate = new();
    private Dictionary<string, List<PackEntry>>? _index;

    public IReadOnlyDictionary<string, ImageData> Resolve(IReadOnlyCollection<string> names, string modelPath)
    {
        Dictionary<string, ImageData> result = new(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        Dictionary<string, List<PackEntry>> index = Index();
        string level = Path.GetFileNameWithoutExtension(modelPath);
        Dictionary<PackEntry, List<string>> byFile = [];
        foreach (string name in names)
        {
            if (!index.TryGetValue(name, out List<PackEntry>? owners)) continue;
            PackEntry best = owners.FirstOrDefault(e => Folder(e).Equals(level, StringComparison.OrdinalIgnoreCase))
                ?? owners.FirstOrDefault(e => Folder(e).Equals("structure", StringComparison.OrdinalIgnoreCase))
                ?? owners[0];
            (byFile.TryGetValue(best, out List<string>? l) ? l : byFile[best] = []).Add(name);
        }
        foreach ((PackEntry entry, List<string> wanted) in byFile)
        {
            byte[] data = load(entry);
            if (PspTexFile.IsPspTex(data))
            {
                foreach (PspTexEntry t in PspTexFile.Read(data))
                    if (wanted.Contains(t.Name, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(t.Name))
                        result[t.Name] = PspTexEntryNode.Decode(t, data);
                continue;
            }
            if (Ps2TexFile.IsPs2Tex(data))
            {
                foreach (Ps2TexEntry t in Ps2TexFile.Read(data))
                    if (wanted.Contains(t.Name, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(t.Name))
                        result[t.Name] = Ps2TexEntryNode.Decode(t, data);
                continue;
            }
            foreach (TexTexture t in TexFile.Read(data))
                if (wanted.Contains(t.Name, StringComparer.OrdinalIgnoreCase) && !result.ContainsKey(t.Name))
                    result[t.Name] = TexTextureNode.Decode(t, data);
        }
        return result;
    }

    private static string Folder(PackEntry e) => Path.GetFileName(Path.GetDirectoryName(e.Path) ?? "");

    private Dictionary<string, List<PackEntry>> Index()
    {
        lock (_gate)
        {
            if (_index is not null) return _index;
            Dictionary<string, List<PackEntry>> index = new(StringComparer.OrdinalIgnoreCase);
            foreach (PackEntry e in entries)
            {
                if (!e.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] data = load(e);
                IEnumerable<string> names = PspTexFile.IsPspTex(data)
                    ? PspTexFile.Read(data).Select(t => t.Name)
                    : !TexFile.IsDictionary(data) ? []
                    : Ps2TexFile.IsPs2Tex(data)
                    ? Ps2TexFile.Read(data).Select(t => t.Name)
                    : TexFile.Read(data).Select(t => t.Name);
                foreach (string name in names)
                    (index.TryGetValue(name, out List<PackEntry>? l) ? l : index[name] = []).Add(e);
            }
            return _index = index;
        }
    }
}
