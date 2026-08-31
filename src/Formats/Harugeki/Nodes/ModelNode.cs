using Harugeki.Formats.Gltf;
using Harugeki.Formats.Scene;
using Harugeki.Formats.Skinning;
using Omega.Plugins;
using System.Diagnostics.CodeAnalysis;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// A model blob: the node that fills the viewport, the animation pane and the
/// skeleton sub-tree.
///
/// <para>
/// The one node in this plugin that implements most of the contract at once —
/// <see cref="ISceneAsset"/> for the viewport and glTF export,
/// <see cref="IAnimatedAsset"/> for playback, <see cref="ICommandSource"/> for the
/// glTF import that has no generic equivalent, and <see cref="IBinaryAsset"/> from
/// its base for raw extract and replace.
/// </para>
/// </summary>
internal sealed class ModelNode : SlotNode, ISceneAsset, IAnimatedAsset, IDescribable, ICommandSource
{
    private readonly string _header;

    /// <summary>
    /// Resolved skin bindings for this model's meshes, kept for the life of the parse.
    /// Rebuilding one is the expensive half of posing a frame, and nothing about it changes
    /// until the model is reparsed — see <see cref="ModelPoser.TryPose"/>.
    /// </summary>
    private readonly Dictionary<RingMesh, SkinBinding?> _bindings = [];

    private RingModel _model;
    private SceneGeometry? _scene;

    public ModelNode(RingNode slot, string header)
        : this(slot, header, RingModel.Parse(slot.Span))
    {
    }

    private ModelNode(RingNode slot, string header, RingModel model)
        : base(slot, $"{header} {model.Name}")
    {
        _header = header;
        _model = model;
        ChildFactory = BuildChildren;
    }

    /// <summary>
    /// The renderable geometry, built once and kept.
    ///
    /// <para>
    /// Cached because the host asks for it on selection and again after any edit, and
    /// building it walks every vertex of the model to expand the skin. Invalidated by
    /// <see cref="Reparse"/>, which is the only thing that can make it stale.
    /// </para>
    /// </summary>
    public SceneGeometry? GetScene()
    {
        return _scene ??= ModelScene.Build(_model);
    }

    // ---- animation --------------------------------------------------------

    /// <summary>
    /// Clips stored beside this model in the same container.
    ///
    /// <para>
    /// A clip is a sibling slot, not a child of the model, because the format keeps
    /// them separate — which is why finding them is this plugin's job and not
    /// something the host could work out.
    /// </para>
    /// </summary>
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
                // A sibling that only looks like a clip. Skip it.
            }
        }

        return clips;
    }

    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return ModelPoser.TryPose(_model, _bindings, clip, seconds, out positions, out normals);
    }

    // ---- commands ---------------------------------------------------------

    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace Geometry from glTF…",
            Kind = AssetCommandKind.OpenFile,
            Filter = new FileFilter("glTF model", "*.gltf", "*.glb"),
            Execute = Import,
        };
    }

    /// <summary>
    /// Replaces the model's geometry with a glTF file's.
    ///
    /// <para>
    /// The model is kept as the template — skeleton, materials and everything the
    /// parser does not interpret survive — so the archive's animations still drive the
    /// result. See <see cref="GltfImport"/> for what that costs and what it buys.
    /// </para>
    /// </summary>
    private void Import(AssetCommandContext context)
    {
        byte[] source = Slot.GetPayload();
        (byte[] blob, GltfImportReport report) = GltfImport.Apply(
            _model, source, context.Host.Gltf.Read(context.Path!));

        // Prove it parses before committing: a model that cannot be read back is one
        // the viewport and every later edit would choke on.
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

    /// <summary>
    /// Points this node at a freshly parsed model after its bytes changed, and drops
    /// everything derived from the old one.
    /// </summary>
    private void Reparse(RingModel model)
    {
        _model = model;
        _scene = null;
        _bindings.Clear();
        Clips = null;
        Detail = Describe(Slot);
        ChildFactory = BuildChildren;
    }

    /// <summary>
    /// Takes a raw replacement, but only one that is still a model.
    ///
    /// <para>
    /// Parsed before it is committed, not after. Writing first and parsing second left a
    /// replacement that was not a model already in the archive and already marked dirty, with
    /// this node still pointing at the old parse — so the viewport went on showing geometry the
    /// file no longer held, and File ▸ Save wrote the unreadable bytes out.
    /// </para>
    /// </summary>
    protected override void Write(byte[] payload)
    {
        RingModel parsed = RingModel.Parse(payload);
        base.Write(payload);
        Reparse(parsed);
    }

    // ---- tree -------------------------------------------------------------

    private IEnumerable<IAssetNode> BuildChildren()
    {
        RingModel model = _model;
        string path = Slot.PathText;

        yield return new GroupNode(
            "Meshes", $"{path}/meshes", $"{model.Meshes.Count}",
            () => model.Meshes.Select(mesh => new MeshNode(model, mesh, path)));

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

    /// <summary>Nests bones by parent index (parent = Related - 1).</summary>
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
