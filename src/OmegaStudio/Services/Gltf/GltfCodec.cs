using Omega.Plugins;
using Omega.Plugins.Gltf;
namespace OmegaStudio.Services.Gltf;
internal sealed class GltfCodec : IGltfCodec
{
    private static long ExportBudget => MemoryBudget.Export;
    public static GltfCodec Instance { get; } = new();
    public GltfDocument Read(string path)
    {
        return GltfReader.Read(path);
    }
    public IGltfSource Open(string path)
    {
        return new GltfSource(path);
    }
    public void Write(SceneGeometry scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long held = scene.Textures.Sum(texture => (long)texture.Rgba.Length);
        foreach (SceneMesh mesh in scene.Meshes)
        {
            held += mesh.Colors.Length + (sizeof(float) * ((long)mesh.Positions.Length + mesh.Normals.Length
                + mesh.Uvs.Length + (mesh.Indices?.Length ?? 0) + (mesh.Joints?.Length ?? 0) + (mesh.Weights?.Length ?? 0)));
        }
        if (held > ExportBudget)
        {
            throw new InvalidOperationException(
                $"this scene needs about {held / 1048576L} MB while it writes, past the "
                + $"{ExportBudget / 1048576L} MB an export may hold. Export it in pieces, or export a "
                + "smaller scene.");
        }
        GltfWriter.Write(scene, path);
    }
}
