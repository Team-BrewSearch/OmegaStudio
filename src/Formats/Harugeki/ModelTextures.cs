using System.Buffers.Binary;
using Omega.Plugins;
namespace Harugeki.Formats;
public static class ModelTextures
{
    private const int TableOffset = 0x10;
    private const int TableEnd = RingModel.NodeTableOffset;
    private const int NodeTexturePointer = 0x24;
    public static byte[] Replace(ReadOnlySpan<byte> blob, int textureOffset, ReadOnlySpan<byte> encoded)
    {
        if (!RingModel.LooksLikeModel(blob))
        {
            throw new ArgumentException("not a model blob", nameof(blob));
        }
        if (!RingTexture.IsTexture(encoded))
        {
            throw new ArgumentException("replacement is not a ringtex2 blob", nameof(encoded));
        }
        int[] table = ReadTable(blob);
        int limit = blob.Length;
        int[] live = [.. table.Where(o => o > 0 && o < limit).Distinct().Order()];
        if (!live.Contains(textureOffset))
        {
            throw new ArgumentException(
                $"no embedded texture starts at 0x{textureOffset:X}", nameof(textureOffset));
        }
        int regionStart = live[0];
        int[] sizes = MeasureTextures(blob, live, textureOffset, encoded.Length);
        int oldTail = live[^1] + SizeOf(blob, live[^1]);
        ReadOnlySpan<byte> trailing = blob[oldTail..];
        Dictionary<int, int> moveTo = [];
        int at = regionStart;
        for (int i = 0; i < live.Length; i++)
        {
            moveTo[live[i]] = at;
            at += sizes[i];
        }
        byte[] result = new byte[at + trailing.Length];
        blob[..regionStart].CopyTo(result);
        for (int i = 0; i < live.Length; i++)
        {
            Span<byte> slot = result.AsSpan(moveTo[live[i]], sizes[i]);
            if (live[i] == textureOffset)
            {
                encoded.CopyTo(slot);
            }
            else
            {
                blob.Slice(live[i], sizes[i]).CopyTo(slot);
            }
        }
        trailing.CopyTo(result.AsSpan(at));
        Repoint(result, table, moveTo);
        VerifyNoStaleReferences(result, regionStart, moveTo);
        return result;
    }
    private static int[] ReadTable(ReadOnlySpan<byte> blob)
    {
        int count = (int)BinaryRead.U32(blob, 0x0C);
        if (TableOffset + (count * 4) > TableEnd)
        {
            throw new ArgumentException($"texture table of {count} does not fit the header", nameof(blob));
        }
        int[] table = new int[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = (int)BinaryRead.U32(blob, TableOffset + (i * 4));
        }
        return table;
    }
    private static int[] MeasureTextures(
        ReadOnlySpan<byte> blob, int[] live, int replacedAt, int replacedSize)
    {
        int[] sizes = new int[live.Length];
        for (int i = 0; i < live.Length; i++)
        {
            int available = (i + 1 < live.Length ? live[i + 1] : blob.Length) - live[i];
            sizes[i] = live[i] == replacedAt ? replacedSize : SizeOf(blob, live[i]);
            if (live[i] != replacedAt && sizes[i] > available)
            {
                throw new InvalidDataException(
                    $"embedded texture at 0x{live[i]:X} declares {sizes[i]} bytes but only {available} are available");
            }
        }
        return sizes;
    }
    private static int SizeOf(ReadOnlySpan<byte> blob, int offset)
    {
        ReadOnlySpan<byte> at = blob[offset..];
        if (!RingTexture.IsTexture(at))
        {
            throw new InvalidDataException($"no ringtex2 header at 0x{offset:X}");
        }
        int size;
        try
        {
            size = RingTexture.EncodedSize(at);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"embedded texture at 0x{offset:X}: {ex.Message}", ex);
        }
        return size > blob.Length - offset
            ? throw new InvalidDataException(
                $"embedded texture at 0x{offset:X} claims {size} bytes, past the end of the blob")
            : size;
    }
    private static void Repoint(byte[] blob, int[] table, Dictionary<int, int> moveTo)
    {
        for (int i = 0; i < table.Length; i++)
        {
            if (moveTo.TryGetValue(table[i], out int moved))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    blob.AsSpan(TableOffset + (i * 4)), (uint)moved);
            }
        }
        foreach (int site in PointerSites(blob))
        {
            int value = (int)BinaryRead.U32(blob, site);
            if (moveTo.TryGetValue(value, out int moved))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(site), (uint)moved);
            }
        }
    }
    private static IEnumerable<int> PointerSites(byte[] blob)
    {
        int nodeCount = (int)BinaryRead.U32(blob, 4);
        for (int i = 0; i < nodeCount; i++)
        {
            int node = (int)BinaryRead.U32(blob, RingModel.NodeTableOffset + (i * 4));
            if (node > 0 && node + NodeTexturePointer + 4 <= blob.Length)
            {
                yield return node + NodeTexturePointer;
            }
        }
    }
    private static void VerifyNoStaleReferences(byte[] blob, int regionStart, Dictionary<int, int> moveTo)
    {
        HashSet<int> stale = [.. moveTo.Where(m => m.Key != m.Value).Select(m => m.Key)];
        if (stale.Count == 0)
        {
            return;
        }
        HashSet<int> known = [.. PointerSites(blob)];
        for (int i = 0; i + 4 <= regionStart; i += 4)
        {
            if ((i >= TableOffset && i < TableEnd) || known.Contains(i))
            {
                continue;
            }
            if (stale.Contains((int)BinaryRead.U32(blob, i)))
            {
                throw new InvalidDataException(
                    $"model holds an unrecognised reference to a moved texture at 0x{i:X}; " +
                    "resizing this texture would leave it dangling");
            }
        }
    }
}
