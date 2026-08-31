using Omega.Plugins;
using Omega.Plugins.Gltf;
using System.Numerics;
using System.Text.Json;

namespace OmegaStudio.Services.Gltf;

/// <summary>
/// A glTF file exposed at the container level, for the importer that needs the file
/// as authored rather than the flattened <see cref="GltfDocument"/>.
///
/// <para>
/// Everything expensive is done once in the constructor — buffers resolved, the
/// scene walked, materials decoded — because an importer reads all three and would
/// otherwise pay for the walk per primitive.
/// </para>
/// </summary>
internal sealed class GltfSource : IGltfSource
{
    private readonly byte[][] _buffers;

    public GltfSource(string path)
    {
        (JsonElement root, byte[][] buffers) = GltfContainer.Load(path);
        Root = root;
        _buffers = buffers;
        Buffers = buffers;
        Materials = GltfMaterials.Read(root, buffers);
    }

    public JsonElement Root { get; }

    public IReadOnlyList<byte[]> Buffers { get; }

    public IReadOnlyList<GltfMaterial> Materials { get; }

    public IReadOnlyList<(int Node, Matrix4x4 World)> WalkScene()
    {
        return GltfContainer.WalkScene(Root, GltfJson.Array(Root, "nodes"));
    }

    public float[] ReadAccessor(int index, int expectedComponents)
    {
        return GltfContainer.ReadAccessor(Root, _buffers, index, expectedComponents);
    }

    /// <summary>
    /// Nothing to release: the JSON is cloned and the buffers are plain arrays. The
    /// interface is still <see cref="IDisposable"/> so that a future implementation
    /// backed by a memory-mapped file does not become a breaking change.
    /// </summary>
    public void Dispose()
    {
    }
}

/// <summary>Decodes a glTF's materials and their base-colour images.</summary>
internal static class GltfMaterials
{
    public static IReadOnlyList<GltfMaterial> Read(JsonElement root, byte[][] buffers)
    {
        JsonElement[] materials = GltfJson.Array(root, "materials");
        JsonElement[] textures = GltfJson.Array(root, "textures");
        JsonElement[] images = GltfJson.Array(root, "images");
        JsonElement[] views = GltfJson.Array(root, "bufferViews");

        List<GltfMaterial> result = new(materials.Length);
        for (int i = 0; i < materials.Length; i++)
        {
            result.Add(new GltfMaterial
            {
                Name = GltfJson.Name(materials[i], $"Material{i:00}"),
                Image = TryDecodeImage(materials[i], textures, images, views, buffers),
            });
        }

        return result;
    }

    /// <summary>
    /// Decodes a material's base-colour image, or returns null.
    ///
    /// <para>
    /// A material without an image, an image the codec cannot read, and an image
    /// referenced through a URI pointing at a file that is not there are all normal
    /// enough in the wild that they are answered with null rather than an exception:
    /// an importer that wanted the pixels has its own fallback, and one that did not
    /// should not fail over a texture it was never going to look at.
    /// </para>
    /// </summary>
    private static ImageData? TryDecodeImage(
        JsonElement material, JsonElement[] textures, JsonElement[] images,
        JsonElement[] views, byte[][] buffers)
    {
        if (!material.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr)
            || !pbr.TryGetProperty("baseColorTexture", out JsonElement baseColor)
            || !baseColor.TryGetProperty("index", out JsonElement textureRef)
            || !textureRef.TryGetInt32(out int textureIndex)
            || textureIndex < 0 || textureIndex >= textures.Length
            || !textures[textureIndex].TryGetProperty("source", out JsonElement sourceRef)
            || !sourceRef.TryGetInt32(out int imageIndex)
            || imageIndex < 0 || imageIndex >= images.Length)
        {
            return null;
        }

        byte[]? encoded = ReadImageBytes(images[imageIndex], views, buffers);
        if (encoded is null)
        {
            return null;
        }

        try
        {
            return ImageIO.Instance.Load(encoded, GltfJson.Name(images[imageIndex], $"image{imageIndex}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[]? ReadImageBytes(JsonElement image, JsonElement[] views, byte[][] buffers)
    {
        if (image.TryGetProperty("bufferView", out JsonElement viewRef)
            && viewRef.TryGetInt32(out int view)
            && view >= 0 && view < views.Length)
        {
            return GltfContainer.ReadBufferView(views[view], buffers);
        }

        if (image.TryGetProperty("uri", out JsonElement uri) && uri.ValueKind == JsonValueKind.String)
        {
            string? text = uri.GetString();
            if (text is not null && text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return GltfContainer.DecodeDataUriOrNull(text);
            }
        }

        return null;
    }
}
