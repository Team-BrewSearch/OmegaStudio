using Harugeki.Formats.Gltf;
using Harugeki.Formats.Scene;
using Harugeki.Formats.Skinning;
using Omega.Plugins;
using System.Diagnostics.CodeAnalysis;
namespace Harugeki.Formats.Nodes;

internal sealed class ModelNode : SlotNode, ISceneAsset, IAnimatedAsset, IDescribable, ICommandSource
{
    private readonly string _header;
    private readonly Dictionary<RingMesh, SkinBinding?> _bindings = [];
    private RingModel _model;
    private SceneGeometry? _scene;
    private int _sceneRevision = -1;
    public ModelNode(RingNode slot, string header)
        : this(slot, header, RingModel.Parse(slot.Span))
    {
    }
    private ModelNode(RingNode slot, string header, RingModel model)
        : base(slot, $"{header} {model.Name}", stem: model.Name)
    {
        _header = header;
        _model = model;
        ChildFactory = BuildChildren;
    }
    public SceneGeometry? GetScene()
    {
        int revision = Slot.Archive.Revision;
        if (_scene is null || _sceneRevision != revision)
        {
            _scene = ModelScene.Build(_model);
            _sceneRevision = revision;
        }
        return _scene;
    }
    [AllowNull]
    public IReadOnlyList<AnimationClip> Clips { get => field ??= FindClips(); private set; }
    private List<AnimationClip> FindClips()
    {
        List<AnimationClip> clips = [];
        foreach (RingNode? sibling in Slot.Parent?.Children ?? [])
        {
            if (sibling is null || !RingAnimation.LooksLikeAnimation(sibling.Span))
            {
                continue;
            }
            try
            {
                clips.Add(AnimationNode.ToClip(
                    RingAnimation.Parse(sibling.Span, $"anim{sibling.Index:00}")));
            }
            catch (ArgumentException)
            {
            }
        }
        return clips;
    }
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return ModelPoser.TryPose(_model, _bindings, clip, seconds, out positions, out normals);
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace geometry from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.gltf", "*.glb")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        byte[] source = Slot.GetPayload();
        (byte[] blob, GltfImportReport report) = GltfImport.Apply(
            _model, source, context.Host.Gltf.Read(context.Path!));
        RingModel rebuilt = RingModel.Parse(blob);
        Slot.Replace(blob);
        Reparse(rebuilt);
        context.Log($"Imported {Path.GetFileName(context.Path!)} into {_model.Name}: {report}.");
        foreach (string warning in report.Warnings)
        {
            context.Log($"  note: {warning}");
        }
        context.Log($"  model is now {Sizes.Describe(blob.Length)}. Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Children | AssetChange.Scene
            | AssetChange.Properties | AssetChange.Dirty;
    }
    private void Reparse(RingModel model)
    {
        _model = model;
        _scene = null;
        _bindings.Clear();
        Clips = null;
        Detail = Describe(Slot);
        ChildFactory = BuildChildren;
    }
    protected override void Write(byte[] payload)
    {
        RingModel parsed = RingModel.Parse(payload);
        base.Write(payload);
        Reparse(parsed);
    }
    private IEnumerable<IAssetNode> BuildChildren()
    {
        RingModel model = _model;
        string path = Slot.PathText;
        yield return new GroupNode(
            "Meshes", $"{path}/meshes", $"{model.Meshes.Count}",
            () => model.Meshes.Select(mesh => new MeshNode(Slot.Archive, model, mesh, path)));
        yield return new GroupNode(
            "Materials", $"{path}/materials", $"{model.Meshes.Sum(m => m.Materials.Count)}",
            () => model.Meshes
                .SelectMany(m => m.Materials)
                .Select(material => new MaterialNode(model, material, path)));
        yield return new GroupNode(
            "Skeleton", $"{path}/skeleton", $"{model.Bones.Count} bones",
            () => Skeleton(model, path));
        if (model.Textures.Count > 0)
        {
            yield return new GroupNode(
                "Textures", $"{path}/textures", $"{model.Textures.Count}",
                () => model.Textures.Select((texture, i) =>
                    (IAssetNode)TextureNode.Embedded(Slot, _header, texture, model, i)));
        }
    }
    private static IEnumerable<IAssetNode> Skeleton(RingModel model, string path)
    {
        Dictionary<int, RingBone> byNode = model.Bones.ToDictionary(b => b.NodeIndex);
        Dictionary<int, List<RingBone>> children = model.Bones
            .GroupBy(b => b.ParentNodeIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        IAssetNode Build(RingBone bone)
        {
            return new BoneNode(
                bone,
                path,
                children.TryGetValue(bone.NodeIndex, out List<RingBone>? kids)
                    ? () => kids.Select(Build)
                    : null);
        }
        return model.Bones.Where(b => !byNode.ContainsKey(b.ParentNodeIndex)).Select(Build);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _model.Name);
        yield return PropertyRow.Of("Bones", _model.Bones.Count);
        yield return PropertyRow.Of("Meshes", _model.Meshes.Count);
        yield return PropertyRow.Of("Triangles", _model.Meshes.Sum(m => m.TriangleCount));
        yield return PropertyRow.Of("Embedded textures", _model.Textures.Count);
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
