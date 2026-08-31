using System.Buffers.Binary;
using System.Text;
namespace Kartz.Formats.Binary;

internal static class BinaryRead
{
    public static uint U32(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }
    public static ushort U16(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }
    public static float F32(ReadOnlySpan<byte> b, int o)
    {
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(b[o..]));
    }
    public static string Name(ReadOnlySpan<byte> blob, int offset, int max)
    {
        if (offset >= blob.Length)
        {
            return string.Empty;
        }
        ReadOnlySpan<byte> s = blob.Slice(offset, System.Math.Min(max, blob.Length - offset));
        int end = s.IndexOf((byte)0);
        if (end >= 0)
        {
            s = s[..end];
        }
        return Encoding.ASCII.GetString(s);
    }
}
