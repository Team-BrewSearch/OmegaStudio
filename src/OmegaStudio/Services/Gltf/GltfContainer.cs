using Omega.Plugins.Gltf;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
namespace OmegaStudio.Services.Gltf;

internal static class GltfContainer
{
    public const int Byte = 5120, UByte = 5121, Short = 5122, UShort = 5123, UInt = 5125, Float = 5126;
    private const int MaxAccessorElements = 1 << 24;
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;
    public static (JsonElement Root, byte[][] Buffers) Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] raw = File.ReadAllBytes(path);
        (JsonElement root, byte[]? glbBuffer) = Parse(raw);
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return (root, LoadBuffers(root, baseDir, glbBuffer));
    }
    private static (JsonElement Root, byte[]? Bin) Parse(byte[] raw)
    {
        if (raw.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == GlbMagic)
        {
            return ParseGlb(raw);
        }
        JsonDocument document = JsonDocument.Parse(raw);
        return (document.RootElement.Clone(), null);
    }
    private static (JsonElement Root, byte[]? Bin) ParseGlb(byte[] raw)
    {
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        if (version != 2)
        {
            throw new InvalidDataException($"GLB container version {version} is not supported (only 2).");
        }
        JsonElement? root = null;
        byte[]? bin = null;
        for (int at = 12; at + 8 <= raw.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 4));
            at += 8;
            if (length < 0 || at + length > raw.Length)
            {
                throw new InvalidDataException("GLB chunk runs past the end of the file.");
            }
            if (type == ChunkJson)
            {
                root = JsonDocument.Parse(raw.AsMemory(at, length)).RootElement.Clone();
            }
            else if (type == ChunkBin)
            {
                bin = raw.AsSpan(at, length).ToArray();
            }
            at += (length + 3) & ~3;
        }
        return root is null
            ? throw new InvalidDataException("GLB file has no JSON chunk.")
            : (root.Value, bin);
    }
    private static byte[][] LoadBuffers(JsonElement root, string baseDir, byte[]? glbBuffer)
    {
        if (!root.TryGetProperty("buffers", out JsonElement buffers))
        {
            return [];
        }
        byte[][] result = new byte[buffers.GetArrayLength()][];
        int i = 0;
        foreach (JsonElement buffer in buffers.EnumerateArray())
        {
            if (!buffer.TryGetProperty("uri", out JsonElement uri))
            {
                result[i] = glbBuffer
                    ?? throw new InvalidDataException("glTF references the GLB binary chunk, but there is none.");
            }
            else
            {
                string text = uri.GetString() ?? string.Empty;
                result[i] = text.StartsWith("data:", StringComparison.Ordinal)
                    ? DecodeDataUri(text)
                    : ReadSiblingBuffer(baseDir, text);
            }
            i++;
        }
        return result;
    }
    private static byte[] ReadSiblingBuffer(string baseDir, string uri)
    {
        string relative = Uri.UnescapeDataString(uri);
        if (relative.Length == 0
            || relative.Contains("://", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                $"glTF buffer URI '{uri}' must be a relative path beside the file.");
        }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir))
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.Ordinal)
            ? File.ReadAllBytes(full)
            : throw new InvalidDataException(
                $"glTF buffer URI '{uri}' points outside the folder the file was loaded from.");
    }
    private static byte[] DecodeDataUri(string uri)
    {
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            throw new InvalidDataException("Malformed data: URI in the glTF buffer list.");
        }
        string header = uri[..comma];
        string payload = uri[(comma + 1)..];
        return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }
    public static List<(int Node, Matrix4x4 World)> WalkScene(JsonElement root, JsonElement[] nodes)
    {
        List<int> roots = [];
        if (root.TryGetProperty("scenes", out JsonElement scenes) && scenes.GetArrayLength() > 0)
        {
            int index = root.TryGetProperty("scene", out JsonElement s) ? s.GetInt32() : 0;
            JsonElement scene = scenes[Math.Clamp(index, 0, scenes.GetArrayLength() - 1)];
            if (scene.TryGetProperty("nodes", out JsonElement list))
            {
                roots.AddRange(list.EnumerateArray().Select(n => n.GetInt32()));
            }
        }
        if (roots.Count == 0)
        {
            roots.AddRange(Enumerable.Range(0, nodes.Length));
        }
        List<(int, Matrix4x4)> flat = [];
        HashSet<int> seen = [];
        foreach (int start in roots)
        {
            Visit(start, Matrix4x4.Identity);
        }
        return flat;
        void Visit(int node, Matrix4x4 parent)
        {
            if (node < 0 || node >= nodes.Length || !seen.Add(node))
            {
                return;
            }
            Matrix4x4 world = LocalTransform(nodes[node]) * parent;
            flat.Add((node, world));
            if (nodes[node].TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement c in children.EnumerateArray())
                {
                    Visit(c.GetInt32(), world);
                }
            }
        }
    }
    private static Matrix4x4 LocalTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement m) && m.GetArrayLength() == 16)
        {
            float[] f = [.. m.EnumerateArray().Select(v => v.GetSingle())];
            return new Matrix4x4(
                f[0], f[1], f[2], f[3],
                f[4], f[5], f[6], f[7],
                f[8], f[9], f[10], f[11],
                f[12], f[13], f[14], f[15]);
        }
        Vector3 translation = Vector3.Zero, scale = Vector3.One;
        Quaternion rotation = Quaternion.Identity;
        if (node.TryGetProperty("translation", out JsonElement t))
        {
            float[] f = [.. t.EnumerateArray().Select(v => v.GetSingle())];
            translation = new Vector3(f[0], f[1], f[2]);
        }
        if (node.TryGetProperty("rotation", out JsonElement r))
        {
            float[] f = [.. r.EnumerateArray().Select(v => v.GetSingle())];
            rotation = new Quaternion(f[0], f[1], f[2], f[3]);
        }
        if (node.TryGetProperty("scale", out JsonElement s))
        {
            float[] f = [.. s.EnumerateArray().Select(v => v.GetSingle())];
            scale = new Vector3(f[0], f[1], f[2]);
        }
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }
    public static float[] ReadAccessor(JsonElement root, byte[][] buffers, int index, int expectedComponents)
    {
        JsonElement[] accessors = GltfJson.Array(root, "accessors");
        if (index < 0 || index >= accessors.Length)
        {
            throw new InvalidDataException($"glTF accessor {index} does not exist.");
        }
        JsonElement accessor = accessors[index];
        if (accessor.TryGetProperty("sparse", out _))
        {
            throw new InvalidDataException("Sparse accessors are not supported; re-export without them.");
        }
        int count = accessor.GetProperty("count").GetInt32();
        if (count is < 0 or > MaxAccessorElements)
        {
            throw new InvalidDataException(
                $"glTF accessor {index} declares {count} elements, outside the supported range.");
        }
        int componentType = accessor.GetProperty("componentType").GetInt32();
        string type = accessor.GetProperty("type").GetString() ?? "SCALAR";
        bool normalized = accessor.TryGetProperty("normalized", out JsonElement nz) && nz.GetBoolean();
        int components = type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new InvalidDataException($"glTF accessor type '{type}' is not supported."),
        };
        int componentSize = componentType switch
        {
            Byte or UByte => 1,
            Short or UShort => 2,
            UInt or Float => 4,
            _ => throw new InvalidDataException($"glTF component type {componentType} is not supported."),
        };
        float[] result = new float[count * expectedComponents];
        if (!accessor.TryGetProperty("bufferView", out JsonElement viewRef))
        {
            return result;
        }
        JsonElement[] views = GltfJson.Array(root, "bufferViews");
        int viewIndex = viewRef.GetInt32();
        if (viewIndex < 0 || viewIndex >= views.Length)
        {
            throw new InvalidDataException(
                $"glTF accessor {index} names buffer view {viewIndex}, which does not exist.");
        }
        JsonElement view = views[viewIndex];
        int bufferIndex = view.GetProperty("buffer").GetInt32();
        if (bufferIndex < 0 || bufferIndex >= buffers.Length)
        {
            throw new InvalidDataException(
                $"glTF buffer view {viewIndex} names buffer {bufferIndex}, which does not exist.");
        }
        byte[] buffer = buffers[bufferIndex];
        int viewOffset = view.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0;
        int accessorOffset = accessor.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt32() : 0;
        int packed = components * componentSize;
        int stride = view.TryGetProperty("byteStride", out JsonElement bs) ? bs.GetInt32() : packed;
        if (stride <= 0)
        {
            stride = packed;
        }
        long start = (long)viewOffset + accessorOffset;
        long reach = count == 0 ? 0L : ((long)(count - 1) * stride) + packed;
        if (viewOffset < 0 || accessorOffset < 0 || start < 0 || start + reach > buffer.Length)
        {
            throw new InvalidDataException($"glTF accessor {index} reads past the end of its buffer.");
        }
        int copy = Math.Min(components, expectedComponents);
        for (int i = 0; i < count; i++)
        {
            int at = (int)(start + ((long)i * stride));
            for (int c = 0; c < copy; c++)
            {
                result[(i * expectedComponents) + c] =
                    Component(buffer, at + (c * componentSize), componentType, normalized);
            }
        }
        return result;
    }
    private static float Component(byte[] buffer, int at, int componentType, bool normalized)
    {
        return componentType switch
        {
            Float => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at))),
            UByte => normalized ? buffer[at] / 255f : buffer[at],
            Byte => normalized ? MathF.Max((sbyte)buffer[at] / 127f, -1f) : (sbyte)buffer[at],
            UShort => Scale(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)), normalized, 65535f),
            Short => normalized
                ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(at)) / 32767f, -1f)
                : BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(at)),
            UInt => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at)),
            _ => 0f,
        };
        static float Scale(float value, bool normalized, float max)
        {
            return normalized ? value / max : value;
        }
    }
    public static byte[]? ReadBufferView(JsonElement view, byte[][] buffers)
    {
        if (!view.TryGetProperty("buffer", out JsonElement bufferRef)
            || !bufferRef.TryGetInt32(out int buffer)
            || buffer < 0 || buffer >= buffers.Length)
        {
            return null;
        }
        int offset = view.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt32() : 0;
        int length = view.TryGetProperty("byteLength", out JsonElement l) ? l.GetInt32() : 0;
        return offset < 0 || length <= 0 || offset + length > buffers[buffer].Length
            ? null
            : buffers[buffer].AsSpan(offset, length).ToArray();
    }
    public static byte[]? DecodeDataUriOrNull(string uri)
    {
        try
        {
            return DecodeDataUri(uri);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException)
        {
            return null;
        }
    }
}
