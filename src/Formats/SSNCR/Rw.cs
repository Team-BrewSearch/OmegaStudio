using System.Buffers.Binary;
using System.Text;
namespace SSNCR.Formats;

internal readonly record struct RwChunk(uint Type, int Offset, int Size)
{
    public int End => Offset + Size;
}

internal static class Rw
{
    public const uint Version = 0x1803FFFF;
    public const uint Struct = 0x01;
    public const uint String = 0x02;
    public const uint Extension = 0x03;
    public const uint Texture = 0x06;
    public const uint Material = 0x07;
    public const uint MaterialList = 0x08;
    public const uint Geometry = 0x0F;
    public const uint Atomic = 0x14;
    public const uint TextureNative = 0x15;
    public const uint TexDictionary = 0x16;
    public const uint BinMesh = 0x50E;
    public const uint NativeData = 0x510;
    public const uint Skin = 0x116;
    public const uint PlatformGcn = 6;
    public const uint PlatformPs2 = 4;

    public static bool IsChunk(ReadOnlySpan<byte> d, int at, uint type)
        => at >= 0 && at + 12 <= d.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(d[at..]) == type
            && BinaryPrimitives.ReadUInt32LittleEndian(d[(at + 8)..]) == Version;

    public static List<RwChunk> Children(ReadOnlySpan<byte> d, int start, int end)
    {
        List<RwChunk> list = [];
        int p = start;
        while (p + 12 <= end)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 8)..]);
            if (ver != Version || size > (uint)(end - p - 12))
                throw new InvalidDataException($"bad RenderWare chunk 0x{type:X} at 0x{p:X}");
            list.Add(new RwChunk(type, p + 12, (int)size));
            p += 12 + (int)size;
        }
        if (p != end) throw new InvalidDataException($"RenderWare chunks overrun at 0x{p:X}");
        return list;
    }

    public static List<RwChunk> Children(ReadOnlySpan<byte> d, RwChunk parent)
        => Children(d, parent.Offset, parent.End);

    public static RwChunk Require(List<RwChunk> list, uint type)
    {
        foreach (RwChunk c in list) if (c.Type == type) return c;
        throw new InvalidDataException($"missing RenderWare chunk 0x{type:X}");
    }

    public static RwChunk? Find(List<RwChunk> list, uint type)
    {
        foreach (RwChunk c in list) if (c.Type == type) return c;
        return null;
    }

    public static string CString(ReadOnlySpan<byte> d)
    {
        int n = d.IndexOf((byte)0);
        return Encoding.ASCII.GetString(n < 0 ? d : d[..n]);
    }
}
