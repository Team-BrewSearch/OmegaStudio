using Omega.Plugins.Gltf;
namespace Omega.Plugins;
public interface IImageCodec
{
    ImageData Load(string path);
    ImageData Load(ReadOnlySpan<byte> bytes, string name = "");
    void Save(ImageData image, string path);
    byte[] Encode(ImageData image, string extension);
    IReadOnlyList<string> WritableExtensions { get; }
    byte[] EncodePng(ImageData image);
}
public interface IGltfCodec
{
    GltfDocument Read(string path);
    IGltfSource Open(string path);
    void Write(SceneGeometry scene, string path);
}
public interface IHostServices
{
    void Log(string message);
    IImageCodec Images { get; }
    IGltfCodec Gltf { get; }
    string PluginDirectory { get; }
    bool IsKnownPayload(ReadOnlySpan<byte> data);
}
