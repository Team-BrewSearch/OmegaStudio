using System.Buffers.Binary;
using Omega.Plugins;
namespace Disgaea.Formats;
internal sealed class Mpd
{
    public const int HeaderSize = 16;
    public const int ChunkSize = 1408;
    public const int TileSize = 128;
    public const int ActorSize = 64;
    public const int ObjectCount = 32;
    public const int ObjectSize = 36;
    public const int EventCount = 16;
    public const int EventsAt = 64 + (ObjectCount * ObjectSize);
    public const int NoTexture = 0xFFFF;
    private readonly byte[] _header;
    private Mpd(byte[] header, List<MpdChunk> chunks, List<byte[]> actors, List<uint> textures, List<uint> models)
    {
        _header = header;
        Chunks = chunks;
        Actors = actors;
        Textures = textures;
        Models = models;
    }
    public byte[] Header => _header;
    public List<MpdChunk> Chunks { get; }
    public List<byte[]> Actors { get; }
    public List<uint> Textures { get; }
    public List<uint> Models { get; }
    public int Revision { get; private set; }
    public static int X(byte[] tile)
    {
        return (sbyte)tile[113];
    }
    public static int Z(byte[] tile)
    {
        return (sbyte)tile[114];
    }
    public IEnumerable<byte[]> Tiles => Chunks.SelectMany(c => c.Tiles);
    public int TileCount => Chunks.Sum(c => c.Tiles.Count);
    public static Mpd Create(byte[] header, List<MpdChunk> chunks, List<byte[]> actors, List<uint> textures, List<uint> models)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.Length == HeaderSize
            ? new Mpd(header, chunks, actors, textures, models)
            : throw new InvalidDataException($"a map header is {HeaderSize} bytes");
    }
    public static Mpd Read(ReadOnlySpan<byte> data)
    {
        BinaryRead.Need(data, 0, HeaderSize);
        int chunkCount = BinaryRead.U16(data, 0);
        int actorCount = BinaryRead.U16(data, 2);
        int at = HeaderSize;
        BinaryRead.Need(data, at, chunkCount * ChunkSize);
        List<MpdChunk> chunks = [];
        for (int i = 0; i < chunkCount; i++)
        {
            chunks.Add(new MpdChunk(data.Slice(at, ChunkSize).ToArray()));
            at += ChunkSize;
        }
        foreach (MpdChunk chunk in chunks)
        {
            int tiles = BinaryRead.U16(chunk.Header, 44);
            BinaryRead.Need(data, at, tiles * TileSize);
            for (int t = 0; t < tiles; t++)
            {
                chunk.Tiles.Add(data.Slice(at, TileSize).ToArray());
                at += TileSize;
            }
        }
        BinaryRead.Need(data, at, actorCount * ActorSize);
        List<byte[]> actors = [];
        for (int i = 0; i < actorCount; i++)
        {
            actors.Add(data.Slice(at, ActorSize).ToArray());
            at += ActorSize;
        }
        List<uint> textures = List(data, ref at, "texture");
        List<uint> models = List(data, ref at, "model");
        if (at != data.Length)
        {
            throw new InvalidDataException($"the map has {data.Length - at} byte(s) after its model list");
        }
        return new Mpd(data[..HeaderSize].ToArray(), chunks, actors, textures, models);
    }
    public byte[] Write()
    {
        if (Chunks.Count > ushort.MaxValue || Actors.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("a map holds at most 65535 chunks and 65535 actors");
        }
        int size = HeaderSize + (Chunks.Count * ChunkSize) + (TileCount * TileSize) + (Actors.Count * ActorSize) + 8 + ((Textures.Count + Models.Count) * 4);
        byte[] output = new byte[size];
        _header.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)Chunks.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)Actors.Count);
        int at = HeaderSize;
        foreach (MpdChunk chunk in Chunks)
        {
            if (chunk.Tiles.Count > ushort.MaxValue)
            {
                throw new InvalidDataException("a chunk holds at most 65535 tiles");
            }
            chunk.Header.CopyTo(output, at);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 44), (ushort)chunk.Tiles.Count);
            at += ChunkSize;
        }
        foreach (byte[] record in Tiles.Concat(Actors))
        {
            record.CopyTo(output, at);
            at += record.Length;
        }
        foreach (List<uint> list in new[] { Textures, Models })
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), list.Count);
            at += 4;
            foreach (uint value in list)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
                at += 4;
            }
        }
        return output;
    }
    public void Touch()
    {
        Revision++;
    }
    private static List<uint> List(ReadOnlySpan<byte> data, ref int at, string what)
    {
        BinaryRead.Need(data, at, 4);
        uint count = BinaryRead.U32(data, at);
        at += 4;
        if (count > (data.Length - at) / 4)
        {
            throw new InvalidDataException($"the {what} list claims {count} entries past the end of the map");
        }
        List<uint> list = [];
        for (int i = 0; i < count; i++)
        {
            list.Add(BinaryRead.U32(data, at));
            at += 4;
        }
        return list;
    }
}
internal sealed class MpdChunk(byte[] header)
{
    public byte[] Header { get; } = header;
    public List<byte[]> Tiles { get; } = [];
    public float OffsetX => BinaryPrimitives.ReadSingleLittleEndian(Header);
    public float OffsetZ => BinaryPrimitives.ReadSingleLittleEndian(Header.AsSpan(8));
    public int Id => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(48));
    public int TileOffsetX => (int)MathF.Floor(OffsetX / 12f);
    public int TileOffsetZ => (int)MathF.Floor(OffsetZ / 12f);
}
