using System.Numerics;
using System.Text.Json;
namespace Omega.Plugins.Gltf;
public interface IGltfSource : IDisposable
{
    JsonElement Root { get; }
    IReadOnlyList<byte[]> Buffers { get; }
    IReadOnlyList<GltfMaterial> Materials { get; }
    IReadOnlyList<(int Node, Matrix4x4 World)> WalkScene();
    float[] ReadAccessor(int index, int expectedComponents);
}
public static class GltfJson
{
    public static JsonElement[] Array(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()]
            : [];
    }
    public static string Name(JsonElement element, string fallback)
    {
        return element.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? fallback
            : fallback;
    }
}
