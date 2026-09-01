using Harugeki.Formats.Scene;
using Omega.Plugins;
using System.Globalization;
namespace Harugeki.Formats.Nodes;

internal sealed class MeshNode : AssetNode, ISceneAsset, IDescribable
{
    private readonly RingArchive _archive;
    private readonly RingModel _model;
    private readonly RingMesh _mesh;
    private SceneGeometry? _scene;
    private int _sceneRevision = -1;
    public MeshNode(RingArchive archive, RingModel model, RingMesh mesh, string parentPath)
        : base(mesh.Name,
               $"{parentPath}/mesh/{mesh.NodeIndex:00}",
               AssetKinds.Mesh,
               $"{mesh.TriangleCount} tris, {mesh.VertexCount} verts")
    {
        _archive = archive;
        _model = model;
        _mesh = mesh;
        ChildFactory = () => _mesh.Materials.Select(m => (IAssetNode)new MaterialNode(_model, m, PathText));
    }
    public SceneGeometry? GetScene()
    {
        if (_scene is not null && _sceneRevision == _archive.Revision)
        {
            return _scene;
        }
        _sceneRevision = _archive.Revision;
        SceneGeometry whole = ModelScene.Build(_model);
        SceneMesh? mine = whole.Meshes.FirstOrDefault(m => m.Name == _mesh.Name);
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
        yield return PropertyRow.Of(
            "Colour",
            string.Join(", ", _material.Color.Select(c => c.ToString("0.###", CultureInfo.InvariantCulture))));
    }
}
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
