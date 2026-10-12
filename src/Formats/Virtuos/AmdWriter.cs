using System.Buffers.Binary;
using Omega.Plugins;
using Omega.Plugins.Gltf;
using Virtuos.Formats.Gltf;
namespace Virtuos.Formats;
public static class AmdWriter
{
    private const int Nop = 0x00;
    private const int MtxRestore = 0x14;
    private const int MtxPush = 0x11;
    private const int MtxPop = 0x12;
    private const int Color = 0x20;
    private const int Normal = 0x21;
    private const int Texcoord = 0x22;
    private const int Vtx16 = 0x23;
    private const int TeximageParam = 0x2A;
    private const int PlttBase = 0x2B;
    private const int BeginVtxs = 0x40;
    private const int EndVtxs = 0x41;
    private const float Rounding = 4f / 4096f;
    public static byte[] Build(
        ReadOnlySpan<byte> template, IReadOnlyList<TrkImportPrimitive> primitives, SkeModel skeleton)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(skeleton);
        AmdModel model = AmdModel.Parse(template);
        if (model.HasParts)
        {
            return BuildParts(template, model, primitives, skeleton);
        }
        List<(TrkImportPrimitive Prim, int A, int B, int C)> triangles = [];
        SortedSet<int> used = [];
        foreach (TrkImportPrimitive primitive in primitives)
        {
            for (int at = 0; at + 2 < primitive.Indices.Length; at += 3)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    _ = used.Add(Bone(primitive, primitive.Indices[at + corner], model, skeleton));
                }
                triangles.Add((primitive, primitive.Indices[at], primitive.Indices[at + 1],
                    primitive.Indices[at + 2]));
            }
        }
        if (triangles.Count == 0)
        {
            throw new InvalidDataException("the model has no triangles to write into a skinned model");
        }
        int[] bones = [.. used];
        SskTrack before = model.Build([]);
        int emitted = 0;
        List<uint> list = [];
        Emit(list, TeximageParam, 0x40000000u);
        Emit(list, PlttBase, 0u);
        Emit(list, Color, 0x7FFFu);
        Emit(list, BeginVtxs, 0u);
        int current = -1;
        foreach ((TrkImportPrimitive prim, int a, int b, int c) in triangles)
        {
            foreach (int vertex in new[] { a, b, c })
            {
                int bone = Bone(prim, vertex, model, skeleton);
                if (bone != current)
                {
                    Emit(list, MtxRestore, (uint)(bone + AmdModel.SlotBase));
                    current = bone;
                }
                float[] local = Snap(Local(prim, vertex, bone, skeleton), before, emitted++);
                Emit(list, Normal, PackNormal(prim, vertex, bone, skeleton));
                Emit(list, Texcoord, PackTexcoord(prim, vertex));
                Emit(list, Vtx16, PackXy(local), PackZ(local));
            }
        }
        Emit(list, EndVtxs);
        byte[] displayList = Pack(list);
        int chunk = model.ChunkOffset;
        int texCount = (int)BinaryRead.U32(template, chunk + 0x10);
        int header = AmdModel.HeaderSize + (bones.Length * 8) + AmdModel.ExtentSize;
        int start = header + ZonModel.ChunkHeaderSize + (texCount * ZonModel.TexRefEntrySize);
        byte[] output = new byte[start + displayList.Length];
        template[..AmdModel.HeaderSize].CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.CountField), (uint)bones.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.TableField), AmdModel.HeaderSize);
        int values = AmdModel.HeaderSize + (bones.Length * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(AmdModel.ExtentField), (uint)(values + (bones.Length * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.ChunkField), (uint)header);
        for (int i = 0; i < bones.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(AmdModel.HeaderSize + (i * 4)), (uint)(values + (i * 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(values + (i * 4)), (uint)bones[i]);
        }
        template.Slice(model.ChunkOffset - AmdModel.ExtentSize, AmdModel.ExtentSize)
            .CopyTo(output.AsSpan(values + (bones.Length * 4)));
        template.Slice(chunk, ZonModel.ChunkHeaderSize + (texCount * ZonModel.TexRefEntrySize))
            .CopyTo(output.AsSpan(header));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header + 4), (uint)displayList.Length);
        displayList.CopyTo(output.AsSpan(start));
        return output;
    }
    private static byte[] BuildParts(
        ReadOnlySpan<byte> template,
        AmdModel model,
        IReadOnlyList<TrkImportPrimitive> primitives,
        SkeModel skeleton)
    {
        List<(TrkImportPrimitive Prim, int A, int B, int C)>[] byPart =
            new List<(TrkImportPrimitive, int, int, int)>[model.PartBones.Count];
        for (int part = 0; part < byPart.Length; part++)
        {
            byPart[part] = [];
        }
        foreach (TrkImportPrimitive primitive in primitives)
        {
            for (int at = 0; at + 2 < primitive.Indices.Length; at += 3)
            {
                int bone = PartBone(primitive, primitive.Indices[at], skeleton);
                int part = primitive.ChunkIndex is { } named && named >= 0 && named < byPart.Length
                    ? named
                    : model.PartBones.ToList().IndexOf(bone);
                if (part < 0)
                {
                    throw new InvalidDataException(
                        $"a triangle hangs from bone {bone}, which this model has no part for; "
                        + "a part model keeps the parts it came with, so move the triangle onto a "
                        + "bone the model already draws");
                }
                byPart[part].Add((primitive, primitive.Indices[at], primitive.Indices[at + 1],
                    primitive.Indices[at + 2]));
            }
        }
        SskTrack before = model.Build([]);
        int emitted = 0;
        byte[][] chunks = new byte[model.PartBones.Count][];
        for (int part = 0; part < model.PartBones.Count; part++)
        {
            int bone = model.PartBones[part];
            int start = model.ChunkOffsets[part];
            ChunkLayout layout = ChunkLayout.Of(template, start)
                ?? throw new InvalidDataException("a part of this model does not hold a chunk");
            int listStart = layout.ListStart(template, start);
            int head = listStart - start;
            List<(TrkImportPrimitive Prim, int A, int B, int C)> triangles = byPart[part];
            if (triangles.Count == 0)
            {
                chunks[part] = template.Slice(start, head + (int)BinaryRead.U32(template, start + 4))
                    .ToArray();
                emitted += CountVertices(template, start, layout);
                continue;
            }
            List<uint> list = [];
            Emit(list, MtxPush);
            Emit(list, TeximageParam, BinaryRead.U32(template, start + 0x08));
            Emit(list, PlttBase, 0u);
            Emit(list, Color, 0x7FFFu);
            Emit(list, BeginVtxs, 0u);
            foreach ((TrkImportPrimitive prim, int a, int b, int c) in triangles)
            {
                foreach (int vertex in new[] { a, b, c })
                {
                    float[] local = Snap(Local(prim, vertex, bone, skeleton), before, emitted++);
                    Emit(list, Texcoord, PackTexcoord(prim, vertex));
                    Emit(list, Normal, PackNormal(prim, vertex, bone, skeleton));
                    Emit(list, Vtx16, PackXy(local), PackZ(local));
                }
            }
            Emit(list, EndVtxs);
            Emit(list, MtxPop, 1u);
            byte[] displayList = Pack(list);
            byte[] chunk = new byte[head + displayList.Length];
            template.Slice(start, head).CopyTo(chunk);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)displayList.Length);
            displayList.CopyTo(chunk.AsSpan(head));
            chunks[part] = chunk;
        }
        int table = AmdModel.PartHeaderSize;
        int records = table + (chunks.Length * 4);
        int body = records + (chunks.Length * AmdModel.ExtentSize);
        int total = body + (chunks.Length * 8);
        foreach (byte[] chunk in chunks)
        {
            total += chunk.Length;
        }
        byte[] output = new byte[total];
        template[..AmdModel.PartHeaderSize].CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.CountField), (uint)chunks.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.TableField), (uint)table);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(AmdModel.ExtentField), (uint)records);
        int cursor = body;
        for (int part = 0; part < chunks.Length; part++)
        {
            template.Slice(RecordOf(model, part), AmdModel.ExtentSize)
                .CopyTo(output.AsSpan(records + (part * AmdModel.ExtentSize)));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(table + (part * 4)), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor), (uint)model.PartBones[part]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 4), (uint)(cursor + 8));
            chunks[part].CopyTo(output.AsSpan(cursor + 8));
            cursor += 8 + chunks[part].Length;
        }
        return output;
    }
    private static int RecordOf(AmdModel model, int part)
    {
        int table = AmdModel.PartHeaderSize;
        int records = table + (model.PartBones.Count * 4);
        return records + (part * AmdModel.ExtentSize);
    }
    private static int CountVertices(ReadOnlySpan<byte> template, int start, ChunkLayout layout)
    {
        int listStart = layout.ListStart(template, start);
        int size = (int)BinaryRead.U32(template, start + 4);
        VertexCounter counter = new();
        Gx.GxRunner.Run(template.Slice(listStart, size), counter);
        return counter.Count;
    }
    private static int PartBone(TrkImportPrimitive primitive, int vertex, SkeModel skeleton)
    {
        int bone = primitive.Bones is { } bound && vertex < bound.Length ? bound[vertex] : -1;
        return bone >= 0 && bone < skeleton.Bones.Count ? bone : 0;
    }
    private sealed class VertexCounter : Gx.IGxSink
    {
        public int Count { get; private set; }
        public void Begin(int mode)
        {
        }
        public void TextureParam(uint param, int byteOffset)
        {
        }
        public void MatrixRestore(int slot)
        {
        }
        public int Vertex(Gx.GxVertex vertex)
        {
            return Count++;
        }
        public void Triangle(int a, int b, int c, int mode)
        {
        }
    }
    public static (byte[] Bytes, int Triangles) Convert(
        IGltfSource source,
        ReadOnlySpan<byte> template,
        SkeModel skeleton,
        IReadOnlyList<SskTexture> textures,
        Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        ArgumentNullException.ThrowIfNull(textures);
        List<TrkImportPrimitive> primitives = TrkGltfReader.Read(source, textures, warn);
        int triangles = 0;
        foreach (TrkImportPrimitive primitive in primitives)
        {
            triangles += primitive.Indices.Length / 3;
        }
        byte[] rebuilt = Build(template, primitives, skeleton);
        return (Unchanged(template, rebuilt) ? template.ToArray() : rebuilt, triangles);
    }
    private static bool Unchanged(ReadOnlySpan<byte> template, ReadOnlySpan<byte> rebuilt)
    {
        try
        {
            SskTrack left = AmdModel.Parse(template).Build([]);
            SskTrack right = AmdModel.Parse(rebuilt).Build([]);
            return left.Indices.AsSpan().SequenceEqual(right.Indices)
                && left.VertexSlots.AsSpan().SequenceEqual(right.VertexSlots)
                && Close(left.Positions, right.Positions, Rounding)
                && Close(left.Uvs, right.Uvs, Rounding);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
    private static float[] Snap(float[] local, SskTrack template, int vertex)
    {
        int at = vertex * 3;
        if (at + 2 >= template.Positions.Length)
        {
            return local;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(template.Positions[at + axis] - local[axis]) > Rounding)
            {
                return local;
            }
        }
        return [template.Positions[at], template.Positions[at + 1], template.Positions[at + 2]];
    }
    private static bool Close(float[] left, float[] right, float tolerance)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (int i = 0; i < left.Length; i++)
        {
            if (Math.Abs(left[i] - right[i]) > tolerance)
            {
                return false;
            }
        }
        return true;
    }
    private static int Bone(
        TrkImportPrimitive primitive, int vertex, AmdModel model, SkeModel skeleton)
    {
        int bone = primitive.Bones is { } bound && vertex < bound.Length ? bound[vertex] : -1;
        return bone >= 0 && bone < skeleton.Bones.Count ? bone : model.Bones.Count > 0 ? model.Bones[0] : 0;
    }
    private static float[] Local(
        TrkImportPrimitive primitive, int vertex, int bone, SkeModel skeleton)
    {
        double[] world = skeleton.Rest(bone);
        double x = primitive.Positions[vertex * 3] - world[12];
        double y = primitive.Positions[(vertex * 3) + 1] - world[13];
        double z = primitive.Positions[(vertex * 3) + 2] - world[14];
        return
        [
            (float)(((x * world[0]) + (y * world[1]) + (z * world[2])) / Squared(world, 0)),
            (float)(((x * world[4]) + (y * world[5]) + (z * world[6])) / Squared(world, 1)),
            (float)(((x * world[8]) + (y * world[9]) + (z * world[10])) / Squared(world, 2)),
        ];
    }
    private static double Squared(double[] matrix, int row)
    {
        double length = (matrix[row * 4] * matrix[row * 4])
            + (matrix[(row * 4) + 1] * matrix[(row * 4) + 1])
            + (matrix[(row * 4) + 2] * matrix[(row * 4) + 2]);
        return length > 0 ? length : 1;
    }
    private static uint PackNormal(
        TrkImportPrimitive primitive, int vertex, int bone, SkeModel skeleton)
    {
        float[] world = skeleton.RestMatrix(bone);
        float x = primitive.Normals[vertex * 3];
        float y = primitive.Normals[(vertex * 3) + 1];
        float z = primitive.Normals[(vertex * 3) + 2];
        float nx = (x * world[0]) + (y * world[1]) + (z * world[2]);
        float ny = (x * world[4]) + (y * world[5]) + (z * world[6]);
        float nz = (x * world[8]) + (y * world[9]) + (z * world[10]);
        float length = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (length > 0)
        {
            nx /= length;
            ny /= length;
            nz /= length;
        }
        return (uint)((Signed10(nx) & 0x3FF) | ((Signed10(ny) & 0x3FF) << 10) | ((Signed10(nz) & 0x3FF) << 20));
    }
    private static uint PackTexcoord(TrkImportPrimitive primitive, int vertex)
    {
        short u = Fixed4(primitive.Uvs[vertex * 2]);
        short v = Fixed4(primitive.Uvs[(vertex * 2) + 1]);
        return (ushort)u | ((uint)(ushort)v << 16);
    }
    private static uint PackXy(float[] local)
    {
        return (ushort)Fixed12(local[0]) | ((uint)(ushort)Fixed12(local[1]) << 16);
    }
    private static uint PackZ(float[] local)
    {
        return (ushort)Fixed12(local[2]);
    }
    private static short Fixed12(float value)
    {
        return (short)Math.Clamp((int)MathF.Round(value * 4096f), short.MinValue, short.MaxValue);
    }
    private static short Fixed4(float value)
    {
        return (short)Math.Clamp((int)MathF.Round(value * 16f), short.MinValue, short.MaxValue);
    }
    private static int Signed10(float value)
    {
        return Math.Clamp((int)MathF.Round(value * 512f), -512, 511);
    }
    private static void Emit(List<uint> list, int command, params uint[] parameters)
    {
        list.Add(0x1000_0000u | (uint)command);
        foreach (uint value in parameters)
        {
            list.Add(value);
        }
    }
    private static byte[] Pack(List<uint> list)
    {
        List<uint> words = [];
        int at = 0;
        while (at < list.Count)
        {
            List<int> commands = [];
            List<uint> parameters = [];
            while (commands.Count < 4 && at < list.Count)
            {
                uint entry = list[at++];
                int command = (int)(entry & 0xFF);
                commands.Add(command);
                int count = Parameters(command);
                for (int k = 0; k < count; k++)
                {
                    parameters.Add(list[at++]);
                }
            }
            while (commands.Count < 4)
            {
                commands.Add(Nop);
            }
            uint pack = 0;
            for (int i = 0; i < 4; i++)
            {
                pack |= (uint)commands[i] << (i * 8);
            }
            words.Add(pack);
            words.AddRange(parameters);
        }
        byte[] output = new byte[words.Count * 4];
        for (int i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), words[i]);
        }
        return output;
    }
    private static int Parameters(int command)
    {
        return command switch
        {
            Vtx16 => 2,
            EndVtxs or Nop => 0,
            _ => 1,
        };
    }
}
