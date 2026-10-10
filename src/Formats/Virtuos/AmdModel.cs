using Virtuos.Formats.Binary;
namespace Virtuos.Formats;

public sealed class AmdModel
{
    public const int HeaderSize = 0x1C;
    public const int CountField = 0x0C;
    public const int TableField = 0x10;
    public const int ExtentField = 0x14;
    public const int ChunkField = 0x18;
    public const int ExtentSize = 12;
    public const int SlotBase = 10;
    public const int PartHeaderSize = 0x18;
    private readonly byte[] _blob;
    private AmdModel(byte[] blob, int[] bones, int[] chunks, int[] partBones)
    {
        _blob = blob;
        Bones = bones;
        ChunkOffsets = chunks;
        PartBones = partBones;
    }
    public IReadOnlyList<int> PartBones { get; }
    public IReadOnlyList<int> Bones { get; }
    public IReadOnlyList<int> ChunkOffsets { get; }
    public int ChunkOffset => ChunkOffsets[0];
    public bool HasParts => PartBones.Count > 0;
    public ReadOnlySpan<byte> Blob => _blob;
    public static bool IsSkinnedModel(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _, out _) || TryReadParts(blob, out _, out _);
    }
    public static AmdModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out int[] bones, out int chunk)
            ? new AmdModel(blob.ToArray(), bones, [chunk], [])
            : TryReadParts(blob, out int[] parts, out int[] partBones)
            ? new AmdModel(blob.ToArray(), [], parts, partBones)
            : throw new InvalidDataException("not a Virtuos .amd skinned model");
    }
    public SskTrack Build(IReadOnlyList<SskTexture> textures)
    {
        SskTrack track = SskTrack.ParseChunks(_blob, [.. ChunkOffsets], textures);
        if (HasParts)
        {
            track.BindVerticesToChunkBones([.. PartBones], SlotBase);
        }
        return track;
    }
    public byte[] Serialize()
    {
        return [.. _blob];
    }
    public int BoneForSlot(int slot)
    {
        int bone = slot - SlotBase;
        return Bones.Contains(bone) ? bone : -1;
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, out int[] bones, out int chunk)
    {
        bones = [];
        chunk = 0;
        if (blob.Length < HeaderSize + ExtentSize + ZonModel.ChunkHeaderSize)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, CountField);
        uint table = BinaryRead.U32(blob, TableField);
        uint extent = BinaryRead.U32(blob, ExtentField);
        uint at = BinaryRead.U32(blob, ChunkField);
        if (count == 0 || count > 1024 || table != HeaderSize)
        {
            return false;
        }
        long values = table + ((long)count * 4);
        if (values + (count * 4) != extent || extent + ExtentSize != at
            || at + ZonModel.ChunkHeaderSize > blob.Length)
        {
            return false;
        }
        int[] found = new int[count];
        for (int i = 0; i < count; i++)
        {
            long pointer = BinaryRead.U32(blob, (int)table + (i * 4));
            if (pointer != values + (i * 4))
            {
                return false;
            }
            found[i] = (int)BinaryRead.U32(blob, (int)pointer);
        }
        long listSize = BinaryRead.U32(blob, (int)at + 4);
        long texCount = BinaryRead.U32(blob, (int)at + 0x10);
        if (texCount > 4096 || listSize == 0 || listSize % 4 != 0)
        {
            return false;
        }
        long end = at + ZonModel.ChunkHeaderSize + (texCount * ZonModel.TexRefEntrySize) + listSize;
        if (end != blob.Length)
        {
            return false;
        }
        bones = found;
        chunk = (int)at;
        return true;
    }
    private static bool TryReadParts(ReadOnlySpan<byte> blob, out int[] chunks, out int[] partBones)
    {
        chunks = [];
        partBones = [];
        if (blob.Length < PartHeaderSize + ZonModel.ChunkHeaderSize)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, CountField);
        uint table = BinaryRead.U32(blob, TableField);
        uint records = BinaryRead.U32(blob, ExtentField);
        if (count == 0 || count > 1024 || table != PartHeaderSize)
        {
            return false;
        }
        long recordsAt = table + ((long)count * 4);
        long first = recordsAt + ((long)count * ExtentSize);
        if (records != recordsAt || first > blob.Length)
        {
            return false;
        }
        int[] found = new int[count];
        int[] hangs = new int[count];
        long end = first;
        for (int part = 0; part < count; part++)
        {
            long at = BinaryRead.U32(blob, (int)table + (part * 4));
            if (at != end || at + 8 + ZonModel.ChunkHeaderSize > blob.Length
                || BinaryRead.U32(blob, (int)at) > 1024
                || BinaryRead.U32(blob, (int)at + 4) != at + 8)
            {
                return false;
            }
            hangs[part] = (int)BinaryRead.U32(blob, (int)at);
            long chunk = at + 8;
            long listSize = BinaryRead.U32(blob, (int)chunk + 4);
            long texCount = BinaryRead.U32(blob, (int)chunk + 0x10);
            if (texCount > 4096 || listSize == 0 || listSize % 4 != 0)
            {
                return false;
            }
            end = chunk + ZonModel.ChunkHeaderSize + (texCount * ZonModel.TexRefEntrySize) + listSize;
            if (end > blob.Length)
            {
                return false;
            }
            found[part] = (int)chunk;
        }
        if (end != blob.Length)
        {
            return false;
        }
        chunks = found;
        partBones = hangs;
        return true;
    }
}
