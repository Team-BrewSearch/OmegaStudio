using System.Globalization;
using System.Numerics;
using DWSSK.Formats.Anim;
using DWSSK.Formats.Core;
using DWSSK.Formats.Models;
using Omega.Plugins;
using Omega.Plugins.Gltf;
namespace DWSSK.Formats.Nodes;
internal sealed class RiggedScene(SceneGeometry scene, AnimRig? rig, Dictionary<AnimationClip, AnimClipData> clips)
{
    public SceneGeometry Scene { get; } = scene;
    public static RiggedScene Build(SceneGeometry scene, GdModel gd, IEnumerable<(Slot Slot, JObj Doc)> packs)
    {
        if (gd.Bones.Count == 0)
        {
            return new RiggedScene(scene, null, []);
        }
        AnimRig rig = new(gd.Bones);
        Dictionary<AnimationClip, AnimClipData> clips = [];
        foreach ((_, JObj doc) in packs)
        {
            foreach (AnimClipData data in rig.Read(doc))
            {
                clips[rig.ToClip(data)] = data;
            }
        }
        SceneGeometry withClips = new()
        {
            Meshes = scene.Meshes,
            Textures = scene.Textures,
            Lighting = scene.Lighting,
            Filtering = scene.Filtering,
            Joints = scene.Joints,
            Clips = [.. clips.Keys],
        };
        return new RiggedScene(withClips, rig, clips);
    }
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        if (rig is null || !clips.TryGetValue(clip, out AnimClipData? data))
        {
            positions = [];
            normals = [];
            return false;
        }
        Matrix4x4[] skin = rig.Pose(data, seconds);
        return AnimRig.Skin(Scene, skin, out positions, out normals);
    }
}
internal sealed class GdNode : SlotNode, ISceneAsset, IAnimatedAsset
{
    private (int Slot, int Group, int Lod, GdModel Model, RiggedScene View)? _view;
    private int _lod;
    public GdNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Model)
    {
    }
    private GdModel Model => Load().Model;
    private (GdModel Model, RiggedScene View) Load()
    {
        if (_view is { } v && v.Slot == Slot.Revision && v.Group == Group.Revision && v.Lod == _lod)
        {
            return (v.Model, v.View);
        }
        GdModel gd = GdReader.Parse(Slot.Data, Slot.Platform);
        SceneGeometry scene = GdScene.Build(gd, Slot.Name, Group.Lookup(Slot), _lod);
        RiggedScene view = RiggedScene.Build(scene, gd, Packs());
        _view = (Slot.Revision, Group.Revision, _lod, gd, view);
        return (gd, view);
    }
    private IEnumerable<(Slot Slot, JObj Doc)> Packs()
    {
        if (!Slot.Name.EndsWith(".gds", StringComparison.Ordinal))
        {
            yield break;
        }
        List<string>? names = null;
        foreach (Slot pack in Group.AnimPacksFor(Slot))
        {
            JObj doc;
            try
            {
                doc = AnimPack.Read(pack.Data);
            }
            catch (Exception ex) when (Failure.IsData(ex))
            {
                Host.Log($"{pack.File}: the animations could not be read: {ex.Message}");
                continue;
            }
            names ??= [.. Group.AnimationNames()];
            AnimPack.AddNames(doc, names);
            yield return (pack, doc);
        }
    }
    public SceneGeometry? GetScene()
    {
        SceneGeometry scene = Load().View.Scene;
        return scene.TriangleCount > 0 ? scene : null;
    }
    public IReadOnlyList<AnimationClip> Clips => Load().View.Scene.Clips;
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        return Load().View.TryPose(clip, seconds, out positions, out normals);
    }
    protected override void Check(byte[] payload)
    {
        _ = GdReader.Parse(payload, Slot.Platform);
    }
    protected override void Invalidate()
    {
        _view = null;
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        GdModel gd = Model;
        SceneGeometry scene = Load().View.Scene;
        yield return PropertyRow.Of("Kind", gd.Kind switch
        {
            "gdw" => "gdw: world geometry, one mesh per visibility zone",
            "gds" => "gds: skinned model",
            "gdi" => "gdi: instanced mesh",
            _ => "gdt: tie (static prop)",
        });
        yield return PropertyRow.Of("Byte order", gd.Big ? "big-endian" : "little-endian");
        if (gd.Lods.Count > 1)
        {
            string[] choices = [.. Enumerable.Range(0, gd.Lods.Count).Select(i => LodLabel(gd, i))];
            yield return PropertyRow.Options("Shown LOD", LodLabel(gd, _lod), choices, value =>
            {
                _lod = Array.IndexOf(choices, value) is int i and >= 0 ? i : 0;
                _view = null;
            }) with { Changed = AssetChange.Scene | AssetChange.Properties };
        }
        else
        {
            yield return PropertyRow.Of("LODs", gd.Lods.Count);
        }
        yield return PropertyRow.Of("Vertices", scene.VertexCount.ToString("N0", CultureInfo.InvariantCulture));
        yield return PropertyRow.Of("Triangles", scene.TriangleCount.ToString("N0", CultureInfo.InvariantCulture));
        yield return PropertyRow.Of("Materials", string.Join(", ", gd.Materials.Select(m => m.Name)));
        int found = gd.Textures.Count(t => Group.ByHash(t.Hash, Slot) is not null);
        yield return PropertyRow.Of("Textures", $"{gd.Textures.Count} referenced, {found} found in this container, its parts or global.wad");
        if (gd.Bones.Count > 0)
        {
            yield return PropertyRow.Of("Bones", gd.Bones.Count);
        }
        if (scene.Clips.Count > 0)
        {
            yield return PropertyRow.Of("Animations", $"{scene.Clips.Count} from {string.Join(", ", Group.AnimPacksFor(Slot).Select(s => s.Name))}");
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
    private static string LodLabel(GdModel gd, int lod)
    {
        int prims = gd.Lods[lod].Count;
        return $"LOD {lod} ({prims} part{(prims == 1 ? "" : "s")})";
    }
    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Export JSON + bin",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("GD document (JSON with a .bin beside it)", "*.json")],
            SuggestedFileName = Slot.Name + ".json",
            Execute = context =>
            {
                (JObj doc, byte[] bin) = GdFile.Read(Slot.Data, Slot.Platform);
                string json = context.Path!;
                string binPath = Path.ChangeExtension(json, ".bin");
                AtomicSave.Write(json, new System.Text.UTF8Encoding(false).GetBytes(PyJson.Dumps(doc, 1)));
                AtomicSave.Write(binPath, bin);
                context.Log($"Wrote {Path.GetFileName(json)} and {Path.GetFileName(binPath)} ({Sizes.Describe(bin.Length)}) from {Slot.File}.");
            },
        };
        if (!Slot.CanWrite)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Import JSON + bin",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("GD document (JSON with a .bin beside it)", "*.json")],
            Execute = context =>
            {
                string binPath = Path.ChangeExtension(context.Path!, ".bin");
                if (!File.Exists(binPath))
                {
                    throw new FileNotFoundException($"{Path.GetFileName(binPath)} must sit beside {Path.GetFileName(context.Path!)}", binPath);
                }
                JObj doc = PyJson.Loads(File.ReadAllBytes(context.Path!)) as JObj ?? throw new InvalidDataException("the JSON document is not an object");
                Apply(context, GdFile.Write(doc, File.ReadAllBytes(binPath)), Path.GetFileName(context.Path!));
            },
        };
        yield return new AssetCommand
        {
            Title = "Replace geometry from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = context =>
            {
                using IGltfSource source = context.Host.Gltf.Open(context.Path!);
                List<GltfPrim> prims = GdImport.ReadGltf(source.Root, source.Buffers);
                (JObj doc, byte[] bin) = GdFile.Read(Slot.Data, Slot.Platform);
                (JObj newDoc, byte[] newBin) = GdImport.Import(doc, bin, prims);
                Apply(context, GdFile.Write(newDoc, newBin), $"{Path.GetFileName(context.Path!)} ({prims.Sum(p => p.Triangles.GetLength(0)):N0} triangles, first LOD replaced)");
            },
        };
    }
    private void Apply(AssetCommandContext context, byte[] rebuilt, string from)
    {
        bool same = rebuilt.AsSpan().SequenceEqual(Slot.Data);
        if (!same)
        {
            Store(rebuilt);
        }
        context.Log(same
            ? $"{from} rebuilds {Slot.File} byte for byte; nothing changed."
            : $"Rebuilt {Slot.File} from {from} ({Sizes.Describe(rebuilt.Length)}). Use File ▸ Save to write it back.");
        context.Changed = same ? AssetChange.None : Everything;
    }
}
internal sealed class AnimPackNode : SlotNode, ISceneAsset, IAnimatedAsset
{
    private (int Slot, int Group, JObj Doc, RiggedScene? View)? _view;
    public AnimPackNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Animation, Label(slot))
    {
    }
    private static string Label(Slot slot)
    {
        try
        {
            int n = J.A(AnimPack.Read(slot.Data)["animations"]).Count;
            return $"{n} animation{(n == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            return Sizes.Describe(slot.Data.Length);
        }
    }
    protected override string DescribeSize()
    {
        return Label(Slot);
    }
    private (JObj Doc, RiggedScene? View) Load()
    {
        if (_view is { } v && v.Slot == Slot.Revision && v.Group == Group.Revision)
        {
            return (v.Doc, v.View);
        }
        JObj doc = Named();
        RiggedScene? view = null;
        if (Group.MasterModel(Slot) is { } master)
        {
            try
            {
                GdModel gd = GdReader.Parse(master.Data, master.Platform);
                SceneGeometry scene = GdScene.Build(gd, master.Name, Group.Lookup(master));
                view = RiggedScene.Build(scene, gd, [(Slot, doc)]);
            }
            catch (Exception ex) when (Failure.IsData(ex))
            {
                Host.Log($"{master.File}: the model for {Slot.Name} could not be read: {ex.Message}");
            }
        }
        _view = (Slot.Revision, Group.Revision, doc, view);
        return (doc, view);
    }
    private JObj Named()
    {
        JObj doc = AnimPack.Read(Slot.Data);
        AnimPack.AddNames(doc, Group.AnimationNames());
        return doc;
    }
    public SceneGeometry? GetScene()
    {
        return Load().View?.Scene is { TriangleCount: > 0 } scene ? scene : null;
    }
    public IReadOnlyList<AnimationClip> Clips => Load().View?.Scene.Clips ?? [];
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        if (Load().View is { } view)
        {
            return view.TryPose(clip, seconds, out positions, out normals);
        }
        positions = [];
        normals = [];
        return false;
    }
    protected override void Check(byte[] payload)
    {
        _ = AnimPack.Read(payload);
    }
    protected override void Invalidate()
    {
        _view = null;
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        (JObj doc, RiggedScene? view) = Load();
        List<object?> anims = J.A(doc["animations"]);
        yield return PropertyRow.Of("Animations", anims.Count);
        int named = anims.Count(a => J.O(a).Has("full_name"));
        yield return PropertyRow.Of("Full names", $"{named} of {anims.Count} recovered from their hashes");
        Slot? master = Group.MasterModel(Slot);
        yield return PropertyRow.Of("Model", master is null ? "none: no MASTER/*.gds beside this file" : master.File);
        if (view is not null)
        {
            yield return PropertyRow.Of("Playable", $"{view.Scene.Clips.Count} clip(s) at {AnimPack.Fps.ToString(CultureInfo.InvariantCulture)} fps");
        }
        foreach (object? ao in anims)
        {
            JObj a = J.O(ao);
            string name = a.Get("full_name") as string ?? J.S(a["name"]);
            yield return PropertyRow.Of(name, $"{J.D(a["duration"]).ToString("0.###", CultureInfo.InvariantCulture)} s, {J.A(a["tracks"]).Count} tracks, hash {Py.Hex8(J.U(a["hash"]))}");
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return ExportJson(() => AnimPack.Dumps(Load().Doc));
        if (Slot.CanWrite)
        {
            yield return ImportJson(AnimPack.Write);
        }
    }
}
