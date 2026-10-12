using System.Buffers.Binary;
using System.Text;
namespace Polarbit.Formats;
internal sealed class LangTable
{
    public bool Utf16 { get; private init; }
    public int StringsPerLanguage { get; private init; }
    public List<(string Name, string[] Strings)> Languages { get; } = [];
    private Encoding TextEncoding => Utf16 ? Encoding.Unicode : Encoding.UTF8;
    public static LangTable Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 20 && b.StartsWith("LANG"u8), "LANG header is truncated.");
        int count = (int)Le.U32(b, 4), strings = (int)Le.U32(b, 8), encoding = (int)Le.U32(b, 12), nameLength = (int)Le.U32(b, 16);
        Le.Need(encoding is 1 or 2 && count < 256 && 20L + nameLength + (16L * count) <= b.Length, "LANG header is out of range.");
        LangTable table = new() { Utf16 = encoding == 2, StringsPerLanguage = strings };
        ReadOnlySpan<byte> names = b.Slice(20, nameLength);
        int end = 20 + nameLength + (16 * count);
        for (int i = 0; i < count; i++)
        {
            int at = 20 + nameLength + (16 * i);
            int offset = (int)Le.U32(b, at), size = (int)Le.U32(b, at + 4), packed = (int)Le.U32(b, at + 8), nameOffset = (int)Le.U32(b, at + 12);
            Le.Need(offset == end && (long)offset + packed <= b.Length && size <= Zlib.MaxInflated && nameOffset < nameLength,
                $"LANG language {i} points outside the file.");
            end += packed;
            string[] parts = table.TextEncoding.GetString(Zlib.Inflate(b.Slice(offset, packed), size)).Split('\0');
            Le.Need(parts.Length == strings + 1 && parts[^1].Length == 0, $"LANG language {i} holds {parts.Length - 1} strings, not {strings}.");
            table.Languages.Add((ReadName(names[nameOffset..], table.Utf16), parts[..^1]));
        }
        Le.Need(end == b.Length, "LANG has trailing bytes.");
        return table;
    }
    private static string ReadName(ReadOnlySpan<byte> b, bool utf16)
    {
        if (!utf16)
        {
            int nul = b.IndexOf((byte)0);
            ReadOnlySpan<byte> name = nul < 0 ? b : b[..nul];
            return Decodes(name) ? Encoding.UTF8.GetString(name) : Encoding.Latin1.GetString(name);
        }
        int k = 0;
        while (k + 1 < b.Length && (b[k] | b[k + 1]) != 0)
        {
            k += 2;
        }
        return Encoding.Unicode.GetString(b[..k]);
    }
    private static bool Decodes(ReadOnlySpan<byte> b)
    {
        System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();
        byte[] probe = b.ToArray();
        char[] one = new char[1 + (b.Length * 2)];
        decoder.Convert(probe, 0, probe.Length, one, 0, one.Length, flush: true, out _, out _, out bool usedAll);
        return usedAll;
    }
    public byte[] Build()
    {
        int width = Utf16 ? 2 : 1;
        using MemoryStream names = new();
        List<int> nameOffsets = [];
        List<(byte[] Text, byte[] Packed)> blobs = [];
        foreach ((string name, string[] strings) in Languages)
        {
            Le.Need(strings.Length == StringsPerLanguage, $"{name} has {strings.Length} strings, not {StringsPerLanguage}.");
            nameOffsets.Add((int)names.Length);
            names.Write(TextEncoding.GetBytes(name));
            names.Write(new byte[width]);
            byte[] text = TextEncoding.GetBytes(string.Concat(strings.Select(s => s + "\0")));
            blobs.Add((text, Zlib.Deflate(text, 9)));
        }
        while (names.Length % 4 != 0)
        {
            names.WriteByte(0);
        }
        int position = 20 + (int)names.Length + (16 * blobs.Count);
        byte[] output = new byte[position + blobs.Sum(x => x.Packed.Length)];
        Span<byte> o = output;
        "LANG"u8.CopyTo(o);
        BinaryPrimitives.WriteUInt32LittleEndian(o[4..], (uint)blobs.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(o[8..], (uint)StringsPerLanguage);
        BinaryPrimitives.WriteUInt32LittleEndian(o[12..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(o[16..], (uint)names.Length);
        names.ToArray().CopyTo(output, 20);
        for (int i = 0; i < blobs.Count; i++)
        {
            Span<byte> row = o[(20 + (int)names.Length + (16 * i))..];
            BinaryPrimitives.WriteUInt32LittleEndian(row, (uint)position);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)blobs[i].Text.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], (uint)blobs[i].Packed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], (uint)nameOffsets[i]);
            blobs[i].Packed.CopyTo(output, position);
            position += blobs[i].Packed.Length;
        }
        return output;
    }
}
internal sealed class FcolTable
{
    public List<(string Key, string Value)> Strings { get; } = [];
    public static FcolTable Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 8 && b.StartsWith("FCOL"u8), "FCOL header is truncated.");
        int count = Le.Count(b, 4, 8, "FCOL table");
        FcolTable table = new();
        int p = 8 + (8 * count);
        string[] keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            Le.Need(Le.U32(b, 8 + (8 * i)) == p, $"FCOL key {i} is not where the previous one ended.");
            int nul = b[p..].IndexOf((byte)0);
            Le.Need(nul >= 0, "FCOL key is not terminated.");
            keys[i] = Encoding.ASCII.GetString(b.Slice(p, nul));
            p += nul + 1;
        }
        if ((p & 1) != 0)
        {
            Le.Need(b[p] == 0xFF, "FCOL pad byte is not 0xFF.");
            p++;
        }
        for (int i = 0; i < count; i++)
        {
            Le.Need(Le.U32(b, 12 + (8 * i)) == p, $"FCOL value {i} is not where the previous one ended.");
            int end = p;
            while (end + 1 < b.Length && (b[end] | b[end + 1]) != 0)
            {
                end += 2;
            }
            Le.Need(end + 1 < b.Length, "FCOL value is not terminated.");
            table.Strings.Add((keys[i], Encoding.Unicode.GetString(b[p..end])));
            p = end + 2;
        }
        Le.Need(p == b.Length, "FCOL has trailing bytes.");
        return table;
    }
    public byte[] Build()
    {
        byte[][] keys = [.. Strings.Select(s => Encoding.ASCII.GetBytes(s.Key + "\0"))];
        byte[][] values = [.. Strings.Select(s => Encoding.Unicode.GetBytes(s.Value + "\0"))];
        int keyStart = 8 + (8 * keys.Length);
        int keyEnd = keyStart + keys.Sum(k => k.Length);
        int pad = keyEnd & 1;
        byte[] output = new byte[keyEnd + pad + values.Sum(v => v.Length)];
        "FCOL"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)keys.Length);
        int k = keyStart, v = keyEnd + pad;
        for (int i = 0; i < keys.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8 + (8 * i)), (uint)k);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12 + (8 * i)), (uint)v);
            keys[i].CopyTo(output, k);
            values[i].CopyTo(output, v);
            k += keys[i].Length;
            v += values[i].Length;
        }
        if (pad != 0)
        {
            output[keyEnd] = 0xFF;
        }
        return output;
    }
}
