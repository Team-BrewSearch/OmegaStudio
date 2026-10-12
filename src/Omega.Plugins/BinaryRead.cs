using System.Buffers.Binary;
using System.Text;
namespace Omega.Plugins;
public static class BinaryRead
{
    private const int Word = 4;
    public static ushort U16(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }
    public static short I16(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, 2);
        return BinaryPrimitives.ReadInt16LittleEndian(b[o..]);
    }
    public static uint U32(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, Word);
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }
    public static int I32(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, Word);
        return BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    }
    public static float F32(ReadOnlySpan<byte> b, int o)
    {
        return BitConverter.Int32BitsToSingle(I32(b, o));
    }
    public static float Fixed(ReadOnlySpan<byte> b, int o)
    {
        return I32(b, o) / 65536f;
    }
    public static ushort U16Big(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    }
    public static uint U32Big(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, Word);
        return BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    }
    public static int I32Big(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, Word);
        return BinaryPrimitives.ReadInt32BigEndian(b[o..]);
    }
    public static string Name(ReadOnlySpan<byte> blob, int offset, int max)
    {
        if (offset >= blob.Length)
        {
            return string.Empty;
        }
        ReadOnlySpan<byte> s = blob.Slice(offset, Math.Min(max, blob.Length - offset));
        int end = s.IndexOf((byte)0);
        return Latin1(end >= 0 ? s[..end] : s);
    }
    public static string Latin1(ReadOnlySpan<byte> b)
    {
        return Encoding.Latin1.GetString(b);
    }
    public static bool Spells(ReadOnlySpan<byte> b, string text)
    {
        return b.SequenceEqual(Encoding.ASCII.GetBytes(text));
    }
    public static string Hex(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0)
        {
            return string.Empty;
        }
        return string.Create(b.Length * 2, b, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                byte value = source[i];
                span[i * 2] = HexDigit(value >> 4);
                span[(i * 2) + 1] = HexDigit(value & 0xF);
            }
        });
    }
    private static char HexDigit(int nibble)
    {
        return (char)(nibble < 10 ? ('0' + nibble) : ('a' + (nibble - 10)));
    }
    public static void Need(ReadOnlySpan<byte> b, int o, int bytes)
    {
        if (o < 0 || bytes < 0 || o + bytes > b.Length)
        {
            throw new InvalidDataException(
                $"reading {bytes} byte(s) at 0x{Math.Max(0, o):X} runs past the end of a {b.Length}-byte blob");
        }
    }
}
