using System.Numerics;
using System.Text.Json;
namespace Omega.Plugins.Gltf;
/// <summary>A glTF or GLB file held open, so a caller can read only the parts it wants instead of parsing everything.</summary>
public interface IGltfSource : IDisposable
{
    /// <summary>The file&apos;s JSON, at the top level.</summary>
    JsonElement Root { get; }
    /// <summary>The binary buffers the accessors read from.</summary>
    IReadOnlyList<byte[]> Buffers { get; }
    /// <summary>The materials, already decoded.</summary>
    IReadOnlyList<GltfMaterial> Materials { get; }
    /// <summary>Walks the default scene, composing each node&apos;s transform with its parents.</summary>
    /// <returns>Every node index reached, paired with its world transform.</returns>
    IReadOnlyList<(int Node, Matrix4x4 World)> WalkScene();
    /// <summary>Reads one accessor as floats, converting from whatever component type it stores.</summary>
    /// <param name="index">Accessor index.</param>
    /// <param name="expectedComponents">Components per element the caller expects, such as three for a position.</param>
    /// <returns>The values, flattened.</returns>
    float[] ReadAccessor(int index, int expectedComponents);
}
/// <summary>The two glTF JSON lookups every reader repeats, each answering with a default instead of throwing on a missing or mistyped property.</summary>
public static class GltfJson
{
    /// <summary>Reads a named array property.</summary>
    /// <param name="parent">Object to read from.</param>
    /// <param name="name">Property name.</param>
    /// <returns>Its elements, or none when the property is absent or not an array.</returns>
    public static JsonElement[] Array(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()]
            : [];
    }
    /// <summary>Reads the conventional name property.</summary>
    /// <param name="element">Object to read from.</param>
    /// <param name="fallback">What to return when there is no usable name.</param>
    /// <returns>The name, or the fallback.</returns>
    public static string Name(JsonElement element, string fallback)
    {
        return element.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? fallback
            : fallback;
    }
}
