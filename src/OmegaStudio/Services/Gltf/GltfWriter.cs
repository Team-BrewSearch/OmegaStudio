using Omega.Plugins;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace OmegaStudio.Services.Gltf;

internal static class GltfWriter
{
    private const int Float = 5126, UShort = 5123, UInt = 5125;
    public static void Write(SceneGeometry scene, string path)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrEmpty(path);
        scene.Validate();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using Builder builder = new(scene);
        JsonObject gltf = builder.Build();
        if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        {
            WriteGlb(path, gltf, builder.Binary);
        }
        else
        {
            gltf["buffers"] = new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(builder.Binary),
                    ["byteLength"] = builder.Binary.Length,
                },
            };
            File.WriteAllText(
                path,
                gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
    private sealed class Builder : IDisposable
    {
        private readonly SceneGeometry _scene;
        private readonly MemoryStream _bin = new();
        private readonly JsonArray _accessors = [];
        private readonly JsonArray _views = [];
        private readonly JsonArray _nodes = [];
        private readonly JsonArray _meshes = [];
        private readonly JsonArray _materials = [];
        private readonly JsonArray _images = [];
        private readonly JsonArray _textures = [];
        private readonly JsonArray _sceneNodes = [];
        private int[] _jointNodes = [];
        private int _skin = -1;
        public Builder(SceneGeometry scene)
        {
            _scene = scene;
        }
        public byte[] Binary => _bin.ToArray();
        public void Dispose()
        {
            _bin.Dispose();
        }
        public JsonObject Build()
        {
            WriteTextures();
            WriteSkeleton();
            foreach (SceneMesh mesh in _scene.Meshes)
            {
                WriteMesh(mesh);
            }
            JsonObject gltf = new()
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Omega Studio" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = _sceneNodes } },
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
                ["accessors"] = _accessors,
                ["bufferViews"] = _views,
            };
            if (_materials.Count > 0)
            {
                gltf["materials"] = _materials;
            }
            if (_images.Count > 0)
            {
                gltf["images"] = _images;
                gltf["textures"] = _textures;
                int filter = _scene.Filtering == TextureFiltering.Nearest ? 9728 : 9729;
                gltf["samplers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["magFilter"] = filter,
                        ["minFilter"] = filter,
                        ["wrapS"] = 10497,
                        ["wrapT"] = 10497,
                    },
                };
            }
            if (_skin >= 0)
            {
                gltf["skins"] = BuildSkins();
            }
            return gltf;
        }
        private int View(byte[] bytes)
        {
            int offset = (int)_bin.Length;
            _bin.Write(bytes, 0, bytes.Length);
            while (_bin.Length % 4 != 0)
            {
                _bin.WriteByte(0);
            }
            _views.Add(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = bytes.Length,
            });
            return _views.Count - 1;
        }
        private int Accessor(Array data, int components, int componentType, bool minMax = false)
        {
            int length = Buffer.ByteLength(data);
            byte[] bytes = new byte[length];
            Buffer.BlockCopy(data, 0, bytes, 0, length);
            int count = data.Length / components;
            JsonObject accessor = new()
            {
                ["bufferView"] = View(bytes),
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = components switch
                {
                    1 => "SCALAR",
                    2 => "VEC2",
                    3 => "VEC3",
                    4 => "VEC4",
                    _ => "MAT4",
                },
            };
            if (minMax && data is float[] values)
            {
                (JsonArray low, JsonArray high) = Bounds(values, components, count);
                accessor["min"] = low;
                accessor["max"] = high;
            }
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }
        private static (JsonArray Low, JsonArray High) Bounds(float[] values, int components, int count)
        {
            float[] low = new float[components];
            float[] high = new float[components];
            Array.Fill(low, float.MaxValue);
            Array.Fill(high, float.MinValue);
            for (int i = 0; i < count; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    low[c] = MathF.Min(low[c], values[(i * components) + c]);
                    high[c] = MathF.Max(high[c], values[(i * components) + c]);
                }
            }
            return (new JsonArray([.. low.Select(v => JsonValue.Create(v))]),
                    new JsonArray([.. high.Select(v => JsonValue.Create(v))]));
        }
        private void WriteTextures()
        {
            for (int i = 0; i < _scene.Textures.Count; i++)
            {
                _images.Add(new JsonObject
                {
                    ["name"] = _scene.Textures[i].Name,
                    ["mimeType"] = "image/png",
                    ["bufferView"] = View(ImageIO.EncodePng(_scene.Textures[i])),
                });
                _textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = _images.Count - 1 });
            }
        }
        private void WriteSkeleton()
        {
            IReadOnlyList<SceneJoint> joints = _scene.Joints;
            if (joints.Count == 0)
            {
                return;
            }
            Matrix4x4[] world = new Matrix4x4[joints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                world[i] = MatrixOps.ToMatrix(joints[i].Bind);
            }
            _jointNodes = new int[joints.Count];
            for (int i = 0; i < joints.Count; i++)
            {
                _nodes.Add(new JsonObject { ["name"] = joints[i].Name });
                _jointNodes[i] = _nodes.Count - 1;
            }
            for (int i = 0; i < joints.Count; i++)
            {
                int parent = joints[i].ParentIndex;
                bool nested = parent >= 0 && parent < joints.Count && parent != i;
                Matrix4x4 local = nested
                    ? world[i] * MatrixOps.InvertOrIdentity(world[parent])
                    : world[i];
                if (nested)
                {
                    JsonObject node = (JsonObject)_nodes[_jointNodes[parent]]!;
                    (node["children"] ??= new JsonArray()).AsArray().Add(_jointNodes[i]);
                }
                else
                {
                    _sceneNodes.Add(_jointNodes[i]);
                }
                ((JsonObject)_nodes[_jointNodes[i]]!)["matrix"] = MatrixArray(local);
            }
            _skin = 0;
        }
        private JsonArray BuildSkins()
        {
            IReadOnlyList<SceneJoint> joints = _scene.Joints;
            float[] inverseBind = new float[joints.Count * 16];
            for (int i = 0; i < joints.Count; i++)
            {
                MatrixOps.CopyTo(
                    MatrixOps.InvertOrIdentity(MatrixOps.ToMatrix(joints[i].Bind)), inverseBind, i * 16);
            }
            return
            [
                new JsonObject
                {
                    ["inverseBindMatrices"] = Accessor(inverseBind, 16, Float),
                    ["joints"] = new JsonArray([.. _jointNodes.Select(j => JsonValue.Create(j))]),
                },
            ];
        }
        private void WriteMesh(SceneMesh mesh)
        {
            if (mesh.VertexCount == 0 || mesh.Submeshes.Count == 0)
            {
                return;
            }
            bool skinned = _skin >= 0 && mesh.Joints is not null && mesh.Weights is not null;
            JsonObject attributes = BuildAttributes(mesh, skinned);
            JsonArray primitives = [];
            foreach (SceneSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Count == 0)
                {
                    continue;
                }
                primitives.Add(BuildPrimitive(mesh, submesh, attributes));
            }
            if (primitives.Count == 0)
            {
                return;
            }
            _meshes.Add(new JsonObject { ["name"] = mesh.Name, ["primitives"] = primitives });
            JsonObject node = new() { ["name"] = mesh.Name, ["mesh"] = _meshes.Count - 1 };
            if (skinned)
            {
                node["skin"] = _skin;
            }
            _nodes.Add(node);
            _sceneNodes.Add(_nodes.Count - 1);
        }
        private JsonObject BuildAttributes(SceneMesh mesh, bool skinned)
        {
            int count = mesh.VertexCount;
            JsonObject attributes = new()
            {
                ["POSITION"] = Accessor(mesh.Positions, 3, Float, minMax: true),
            };
            if (mesh.Normals.Length == count * 3)
            {
                attributes["NORMAL"] = Accessor(mesh.Normals, 3, Float);
            }
            if (mesh.Uvs.Length == count * 2)
            {
                attributes["TEXCOORD_0"] = Accessor(mesh.Uvs, 2, Float);
            }
            if (mesh.Colors.Length == count * 4)
            {
                float[] colors = new float[count * 4];
                for (int i = 0; i < colors.Length; i++)
                {
                    colors[i] = mesh.Colors[i] / 255f;
                }
                attributes["COLOR_0"] = Accessor(colors, 4, Float);
            }
            if (skinned)
            {
                ushort[] joints = new ushort[count * 4];
                for (int i = 0; i < joints.Length; i++)
                {
                    joints[i] = (ushort)Math.Clamp(mesh.Joints![i], 0, ushort.MaxValue);
                }
                attributes["JOINTS_0"] = Accessor(joints, 4, UShort);
                attributes["WEIGHTS_0"] = Accessor(mesh.Weights!, 4, Float);
            }
            return attributes;
        }
        private JsonObject BuildPrimitive(SceneMesh mesh, SceneSubmesh submesh, JsonObject attributes)
        {
            uint[] indices = new uint[submesh.Count];
            for (int i = 0; i < submesh.Count; i++)
            {
                indices[i] = (uint)(mesh.Indices is { } source
                    ? source[submesh.First + i]
                    : submesh.First + i);
            }
            JsonObject primitive = new()
            {
                ["attributes"] = attributes.DeepClone(),
                ["indices"] = Accessor(indices, 1, UInt),
                ["mode"] = 4,
                ["material"] = Material(submesh),
            };
            if (submesh.Extras is { Count: > 0 } extras)
            {
                JsonObject bag = [];
                foreach ((string key, JsonNode value) in extras)
                {
                    bag[key] = value.DeepClone();
                }
                primitive["extras"] = bag;
            }
            return primitive;
        }
        private int Material(SceneSubmesh submesh)
        {
            JsonObject pbr = new()
            {
                ["baseColorFactor"] = new JsonArray(
                [
                    JsonValue.Create(1.0), JsonValue.Create(1.0),
                    JsonValue.Create(1.0), JsonValue.Create(1.0),
                ]),
                ["metallicFactor"] = 0.0,
                ["roughnessFactor"] = 1.0,
            };
            if (submesh.TextureIndex >= 0 && submesh.TextureIndex < _textures.Count)
            {
                pbr["baseColorTexture"] = new JsonObject { ["index"] = submesh.TextureIndex };
            }
            JsonObject material = new()
            {
                ["name"] = submesh.Name,
                ["pbrMetallicRoughness"] = pbr,
                ["doubleSided"] = true,
            };
            if (submesh.AlphaClip)
            {
                material["alphaMode"] = "MASK";
                material["alphaCutoff"] = 0.5;
            }
            _materials.Add(material);
            return _materials.Count - 1;
        }
        private static JsonArray MatrixArray(in Matrix4x4 m)
        {
            float[] flat = new float[16];
            MatrixOps.CopyTo(m, flat);
            return new JsonArray([.. flat.Select(v => JsonValue.Create(v))]);
        }
    }
    private static void WriteGlb(string path, JsonObject gltf, byte[] binary)
    {
        gltf["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };
        byte[] json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        int jsonPadded = Align4(json.Length);
        int binPadded = Align4(binary.Length);
        int total = 12 + 8 + jsonPadded + (binary.Length > 0 ? 8 + binPadded : 0);
        using FileStream file = File.Create(path);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)total);
        file.Write(header);
        WriteChunk(file, 0x4E4F534A, json, jsonPadded, 0x20);
        if (binary.Length > 0)
        {
            WriteChunk(file, 0x004E4942, binary, binPadded, 0x00);
        }
    }
    private static void WriteChunk(Stream file, uint type, byte[] payload, int padded, byte pad)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], type);
        file.Write(header);
        file.Write(payload);
        for (int i = payload.Length; i < padded; i++)
        {
            file.WriteByte(pad);
        }
    }
    private static int Align4(int length)
    {
        return (length + 3) & ~3;
    }
}
