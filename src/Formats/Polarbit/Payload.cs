using System.Buffers.Binary;
using System.Text;
namespace Polarbit.Formats;

internal enum PayloadKind
{
    Binary,
    Png,
    Pvr,
    Piff,
    Psnd,
    Pfnt,
    Lang,
    Pmod,
    Fcol,
    Tlpb,
    Pxc,
    Cfb,
    Plt,
    Xml,
    Text,
}

internal static class Kinds
{
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> PngMagic => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> PvrHeaderSize => [52, 0, 0, 0];
    private static ReadOnlySpan<byte> CfbMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    public static PayloadKind Sniff(ReadOnlySpan<byte> p)
    {
        if (p.StartsWith(PngMagic))
        {
            return PayloadKind.Png;
        }
        if (p.Length >= 52 && p.StartsWith(PvrHeaderSize) && p.Slice(44, 4).SequenceEqual("PVR!"u8))
        {
            return PayloadKind.Pvr;
        }
        if (p.Length >= 4)
        {
            switch (Encoding.Latin1.GetString(p[..4]))
            {
                case "PIFF": return PayloadKind.Piff;
                case "PSND": return PayloadKind.Psnd;
                case "PFNT": return PayloadKind.Pfnt;
                case "LANG": return PayloadKind.Lang;
                case "PMOD": return PayloadKind.Pmod;
                case "FCOL": return PayloadKind.Fcol;
                case "TLPB": return PayloadKind.Tlpb;
                case "M<+\x1A": return PayloadKind.Pxc;
                default: break;
            }
        }
        if (p.StartsWith(CfbMagic))
        {
            return PayloadKind.Cfb;
        }
        if (p.Length >= 20 && p.Length == 20 + (40 * Le.U16(p, 4)) && Le.U32(p, 8) == (uint)(Le.U16(p, 0) * Le.U16(p, 2)))
        {
            return PayloadKind.Plt;
        }
        int start = 0;
        while (start < p.Length && p[start] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C)
        {
            start++;
        }
        if (start < p.Length && p[start] == (byte)'<')
        {
            return PayloadKind.Xml;
        }
        try
        {
            _ = s_strictUtf8.GetCharCount(p);
            return PayloadKind.Text;
        }
        catch (DecoderFallbackException)
        {
            return PayloadKind.Binary;
        }
    }

    public static string Extension(PayloadKind kind)
    {
        return kind switch
        {
            PayloadKind.Png => ".png",
            PayloadKind.Pvr => ".pvr",
            PayloadKind.Piff => ".pof",
            PayloadKind.Psnd => ".psn",
            PayloadKind.Pfnt => ".fnt",
            PayloadKind.Lang => ".bin",
            PayloadKind.Pmod => ".pmod",
            PayloadKind.Fcol => ".fcol",
            PayloadKind.Tlpb or PayloadKind.Plt => ".plt",
            PayloadKind.Pxc => ".pxc",
            PayloadKind.Cfb => ".db",
            PayloadKind.Xml => ".xml",
            PayloadKind.Text => ".txt",
            _ => ".bin",
        };
    }

    public static string Describe(PayloadKind kind)
    {
        return kind switch
        {
            PayloadKind.Png => "PNG image",
            PayloadKind.Pvr => "PVR texture",
            PayloadKind.Piff => "PIFF model or track",
            PayloadKind.Psnd => "PSND sound",
            PayloadKind.Pfnt => "PFNT bitmap font",
            PayloadKind.Lang => "LANG string table",
            PayloadKind.Pmod => "PMOD music module",
            PayloadKind.Fcol => "FCOL string table",
            PayloadKind.Tlpb => "TLPB texture plate",
            PayloadKind.Plt => "PLT texture plate",
            PayloadKind.Pxc => "PXC scene graph",
            PayloadKind.Cfb => "OLE compound file (Thumbs.db)",
            PayloadKind.Xml => "XML text",
            PayloadKind.Text => "Text",
            _ => "Binary",
        };
    }
}

internal static class Le
{
    public static ushort U16(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    }
    public static short I16(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(b[o..]);
    }
    public static uint U32(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    }
    public static int I32(ReadOnlySpan<byte> b, int o)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    }
    public static float Fx(ReadOnlySpan<byte> b, int o)
    {
        return I32(b, o) / 65536f;
    }
    public static int Count(ReadOnlySpan<byte> b, int o, int recordSize, string what)
    {
        uint count = U32(b, o);
        return count > (uint)((b.Length - o) / System.Math.Max(1, recordSize))
            ? throw new InvalidDataException($"{what} at 0x{o:X} claims {count} records, more than the data holds.")
            : (int)count;
    }
    public static void Need(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
    public static int ToFixed(float value)
    {
        double scaled = System.Math.Round(value * 65536.0);
        return scaled is < int.MinValue or > int.MaxValue
            ? throw new InvalidDataException($"{value} does not fit a 16.16 fixed-point number.")
            : (int)scaled;
    }
}

/// <summary>
/// The engine is OpenGL ES: right-handed, Y up, texture v running upward. The host wants v running down.
/// Positions pass through unchanged: a Y-up to Z-up turn (+90 degrees about X) showed every model lying on its back
/// in the viewport, and the -90 degrees that stands it up again cancels it.
/// </summary>
internal static class Axes
{
    public static (float X, float Y, float Z) ToHost(float x, float y, float z)
    {
        return (x, y, z);
    }
    public static (float X, float Y, float Z) FromHost(float x, float y, float z)
    {
        return (x, y, z);
    }
    public static float FlipV(float v)
    {
        return 1f - v;
    }
}
