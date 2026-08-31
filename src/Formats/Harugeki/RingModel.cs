using Harugeki.Formats.Binary;
using System.Diagnostics;
namespace Harugeki.Formats;

public sealed class RingModel
{
    public const int NodeTableOffset = 0x50;
    public const int VertexStride = 40;
    public const int FaceTrailer = 28;
    public const int MaterialStride = 0x44;
    private const int MeshTagOffset = 0x70;
    private const int MaxNodes = 4096;
    private const int MaxTextures = 64;
    private const int HeaderSize = 0x60;
    private static ReadOnlySpan<byte> MeshTag => "Mate"u8;
    public string Name { get; private set; } = string.Empty;
    public List<RingBone> Bones { get; } = [];
    public List<RingMesh> Meshes { get; } = [];
    public List<RingTexture> Textures { get; } = [];
    public List<(int Offset, int Length)> TextureSpans { get; } = [];
    public static bool LooksLikeModel(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize || BinaryRead.U32(blob, 0) != 1)
        {
            return false;
        }
        uint nodes = BinaryRead.U32(blob, 4);
        if (nodes is 0 or >= MaxNodes || BinaryRead.U32(blob, 0x0C) > MaxTextures)
        {
            return false;
        }
        long tableEnd = NodeTableOffset + ((long)nodes * 4);
        return tableEnd <= blob.Length && BinaryRead.U32(blob, NodeTableOffset) == tableEnd;
    }
    public static RingModel Parse(ReadOnlySpan<byte> blob)
    {
        if (!LooksLikeModel(blob))
        {
            throw new ArgumentException("not a model blob", nameof(blob));
        }
        int nodeCount = (int)BinaryRead.U32(blob, 4);
        int textureCount = (int)BinaryRead.U32(blob, 0x0C);
        int[] textureOffsets = new int[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            textureOffsets[i] = (int)BinaryRead.U32(blob, 0x10 + (i * 4));
        }
        int[] nodeOffsets = new int[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeOffsets[i] = (int)BinaryRead.U32(blob, NodeTableOffset + (i * 4));
        }
        RingModel model = new();
        Dictionary<int, int> endOf = BuildBounds(nodeOffsets, textureOffsets, blob.Length);
        for (int i = 0; i < nodeCount; i++)
        {
            int offset = nodeOffsets[i];
            if (offset <= 0 || offset + MeshTagOffset + MeshTag.Length > blob.Length)
            {
                Debug.WriteLine($"[RingModel] skipped node {i}: offset 0x{offset:X} out of range");
                continue;
            }
            int end = endOf.TryGetValue(offset, out int e) ? e : blob.Length;
            if (blob.Slice(offset + MeshTagOffset, MeshTag.Length).SequenceEqual(MeshTag))
            {
                try
                {
                    model.Meshes.Add(RingMesh.Parse(blob, offset, end, i));
                }
                catch (ArgumentException ex)
                {
                    Debug.WriteLine($"[RingModel] skipped node {i}: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    model.Bones.Add(RingBone.Parse(blob, offset, end, i));
                }
                catch (ArgumentException ex)
                {
                    Debug.WriteLine($"[RingModel] skipped bone {i}: {ex.Message}");
                }
            }
        }
        model.ReadTextures(blob, textureOffsets);
        if (model.Meshes.Count > 0)
        {
            model.Name = model.Meshes.MaxBy(m => m.TriangleCount)!.Name;
        }
        return model;
    }
    private static Dictionary<int, int> BuildBounds(int[] nodeOffsets, int[] textureOffsets, int limit)
    {
        int[] sorted =
        [
            .. nodeOffsets
                .Concat(textureOffsets.Length > 0 ? [textureOffsets[0]] : [limit])
                .Where(o => o > 0)
                .Distinct()
                .Order(),
        ];
        Dictionary<int, int> endOf = new(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            endOf[sorted[i]] = i + 1 < sorted.Length ? sorted[i + 1] : limit;
        }
        return endOf;
    }
    private void ReadTextures(ReadOnlySpan<byte> blob, int[] textureOffsets)
    {
        for (int i = 0; i < textureOffsets.Length; i++)
        {
            int start = textureOffsets[i];
            if (start <= 0 || start >= blob.Length)
            {
                continue;
            }
            int end = textureOffsets.Where(t => t > start).DefaultIfEmpty(blob.Length).Min();
            ReadOnlySpan<byte> slice = blob[start..end];
            if (!RingTexture.IsTexture(slice))
            {
                continue;
            }
            Textures.Add(RingTexture.Parse(slice));
            TextureSpans.Add((start, end - start));
        }
    }
    public override string ToString()
    {
        return $"{Name} bones={Bones.Count} meshes={Meshes.Count} tex={Textures.Count}";
    }
}
