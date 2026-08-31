using System.Numerics;
using System.Text.Json;

namespace Omega.Plugins.Gltf;

/// <summary>
/// A glTF/GLB file opened at the container level: parsed JSON, resolved buffers, and
/// accessor decoding, with nothing interpreted for you.
///
/// <para>
/// <see cref="IGltfCodec.Read"/> suits almost every importer. This is for the one that
/// does not: a format preserving detail the neutral document drops — unmerged
/// per-primitive vertex blocks, or round-trip hints written into <c>extras</c>. Sharing
/// the container plumbing keeps exactly one glTF reader in the codebase rather than a
/// second inside whichever plugin needed more.
/// </para>
/// </summary>
public interface IGltfSource : IDisposable
{
    /// <summary>The glTF JSON root.</summary>
    JsonElement Root { get; }

    /// <summary>Buffers, resolved from GLB chunks, data URIs and sibling <c>.bin</c> files.</summary>
    IReadOnlyList<byte[]> Buffers { get; }

    /// <summary>
    /// The file's materials in document order, with each base-colour image already
    /// decoded to RGBA. Decoding lives here rather than in the plugin because it
    /// needs an image codec, and the whole point of the seam is that a plugin does
    /// not have one.
    /// </summary>
    IReadOnlyList<GltfMaterial> Materials { get; }

    /// <summary>
    /// Every node reachable from the scene, paired with its accumulated world matrix.
    /// </summary>
    IReadOnlyList<(int Node, Matrix4x4 World)> WalkScene();

    /// <summary>
    /// Decodes accessor <paramref name="index"/> to floats, normalising integer
    /// component types. Throws if it does not have
    /// <paramref name="expectedComponents"/> components per element, or if it is
    /// sparse.
    /// </summary>
    float[] ReadAccessor(int index, int expectedComponents);
}

/// <summary>Small helpers for reading the glTF JSON an <see cref="IGltfSource"/> exposes.</summary>
public static class GltfJson
{
    /// <summary>
    /// The named array property, or empty when absent — glTF makes almost every
    /// top-level array optional, and the alternative is a null check at every use.
    /// </summary>
    public static JsonElement[] Array(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray()]
            : [];
    }

    /// <summary>The element's <c>name</c>, or <paramref name="fallback"/>.</summary>
    public static string Name(JsonElement element, string fallback)
    {
        return element.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? fallback
            : fallback;
    }
}
