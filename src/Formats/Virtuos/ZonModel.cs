using Virtuos.Formats.Binary;
using System.Buffers.Binary;
namespace Virtuos.Formats;

public sealed class ZonModel
{
    public const int HeaderSize = 0x0C;
    public const int WideHeaderSize = 0x10;
    public const int ChunkHeaderSize = 24;
    public const int TexRefEntrySize = 12;
    private readonly byte[] _blob;
    private ZonModel(byte[] blob, int[] offsets, int trailerStart, int header)
    {
        _blob = blob;
        ChunkOffsets = offsets;
        TrailerStart = trailerStart;
        Header = header;
    }
    public IReadOnlyList<int> ChunkOffsets { get; }
    public int TrailerStart { get; }
    public int Header { get; }
    public ReadOnlySpan<byte> Blob => _blob;
    public static bool IsZone(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _, out _);
    }
    public static ZonModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out int[] offsets, out int trailerStart)
            ? new ZonModel(blob.ToArray(), offsets, trailerStart, (int)BinaryRead.U32(blob, 4))
            : throw new InvalidDataException("not a Virtuos .zon model bundle");
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
        return Assemble(chunks, trailer, HeaderSize);
    }
    public static byte[] Assemble(IReadOnlyList<byte[]> chunks, ReadOnlySpan<byte> trailer, int header)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        int tableSize = chunks.Count * 4;
        int body = header + tableSize;
        foreach (byte[] chunk in chunks)
        {
            body += chunk.Length;
        }
        byte[] output = new byte[body + trailer.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)chunks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)header);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)(output.Length - header));
        int at = header + tableSize;
        for (int i = 0; i < chunks.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header + (i * 4)), (uint)at);
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
        if (count == 0 || count > 4096
            || (headerSize != HeaderSize && headerSize != WideHeaderSize)
            || payload < 4 || payload > blob.Length - HeaderSize)
        {
            return false;
        }
        if (headerSize == WideHeaderSize && BinaryRead.U32(blob, 12) != 0)
        {
            return false;
        }
        long tableEnd = headerSize + ((long)count * 4);
        if (tableEnd > blob.Length)
        {
            return false;
        }
        int[] found = new int[count];
        long end = tableEnd;
        for (int i = 0; i < count; i++)
        {
            long at = BinaryRead.U32(blob, (int)headerSize + (i * 4));
            if (at < tableEnd || ChunkLayout.Of(blob, at) is not { } layout)
            {
                return false;
            }
            long chunkEnd = layout.End(blob, at);
            if (chunkEnd < 0)
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
