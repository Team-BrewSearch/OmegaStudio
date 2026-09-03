using Kartz.Formats.Binary;
using System.Buffers.Binary;
namespace Kartz.Formats;

public sealed class ZonModel
{
    public const int HeaderSize = 0x0C;
    public const int ChunkHeaderSize = 24;
    public const int TexRefEntrySize = 12;
    private readonly byte[] _blob;
    private ZonModel(byte[] blob, int[] offsets, int trailerStart)
    {
        _blob = blob;
        ChunkOffsets = offsets;
        TrailerStart = trailerStart;
    }
    public IReadOnlyList<int> ChunkOffsets { get; }
    public int TrailerStart { get; }
    public ReadOnlySpan<byte> Blob => _blob;
    public static bool IsZone(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _, out _);
    }
    public static ZonModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out int[] offsets, out int trailerStart)
            ? new ZonModel(blob.ToArray(), offsets, trailerStart)
            : throw new InvalidDataException("not a Kartz .zon model bundle");
    }
    public SskTrack Build(IReadOnlyList<SskTexture> textures)
    {
        return SskTrack.ParseChunks(_blob, ChunkOffsets, textures);
    }
    public byte[] Serialize()
    {
        return [.. _blob];
    }
    public static byte[] Assemble(IReadOnlyList<byte[]> chunks, ReadOnlySpan<byte> trailer)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        int tableSize = chunks.Count * 4;
        int body = HeaderSize + tableSize;
        foreach (byte[] chunk in chunks)
        {
            body += chunk.Length;
        }
        byte[] output = new byte[body + trailer.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)chunks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)(output.Length - HeaderSize));
        int at = HeaderSize + tableSize;
        for (int i = 0; i < chunks.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(HeaderSize + (i * 4)), (uint)at);
            chunks[i].CopyTo(output.AsSpan(at));
            at += chunks[i].Length;
        }
        trailer.CopyTo(output.AsSpan(at));
        return output;
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, out int[] offsets, out int trailerStart)
    {
        offsets = [];
        trailerStart = 0;
        if (blob.Length < HeaderSize + 4)
        {
            return false;
        }
        uint count = BinaryRead.U32(blob, 0);
        uint headerSize = BinaryRead.U32(blob, 4);
        uint payload = BinaryRead.U32(blob, 8);
        if (count == 0 || count > 4096 || headerSize != HeaderSize
            || payload < 4 || payload > blob.Length - HeaderSize)
        {
            return false;
        }
        long tableEnd = HeaderSize + ((long)count * 4);
        if (tableEnd > blob.Length)
        {
            return false;
        }
        int[] found = new int[count];
        long end = tableEnd;
        for (int i = 0; i < count; i++)
        {
            long at = BinaryRead.U32(blob, HeaderSize + (i * 4));
            if (at < tableEnd || at + ChunkHeaderSize > blob.Length)
            {
                return false;
            }
            long listSize = BinaryRead.U32(blob, (int)at + 4);
            long texCount = BinaryRead.U32(blob, (int)at + 0x10);
            if (texCount > 4096 || listSize % 4 != 0)
            {
                return false;
            }
            long chunkEnd = at + ChunkHeaderSize + (texCount * TexRefEntrySize) + listSize;
            if (chunkEnd > blob.Length)
            {
                return false;
            }
            found[i] = (int)at;
            end = Math.Max(end, chunkEnd);
        }
        offsets = found;
        trailerStart = (int)end;
        return true;
    }
}
