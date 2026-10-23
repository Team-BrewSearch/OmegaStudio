using System.Buffers.Binary;
using System.Text;
namespace DWSSK.Formats.Core;
internal sealed class StructException(string message) : Exception(message);
internal sealed class PyIndexException(string message) : Exception(message);
internal static class Failure
{
    public static bool IsData(Exception ex)
    {
        return ex is InvalidDataException or StructException or PyIndexException or Models.ValueException
            or ArgumentException or IndexOutOfRangeException or KeyNotFoundException or InvalidCastException
            or OverflowException or FormatException or IOException or NotSupportedException;
    }
}
internal static class Bin
{
    public static void Need(ReadOnlySpan<byte> b, int o, long n)
    {
        if (o < 0 || n < 0 || o + n > b.Length)
        {
            throw new StructException(
                $"reading {n} byte(s) at 0x{Math.Max(0, o):X} runs past the end of a {b.Length}-byte blob");
        }
    }
    public static byte U8(ReadOnlySpan<byte> b, int o)
    {
        Need(b, o, 1);
        return b[o];
    }
    public static sbyte I8(ReadOnlySpan<byte> b, int o)
    {
        return (sbyte)U8(b, o);
    }
    public static ushort U16(ReadOnlySpan<byte> b, int o, bool big)
    {
        Need(b, o, 2);
        return big ? BinaryPrimitives.ReadUInt16BigEndian(b[o..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }
    public static short I16(ReadOnlySpan<byte> b, int o, bool big)
    {
        return (short)U16(b, o, big);
    }
    public static uint U32(ReadOnlySpan<byte> b, int o, bool big)
    {
        Need(b, o, 4);
        return big ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }
    public static int I32(ReadOnlySpan<byte> b, int o, bool big)
    {
        return (int)U32(b, o, big);
    }
    public static ulong U64(ReadOnlySpan<byte> b, int o, bool big)
    {
        Need(b, o, 8);
        return big ? BinaryPrimitives.ReadUInt64BigEndian(b[o..]) : BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    }
    public static float F32(ReadOnlySpan<byte> b, int o, bool big)
    {
        return BitConverter.UInt32BitsToSingle(U32(b, o, big));
    }
    public static double F64(ReadOnlySpan<byte> b, int o, bool big)
    {
        return BitConverter.UInt64BitsToDouble(U64(b, o, big));
    }
    public static uint[] U32s(ReadOnlySpan<byte> b, int o, int n, bool big)
    {
        Need(b, o, 4L * n);
        uint[] v = new uint[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = U32(b, o + (4 * i), big);
        }
        return v;
    }
    public static float[] F32s(ReadOnlySpan<byte> b, int o, int n, bool big)
    {
        Need(b, o, 4L * n);
        float[] v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = F32(b, o + (4 * i), big);
        }
        return v;
    }
    public static void PutU16(Span<byte> b, int o, uint v, bool big)
    {
        if (v > ushort.MaxValue)
        {
            throw new StructException($"{v} does not fit in an unsigned 16-bit field");
        }
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(b[o..], (ushort)v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b[o..], (ushort)v);
        }
    }
    public static void PutU32(Span<byte> b, int o, uint v, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b[o..], v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b[o..], v);
        }
    }
    public static void PutF32(Span<byte> b, int o, double v, bool big)
    {
        PutU32(b, o, BitConverter.SingleToUInt32Bits(Py.ToSingle(v)), big);
    }
    public static string Latin1(ReadOnlySpan<byte> b)
    {
        return Encoding.Latin1.GetString(b);
    }
    public static byte[] Latin1(string s)
    {
        foreach (char c in s)
        {
            if (c > 0xFF)
            {
                throw new InvalidDataException($"'{s}' has a character that Latin-1 cannot hold");
            }
        }
        return Encoding.Latin1.GetBytes(s);
    }
    public static string CString(ReadOnlySpan<byte> b, int o)
    {
        if (o < 0 || o > b.Length)
        {
            throw new InvalidDataException($"string at 0x{o:X} is outside the {b.Length}-byte blob");
        }
        int end = b[o..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException($"string at 0x{o:X} has no terminating NUL");
        }
        return Latin1(b.Slice(o, end));
    }
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> b, int start, int end)
    {
        start = Math.Clamp(start, 0, b.Length);
        end = Math.Clamp(end, 0, b.Length);
        return end <= start ? [] : b[start..end];
    }
    public static string Hex(ReadOnlySpan<byte> b)
    {
        return Convert.ToHexStringLower(b);
    }
    public static byte[] FromHex(string s)
    {
        return Convert.FromHexString(s);
    }
    public static bool AnyNonZero(ReadOnlySpan<byte> b)
    {
        return b.IndexOfAnyExcept((byte)0) >= 0;
    }
    public static int Align(int n, int a)
    {
        return (n + a - 1) / a * a;
    }
    public static int FloorDiv(int n, int d)
    {
        int q = n / d;
        return (n % d != 0) && ((n < 0) != (d < 0)) ? q - 1 : q;
    }
    public static int CeilDiv(int n, int d)
    {
        return -FloorDiv(-n, d);
    }
    public static int Mod(int n, int d)
    {
        int r = n % d;
        return r != 0 && ((r < 0) != (d < 0)) ? r + d : r;
    }
}
internal sealed class ByteWriter
{
    private byte[] _buffer;
    public ByteWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }
    public int Length { get; private set; }
    public Span<byte> Span => _buffer.AsSpan(0, Length);
    public byte this[int index]
    {
        get => Span[index];
        set => Span[index] = value;
    }
    private void Grow(int need)
    {
        if (Length + need <= _buffer.Length)
        {
            return;
        }
        int size = _buffer.Length;
        while (size < Length + need)
        {
            size = size < (1 << 29) ? size * 2 : Math.Max(Length + need, size + (1 << 28));
        }
        Array.Resize(ref _buffer, size);
    }
    public void Add(byte v)
    {
        Grow(1);
        _buffer[Length++] = v;
    }
    public void Add(ReadOnlySpan<byte> v)
    {
        Grow(v.Length);
        v.CopyTo(_buffer.AsSpan(Length));
        Length += v.Length;
    }
    public void Zeros(int n)
    {
        if (n <= 0)
        {
            return;
        }
        Grow(n);
        _buffer.AsSpan(Length, n).Clear();
        Length += n;
    }
    public void Fill(byte v, int n)
    {
        if (n <= 0)
        {
            return;
        }
        Grow(n);
        _buffer.AsSpan(Length, n).Fill(v);
        Length += n;
    }
    public void Pad(int align)
    {
        Zeros(Bin.Mod(-Length, align));
    }
    public void SetLength(int n)
    {
        if (n > Length)
        {
            Zeros(n - Length);
        }
        else
        {
            Length = n;
        }
    }
    public void U16(uint v, bool big)
    {
        Grow(2);
        Bin.PutU16(_buffer, Length, v, big);
        Length += 2;
    }
    public void U32(uint v, bool big)
    {
        Grow(4);
        Bin.PutU32(_buffer, Length, v, big);
        Length += 4;
    }
    public void I32(int v, bool big)
    {
        U32((uint)v, big);
    }
    public void F32(double v, bool big)
    {
        Grow(4);
        Bin.PutF32(_buffer, Length, v, big);
        Length += 4;
    }
    public void F64(double v, bool big)
    {
        Grow(8);
        ulong bits = BitConverter.DoubleToUInt64Bits(v);
        if (big)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(Length), bits);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(Length), bits);
        }
        Length += 8;
    }
    public void PatchU32(int at, uint v, bool big)
    {
        Bin.PutU32(Span, at, v, big);
    }
    public void PatchU16(int at, uint v, bool big)
    {
        Bin.PutU16(Span, at, v, big);
    }
    public void Write(int at, ReadOnlySpan<byte> v)
    {
        if (at + v.Length > Length)
        {
            SetLength(at + v.Length);
        }
        v.CopyTo(_buffer.AsSpan(at));
    }
    public byte[] ToArray()
    {
        return _buffer.AsSpan(0, Length).ToArray();
    }
}
