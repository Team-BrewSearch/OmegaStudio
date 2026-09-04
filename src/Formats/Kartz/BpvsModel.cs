using Kartz.Formats.Binary;
namespace Kartz.Formats;

public sealed class BpvsSector
{
    public required int Offset { get; init; }
    public required byte[] Runs { get; init; }
    public int VisibleChunks
    {
        get
        {
            int visible = 0;
            foreach (byte run in Runs)
            {
                if ((run & BpvsModel.HiddenFlag) == 0)
                {
                    visible += run & BpvsModel.RunMask;
                }
            }
            return visible;
        }
    }
    public bool[] Visibility(int chunkCount)
    {
        bool[] drawn = new bool[chunkCount];
        int at = 0;
        foreach (byte run in Runs)
        {
            int length = Math.Min(run & BpvsModel.RunMask, chunkCount - at);
            if ((run & BpvsModel.HiddenFlag) == 0)
            {
                for (int i = 0; i < length; i++)
                {
                    drawn[at + i] = true;
                }
            }
            at += length;
        }
        return drawn;
    }
}
public sealed class BpvsModel
{
    public const byte HiddenFlag = 0x80;
    public const byte RunMask = 0x7F;
    private const int TableOffset = 8;
    private readonly byte[] _blob;
    private BpvsModel(byte[] blob, IReadOnlyList<BpvsSector> sectors, int chunkCount)
    {
        _blob = blob;
        Sectors = sectors;
        ChunkCount = chunkCount;
    }
    public IReadOnlyList<BpvsSector> Sectors { get; }
    public int ChunkCount { get; }
    public int SharedLists
    {
        get
        {
            HashSet<int> seen = [];
            int shared = 0;
            foreach (BpvsSector sector in Sectors)
            {
                if (!seen.Add(sector.Offset))
                {
                    shared++;
                }
            }
            return shared;
        }
    }
    public static bool IsVisibility(ReadOnlySpan<byte> blob)
    {
        return TryReadTable(blob, out _);
    }
    public static BpvsModel Parse(ReadOnlySpan<byte> blob, int chunkCount)
    {
        if (!TryReadTable(blob, out int[] offsets))
        {
            throw new InvalidDataException("not a Kartz .bpvs visibility table");
        }
        if (chunkCount <= 0)
        {
            throw new InvalidDataException(
                "the track this visibility table belongs to was not found, so its chunk count is unknown");
        }
        List<BpvsSector> sectors = new(offsets.Length);
        foreach (int offset in offsets)
        {
            sectors.Add(new BpvsSector { Offset = offset, Runs = ReadRuns(blob, offset, chunkCount) });
        }
        return new BpvsModel(blob.ToArray(), sectors, chunkCount);
    }
    public byte[] Serialize()
    {
        return [.. _blob];
    }
    public static byte[] Build(IReadOnlyList<bool[]> visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        List<byte[]> lists = new(visibility.Count);
        foreach (bool[] sector in visibility)
        {
            lists.Add(Encode(sector));
        }
        int table = TableOffset + ((visibility.Count + 1) * 4);
        int size = table;
        foreach (byte[] list in lists)
        {
            size += list.Length;
        }
        byte[] output = new byte[size];
        Write(output, 0, (uint)visibility.Count);
        Write(output, 4, TableOffset);
        int at = table;
        for (int i = 0; i < lists.Count; i++)
        {
            Write(output, TableOffset + (i * 4), (uint)at);
            lists[i].CopyTo(output.AsSpan(at));
            at += lists[i].Length;
        }
        return output;
    }
    private static byte[] Encode(bool[] sector)
    {
        List<byte> runs = [];
        int at = 0;
        while (at < sector.Length)
        {
            bool drawn = sector[at];
            int length = 0;
            while (at + length < sector.Length && sector[at + length] == drawn && length < RunMask)
            {
                length++;
            }
            runs.Add((byte)(drawn ? length : length | HiddenFlag));
            at += length;
        }
        return [.. runs];
    }
    private static byte[] ReadRuns(ReadOnlySpan<byte> blob, int offset, int chunkCount)
    {
        int at = offset;
        int covered = 0;
        while (covered < chunkCount)
        {
            if (at >= blob.Length)
            {
                throw new InvalidDataException(
                    $"the visibility list at 0x{offset:X} runs past the end of the file");
            }
            covered += blob[at] & RunMask;
            at++;
        }
        return covered == chunkCount
            ? blob[offset..at].ToArray()
            : throw new InvalidDataException(
                $"the visibility list at 0x{offset:X} covers {covered} chunks, but the track has {chunkCount}");
    }
    private static bool TryReadTable(ReadOnlySpan<byte> blob, out int[] offsets)
    {
        offsets = [];
        if (blob.Length < TableOffset + 8 || BinaryRead.U32(blob, 4) != TableOffset)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, 0);
        long tableEnd = TableOffset + (((long)count + 1) * 4);
        if (count == 0 || count > 8192 || tableEnd > blob.Length)
        {
            return false;
        }
        int[] found = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint at = BinaryRead.U32(blob, TableOffset + (i * 4));
            if (at < tableEnd || at >= blob.Length)
            {
                return false;
            }
            found[i] = (int)at;
        }
        if (BinaryRead.U32(blob, TableOffset + ((int)count * 4)) != 0)
        {
            return false;
        }
        offsets = found;
        return true;
    }
    private static void Write(byte[] output, int at, uint value)
    {
        output[at] = (byte)value;
        output[at + 1] = (byte)(value >> 8);
        output[at + 2] = (byte)(value >> 16);
        output[at + 3] = (byte)(value >> 24);
    }
}
