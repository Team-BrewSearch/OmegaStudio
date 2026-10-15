using System.Text;

namespace DWSSK.Formats.Core;

internal static class Hashes
{
    private static readonly uint[] s_table = BuildTable();

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint start = 0)
    {
        uint c = start ^ 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            c = s_table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    public static uint Crc32(string latin1)
    {
        return Crc32(Bin.Latin1(latin1));
    }

    public static uint UpperCrc(string name)
    {
        return Crc32(Bin.Latin1(Py.Upper(name)));
    }

    public static bool TryUpperCrc(string name, out uint hash)
    {
        string upper = Py.Upper(name);
        foreach (char c in upper)
        {
            if (c > 0xFF)
            {
                hash = 0;
                return false;
            }
        }
        hash = Crc32(System.Text.Encoding.Latin1.GetBytes(upper));
        return true;
    }

    public static uint Fnv1(ReadOnlySpan<byte> data)
    {
        uint h = 0x811C9DC5;
        foreach (byte c in data)
        {
            h = unchecked(h * 0x01000193) ^ c;
        }
        return h;
    }

    public static uint WwiseId(string name)
    {
        return Fnv1(Encoding.Latin1.GetBytes(Py.Lower(name)));
    }
}
