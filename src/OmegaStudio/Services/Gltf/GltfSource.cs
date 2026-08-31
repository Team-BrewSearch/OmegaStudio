using Omega.Plugins;
using Omega.Plugins.Gltf;
using System.Numerics;
using System.Text.Json;
namespace OmegaStudio.Services.Gltf;

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
    public void Dispose()
    {
    }
}
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
