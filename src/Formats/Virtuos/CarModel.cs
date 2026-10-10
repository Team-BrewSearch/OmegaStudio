using Virtuos.Formats.Binary;
namespace Virtuos.Formats;

public sealed class CarModel
{
    private static readonly int[] TableFields = [0x10, 0x14];
    public const int WheelCount = 4;
    private const int WheelBlock = WheelCount * 12;
    private readonly byte[] _blob;
    private CarModel(byte[] blob, int table, int[] offsets, int tailStart, int wheelStart)
    {
        _blob = blob;
        TableField = table;
        ChunkOffsets = offsets;
        TailStart = tailStart;
        WheelStart = wheelStart;
    }
    public int TableField { get; }
    public IReadOnlyList<int> ChunkOffsets { get; }
    public int TailStart { get; }
    public int WheelStart { get; }
    public bool HasWheels => WheelStart >= 0;
    public ReadOnlySpan<byte> Blob => _blob;
    public static bool IsCar(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _, out _, out _, out _);
    }
    public static CarModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out int table, out int[] offsets, out int tail, out int wheels)
            ? new CarModel(blob.ToArray(), table, offsets, tail, wheels)
            : throw new InvalidDataException("not a Virtuos .car model");
    }
    public SskTrack Build(IReadOnlyList<SskTexture> textures)
    {
        return SskTrack.ParseChunks(_blob, ChunkOffsets, textures);
    }
    public byte[] Serialize()
    {
        return [.. _blob];
    }
    public float Wheel(int index, int axis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, WheelCount);
        return HasWheels
            ? (int)BinaryRead.U32(_blob, WheelStart + (index * 12) + (axis * 4)) / 4096f
            : 0f;
    }
    private static bool TryRead(
        ReadOnlySpan<byte> blob, out int table, out int[] offsets, out int tail, out int wheels)
    {
        table = 0;
        offsets = [];
        tail = 0;
        wheels = -1;
        if (blob.Length < 0x28)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, 0);
        if (count is 0 or > 256)
        {
            return false;
        }
        foreach (int field in TableFields)
        {
            if (Walk(blob, field, (int)count) is not { } walked)
            {
                continue;
            }
            table = field;
            offsets = walked.Offsets;
            tail = walked.End;
            wheels = blob.Length - walked.End >= WheelBlock ? blob.Length - WheelBlock : -1;
            return true;
        }
        return false;
    }
    private static (int[] Offsets, int End)? Walk(ReadOnlySpan<byte> blob, int field, int count)
    {
        if (field + 4 > blob.Length)
        {
            return null;
        }
        long start = BinaryRead.U32(blob, field);
        long tableEnd = start + ((long)count * 4);
        if (start < 0x20 || tableEnd > blob.Length)
        {
            return null;
        }
        int[] found = new int[count];
        long end = tableEnd;
        for (int i = 0; i < count; i++)
        {
            long at = BinaryRead.U32(blob, (int)start + (i * 4));
            if (at < end || at + ZonModel.ChunkHeaderSize > blob.Length)
            {
                return null;
            }
            long listSize = BinaryRead.U32(blob, (int)at + 4);
            long texCount = BinaryRead.U32(blob, (int)at + 0x10);
            if (texCount > 4096 || listSize == 0 || listSize % 4 != 0)
            {
                return null;
            }
            long chunkEnd = at + ZonModel.ChunkHeaderSize
                + (texCount * ZonModel.TexRefEntrySize) + listSize;
            if (chunkEnd > blob.Length)
            {
                return null;
            }
            found[i] = (int)at;
            end = chunkEnd;
        }
        return (found, (int)end);
    }
}
