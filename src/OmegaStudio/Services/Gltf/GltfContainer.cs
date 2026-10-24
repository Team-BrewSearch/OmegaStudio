using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Omega.Plugins;
using Omega.Plugins.Gltf;
namespace OmegaStudio.Services.Gltf;
internal static class GltfContainer
{
    public const int Byte = 5120, UByte = 5121, Short = 5122, UShort = 5123, UInt = 5125, Float = 5126;
    private const int MaxAccessorElements = 1 << 24;
    public const long MaxBufferBytes = 1L << 31;
    public const long MaxDecodedBytes = 1L << 30;
    private const uint GlbMagic = 0x46546C67;
    private const uint ChunkJson = 0x4E4F534A;
    private const uint ChunkBin = 0x004E4942;
    public static (JsonElement Root, byte[][] Buffers, string BaseDir) Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (new FileInfo(path).Length > MaxBufferBytes)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is larger than the {Sizes.Describe(MaxBufferBytes)} a glTF import may read.");
        }
        byte[] raw = File.ReadAllBytes(path);
        (JsonElement root, byte[]? glbBuffer) = Parse(raw);
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        byte[][] buffers = LoadBuffers(root, baseDir, glbBuffer);
        RequireDecodeBudget(root);
        return (root, buffers, baseDir);
    }
    private static void RequireDecodeBudget(JsonElement root)
    {
        long total = 0;
        foreach (JsonElement accessor in GltfJson.Array(root, "accessors"))
        {
            long count = accessor.TryGetProperty("count", out JsonElement c) && c.TryGetInt64(out long n) ? n : 0;
            int components = (accessor.TryGetProperty("type", out JsonElement t) ? t.GetString() : "SCALAR") switch
            {
                "VEC2" => 2,
                "VEC3" or "VEC4" => 4,
                "MAT4" => 16,
                _ => 1,
            };
            total = checked(total + (Math.Max(0, count) * components * sizeof(float)));
            if (total > MaxDecodedBytes)
            {
                throw new InvalidDataException(
                    $"the glTF accessors decode to more than the {Sizes.Describe(MaxDecodedBytes)} an import may hold");
            }
        }
    }
    private static (JsonElement Root, byte[]? Bin) Parse(byte[] raw)
    {
        if (raw.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == GlbMagic)
        {
            return ParseGlb(raw);
        }
        using JsonDocument document = JsonDocument.Parse(raw);
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
                using JsonDocument json = JsonDocument.Parse(raw.AsMemory(at, length));
                root = json.RootElement.Clone();
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
        long total = 0;
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
                    : ReadSibling(baseDir, text, "buffer");
            }
            total += result[i].LongLength;
            if (total > MaxBufferBytes)
            {
                throw new InvalidDataException(
                    $"the glTF buffers add up to more than the {Sizes.Describe(MaxBufferBytes)} an import may read");
            }
            i++;
        }
        return result;
    }
    public static byte[] ReadSibling(string baseDir, string uri, string what)
    {
        string relative = Uri.UnescapeDataString(uri);
        if (relative.Length == 0
            || relative.Contains("://", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException(
                $"glTF {what} URI '{uri}' must be a relative path beside the file.");
        }
        string full;
        try
        {
            full = ContainedPath.Resolve(baseDir, relative);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"glTF {what} URI '{uri}' points outside the folder the file was loaded from: {ex.Message}", ex);
        }
        if (!File.Exists(full))
        {
            throw new InvalidDataException($"glTF {what} '{relative}' is missing from the folder the file was loaded from.");
        }
        if (new FileInfo(full).Length > MaxBufferBytes)
        {
            throw new InvalidDataException($"glTF {what} '{relative}' is larger than an import may read.");
        }
        return File.ReadAllBytes(full);
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
        int[] parent = new int[nodes.Length];
        Array.Fill(parent, -1);
        for (int node = 0; node < nodes.Length; node++)
        {
            if (!nodes[node].TryGetProperty("children", out JsonElement kids))
            {
                continue;
            }
            foreach (JsonElement kid in kids.EnumerateArray())
            {
                int child = kid.GetInt32();
                if (child < 0 || child >= nodes.Length)
                {
                    throw new InvalidDataException($"glTF node {node} names child {child}, which does not exist.");
                }
                if (parent[child] >= 0 || child == node)
                {
                    throw new InvalidDataException($"glTF node {child} has more than one parent.");
                }
                parent[child] = node;
            }
        }
        for (int node = 0; node < nodes.Length; node++)
        {
            int steps = 0;
            for (int up = parent[node]; up >= 0; up = parent[up])
            {
                if (++steps > nodes.Length)
                {
                    throw new InvalidDataException($"glTF node {node} is part of a parent cycle.");
                }
            }
        }
        List<int> roots = [];
        if (root.TryGetProperty("scenes", out JsonElement scenes) && scenes.GetArrayLength() > 0)
        {
            int index = root.TryGetProperty("scene", out JsonElement s) ? s.GetInt32() : 0;
            if (index < 0 || index >= scenes.GetArrayLength())
            {
                throw new InvalidDataException($"glTF names scene {index}, and the file has {scenes.GetArrayLength()}.");
            }
            if (scenes[index].TryGetProperty("nodes", out JsonElement list))
            {
                roots.AddRange(list.EnumerateArray().Select(n => n.GetInt32()));
            }
        }
        else
        {
            roots.AddRange(Enumerable.Range(0, nodes.Length).Where(node => parent[node] < 0));
        }
        List<(int, Matrix4x4)> flat = [];
        HashSet<int> seen = [];
        Stack<(int Node, Matrix4x4 Parent)> pending = new();
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            pending.Push((roots[i], Matrix4x4.Identity));
        }
        while (pending.TryPop(out (int Node, Matrix4x4 Parent) next))
        {
            int node = next.Node;
            if (node < 0 || node >= nodes.Length || !seen.Add(node))
            {
                continue;
            }
            Matrix4x4 world = LocalTransform(nodes[node]) * next.Parent;
            flat.Add((node, world));
            if (nodes[node].TryGetProperty("children", out JsonElement children))
            {
                int[] kids = [.. children.EnumerateArray().Select(child => child.GetInt32())];
                for (int k = kids.Length - 1; k >= 0; k--)
                {
                    pending.Push((kids[k], world));
                }
            }
        }
        return flat;
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
    private readonly record struct Layout(byte[] Buffer, long Start, int Stride, int Count, int Components, int ComponentType, int ComponentSize, bool Normalized);
    public static int[] ReadIntegers(JsonElement root, byte[][] buffers, int index, int expectedComponents)
    {
        if (!IsInteger(root, index))
        {
            throw new InvalidDataException($"glTF accessor {index} holds indices that are not unsigned integers.");
        }
        if (Locate(root, buffers, index) is not { } layout)
        {
            return [];
        }
        int[] into = new int[checked((long)layout.Count * expectedComponents)];
        int copy = Math.Min(layout.Components, expectedComponents);
        for (int i = 0; i < layout.Count; i++)
        {
            int at = (int)(layout.Start + ((long)i * layout.Stride));
            for (int c = 0; c < copy; c++)
            {
                ReadOnlySpan<byte> raw = layout.Buffer.AsSpan(at + (c * layout.ComponentSize));
                uint value = layout.ComponentType switch
                {
                    UByte => raw[0],
                    UShort => BinaryPrimitives.ReadUInt16LittleEndian(raw),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(raw),
                };
                if (value > int.MaxValue)
                {
                    throw new InvalidDataException($"glTF accessor {index} holds {value}, past what an index can be.");
                }
                into[(i * expectedComponents) + c] = (int)value;
            }
        }
        return into;
    }
    public static float[] ReadAccessor(JsonElement root, byte[][] buffers, int index, int expectedComponents)
    {
        if (expectedComponents < 1 || expectedComponents > 16)
        {
            throw new InvalidDataException($"glTF accessor {index} was asked for {expectedComponents} components.");
        }
        if (Locate(root, buffers, index) is not { } layout)
        {
            return [];
        }
        float[] result = new float[checked((long)layout.Count * expectedComponents)];
        int copy = Math.Min(layout.Components, expectedComponents);
        for (int i = 0; i < layout.Count; i++)
        {
            int at = (int)(layout.Start + ((long)i * layout.Stride));
            for (int c = 0; c < copy; c++)
            {
                float value = Component(layout.Buffer, at + (c * layout.ComponentSize), layout.ComponentType, layout.Normalized);
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException($"glTF accessor {index} holds a value that is not a finite number.");
                }
                result[(i * expectedComponents) + c] = value;
            }
        }
        return result;
    }
    private static Layout? Locate(JsonElement root, byte[][] buffers, int index)
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
        if (!accessor.TryGetProperty("bufferView", out JsonElement viewRef))
        {
            return null;
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
        long viewOffset = view.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt64() : 0;
        long viewLength = view.GetProperty("byteLength").GetInt64();
        long accessorOffset = accessor.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt64() : 0;
        int packed = components * componentSize;
        int stride = view.TryGetProperty("byteStride", out JsonElement bs) ? bs.GetInt32() : packed;
        if (!Fits(viewOffset, viewLength, buffer.LongLength))
        {
            throw new InvalidDataException($"glTF buffer view {viewIndex} is out of range of its buffer.");
        }
        if (accessorOffset < 0 || stride < packed || stride % componentSize != 0 || accessorOffset % componentSize != 0)
        {
            throw new InvalidDataException($"glTF accessor {index} has an invalid offset or stride.");
        }
        long reach = count == 0 ? 0L : checked(((long)(count - 1) * stride) + packed);
        if (!Fits(accessorOffset, reach, viewLength))
        {
            throw new InvalidDataException($"glTF accessor {index} reads past the end of its buffer view.");
        }
        long start = viewOffset + accessorOffset;
        return new Layout(buffer, start, stride, count, components, componentType, componentSize, normalized);
    }
    public static bool IsInteger(JsonElement root, int index)
    {
        JsonElement[] accessors = GltfJson.Array(root, "accessors");
        return index >= 0 && index < accessors.Length
            && accessors[index].GetProperty("componentType").GetInt32() is UByte or UShort or UInt
            && !(accessors[index].TryGetProperty("normalized", out JsonElement nz) && nz.GetBoolean());
    }
    public static string TypeOf(JsonElement root, int index)
    {
        JsonElement[] accessors = GltfJson.Array(root, "accessors");
        return index >= 0 && index < accessors.Length
            ? accessors[index].GetProperty("type").GetString() ?? "SCALAR"
            : "SCALAR";
    }
    private static bool Fits(long offset, long length, long limit) =>
        offset >= 0 && length >= 0 && offset <= limit && length <= limit - offset;
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
        long offset = view.TryGetProperty("byteOffset", out JsonElement o) ? o.GetInt64() : 0;
        long length = view.TryGetProperty("byteLength", out JsonElement l) ? l.GetInt64() : 0;
        return length <= 0 || !Fits(offset, length, buffers[buffer].LongLength)
            ? null
            : buffers[buffer].AsSpan((int)offset, (int)length).ToArray();
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
