using Harugeki.Formats.Scene;
using Omega.Plugins;
using System.Globalization;

namespace Harugeki.Formats.Nodes;

/// <summary>
/// One mesh of a model, shown so the parts of a character can be inspected
/// separately.
///
/// <para>
/// It is an <see cref="ISceneAsset"/> in its own right, which means selecting a mesh
/// draws just that mesh in the viewport and exports just that mesh to glTF. The
/// version this replaced showed the whole model for a mesh selection, because the
/// viewport could only be handed a model.
/// </para>
/// </summary>
internal sealed class MeshNode : AssetNode, ISceneAsset, IDescribable
{
    private readonly RingModel _model;
    private readonly RingMesh _mesh;

    /// <summary>
    /// This mesh's geometry, built once. Building it expands the whole model's skin, so doing
    /// it again on every selection made clicking down a list of parts cost the model each time.
    /// </summary>
    private SceneGeometry? _scene;

    public MeshNode(RingModel model, RingMesh mesh, string parentPath)
        : base(mesh.Name,
               $"{parentPath}/mesh/{mesh.NodeIndex:00}",
               AssetKinds.Mesh,
               $"{mesh.TriangleCount} tris, {mesh.VertexCount} verts")
    {
        _model = model;
        _mesh = mesh;
        ChildFactory = () => _mesh.Materials.Select(m => (IAssetNode)new MaterialNode(_model, m, PathText));
    }

    public SceneGeometry? GetScene()
    {
        if (_scene is not null)
        {
            return _scene;
        }

        SceneGeometry whole = ModelScene.Build(_model);
        SceneMesh? mine = whole.Meshes.FirstOrDefault(m => m.Name == _mesh.Name);

        // Falling back to the whole model rather than to nothing: a duplicate mesh
        // name is possible, and showing the model is a better answer than an empty
        // viewport for a node the user just clicked.
        return _scene = mine is null
            ? whole
            : new SceneGeometry
            {
                Meshes = [mine],
                Textures = whole.Textures,
                Joints = whole.Joints,
                Lighting = whole.Lighting,
                Filtering = whole.Filtering,
            };
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _mesh.Name);
        yield return PropertyRow.Of("Triangles", _mesh.TriangleCount);
        yield return PropertyRow.Of("Draw vertices", _mesh.VertexCount);
        yield return PropertyRow.Of("Skin vertices", _mesh.SkinPositions.Length / 3);
        yield return PropertyRow.Of("Materials", string.Join(", ", _mesh.Materials.Select(m => m.Name)));
        yield return PropertyRow.Of("Per-mat tris", string.Join(", ", _mesh.TriangleCounts));
    }
}

/// <summary>One material record: a name, an optional texture, and a base colour.</summary>
internal sealed class MaterialNode : AssetNode, IDescribable
{
    private readonly RingModel _model;
    private readonly RingMaterial _material;

    public MaterialNode(RingModel model, RingMaterial material, string parentPath)
        : base(material.Name,
               $"{parentPath}/material/{material.Name}",
               AssetKinds.Material,
               TextureName(model, material))
    {
        _model = model;
        _material = material;
    }

    private static string TextureName(RingModel model, RingMaterial material)
    {
        return material.HasTexture && material.TextureIndex < model.Textures.Count
            ? model.Textures[(int)material.TextureIndex].Name
            : "untextured";
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _material.Name);
        yield return PropertyRow.Of("Texture index", _material.HasTexture ? _material.TextureIndex : "none");
        yield return PropertyRow.Of("Texture", TextureName(_model, _material));
        // Invariant, not the user's locale: this is a value read back out of the file,
        // and a decimal comma would make it look like four numbers instead of one.
        yield return PropertyRow.Of(
            "Colour",
            string.Join(", ", _material.Color.Select(c => c.ToString("0.###", CultureInfo.InvariantCulture))));
    }
}

/// <summary>One bone, nested under its parent in the skeleton sub-tree.</summary>
internal sealed class BoneNode : AssetNode, IDescribable
{
    private readonly RingBone _bone;

    public BoneNode(RingBone bone, string parentPath, Func<IEnumerable<IAssetNode>>? children)
        : base(bone.Name,
               $"{parentPath}/bone/{bone.NodeIndex:00}",
               AssetKinds.Bone,
               $"{bone.Weights.Length} weights")
    {
        _bone = bone;
        ChildFactory = children;
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name", _bone.Name);
        yield return PropertyRow.Of("Node index", _bone.NodeIndex);
        yield return PropertyRow.Of("Parent node", _bone.ParentNodeIndex);
        yield return PropertyRow.Of("Weighted verts", _bone.Weights.Length);
    }
}
