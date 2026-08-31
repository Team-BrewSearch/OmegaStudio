using Omega.Plugins;
using Omega.Plugins.Gltf;
namespace OmegaStudio.Services.Gltf;

internal sealed class GltfCodec : IGltfCodec
{
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
        GltfWriter.Write(scene, path);
    }
}
