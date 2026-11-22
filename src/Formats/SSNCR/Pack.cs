using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Omega.Plugins;
namespace SSNCR.Formats;

// ── data model ──────────────────────────────────────────────────────────────

internal sealed record PackEntry(string Path, long Offset, int StoredSize, int Flags);

internal static class PackFile
{
    private static readonly byte[] Magic = "PAK\0"u8.ToArray();

    public static bool IsPack(ReadOnlySpan<byte> head)
        => head.Length >= 12
            && head[..4].SequenceEqual(Magic)
            && BinaryPrimitives.ReadInt32LittleEndian(head[8..]) == 0x0C;

    public static (IReadOnlyList<PackEntry> Entries, string Platform) Read(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        Span<byte> header = stackalloc byte[8];
        fs.ReadExactly(header);

        int infoSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        fs.Seek(0, SeekOrigin.Begin);
        byte[] info = new byte[infoSize];
        fs.ReadExactly(info);

        List<PackEntry> entries = ParseEntries(info);
        return (entries, DetectPlatform(entries));
    }

    public static (IReadOnlyList<PackEntry> Entries, string Platform) ReadFromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return ([], "unknown");
        int infoSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (infoSize < 12 || infoSize > data.Length) return ([], "unknown");
        byte[] info = data[..infoSize].ToArray();
        List<PackEntry> entries = ParseEntries(info);
        return (entries, DetectPlatform(entries));
    }

    private static List<PackEntry> ParseEntries(byte[] info)
    {
        int infoSize = info.Length;
        List<PackEntry> entries = [];
        HashSet<int> seenDirs = [];
        int dirPos = 12;

        while (dirPos != 0 && dirPos + 8 < infoSize && seenDirs.Add(dirPos))
        {
            int nextDir   = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(dirPos));
            int fileStart = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(dirPos + 4));
            string path   = ReadCStr(info, dirPos + 8);

            HashSet<int> seenFiles = [];
            int fe = fileStart;
            while (fe != 0 && fe + 16 < infoSize && seenFiles.Add(fe))
            {
                int nextFe  = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(fe));
                int size    = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(fe + 4));
                int flags   = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(fe + 8));
                int offPtr  = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(fe + 12));
                string name = ReadCStr(info, fe + 16);
                long offset = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(offPtr)) & 0xFFFFFFFFL;

                entries.Add(new PackEntry($"{path}/{name}", offset, size, flags));
                fe = nextFe;
            }
            dirPos = nextDir;
        }
        return entries;
    }

    public static byte[] LoadEntry(string filePath, PackEntry entry)
    {
        using FileStream fs = File.OpenRead(filePath);
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        byte[] raw = new byte[entry.StoredSize];
        fs.ReadExactly(raw);
        return Decompress(raw, entry.Flags);
    }

    public static byte[] LoadEntryFromBytes(byte[] packData, PackEntry entry)
    {
        long end = entry.Offset + entry.StoredSize;
        if (end > packData.Length) return [];
        byte[] raw = packData[(int)entry.Offset..(int)end];
        return Decompress(raw, entry.Flags);
    }

    private static byte[] Decompress(byte[] raw, int flags)
    {
        // PAK-level decompression: flags bit 1 (0x02) with !CMP header
        if ((flags & 2) != 0 && IsCmp(raw))
            raw = DecompressCmp(raw);

        // App-level decompression: payload itself starts with !CMP
        if (IsCmp(raw))
            raw = DecompressCmp(raw);

        return raw;
    }

    private static bool IsCmp(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..4].SequenceEqual("!CMP"u8);

    private static byte[] DecompressCmp(byte[] data)
    {
        int decompSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        byte[] result = new byte[decompSize];
        using var src = new MemoryStream(data, 8, data.Length - 8, writable: false);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        z.ReadExactly(result);
        return result;
    }

    private static string ReadCStr(byte[] buf, int pos)
    {
        int end = Array.IndexOf(buf, (byte)0, pos);
        return end < 0 ? string.Empty : Encoding.ASCII.GetString(buf, pos, end - pos);
    }

    private static string DetectPlatform(List<PackEntry>? entries)
    {
        if (entries is null) return "unknown";
        string[] roots = ["gcn/", "ps2/", "psp/"];
        string[] names = ["GameCube", "PS2", "PSP"];
        int[] counts = [.. roots.Select(r => entries.Count(e => e.Path.TrimStart('/').StartsWith(r, StringComparison.OrdinalIgnoreCase)))];
        int best = Array.IndexOf(counts, counts.Max());
        return counts[best] > 0 ? names[best] : "unknown";
    }
}
