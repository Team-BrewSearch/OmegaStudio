using DWSSK.Formats.Core;
namespace DWSSK.Formats.Wad;
internal static class Tjzip
{
    private const int MaxDist = 0x7FFF;
    private const int MaxLen = 0xFFFF;
    public static long MaxOutput(int compressed) => ((compressed / 6L) + 1) * MaxLen;
    public static byte[] Decompress(ReadOnlySpan<byte> comp, int size)
    {
        if (size < 0 || size > MaxOutput(comp.Length))
        {
            throw new InvalidDataException(
                $"a {comp.Length}-byte TJZIP stream cannot hold the {size} bytes its header declares");
        }
        byte[] output = new byte[size];
        int n = 0;
        int i = Literal(comp, 0, output, ref n);
        while (n < size)
        {
            int b1 = At(comp, i);
            int b2 = At(comp, i + 1);
            int length;
            int dist;
            int post;
            if (b1 < 0xC0)
            {
                (length, dist, post, i) = ((b1 >> 5) + 4, ((b1 & 0x0C) << 6) | b2, b1 & 3, i + 2);
            }
            else if (b1 < 0xE0)
            {
                (length, dist, post, i) = ((b1 & 0x1F) + 4, ((b2 & 0xFC) << 6) | At(comp, i + 2), b2 & 3, i + 3);
            }
            else
            {
                int hi = (b1 & 0x10) << 10;
                if ((b1 & 0x0F) != 0)
                {
                    (length, dist, post, i) = ((b1 & 0x0F) + 3, hi | ((b2 & 0xFC) << 6) | At(comp, i + 2), b2 & 3, i + 3);
                }
                else if (b2 != 0)
                {
                    int b3 = At(comp, i + 2);
                    (length, dist, post, i) = (b2 + 0x12, hi | ((b3 & 0xFC) << 6) | At(comp, i + 3), b3 & 3, i + 4);
                }
                else
                {
                    int b5 = At(comp, i + 4);
                    (length, dist, post, i) = ((At(comp, i + 2) << 8) | At(comp, i + 3), hi | ((b5 & 0xFC) << 6) | At(comp, i + 5), b5 & 3, i + 6);
                }
            }
            if (dist <= 0 || dist > n)
            {
                throw new InvalidDataException($"TJZIP back-reference {dist} beyond the {n} bytes decoded");
            }
            int start = n - dist;
            int copy = Math.Min(length, size - n);
            for (int k = 0; k < copy; k++)
            {
                output[n + k] = output[start + k];
            }
            n += copy;
            if (n >= size)
            {
                break;
            }
            if (post == 0)
            {
                i = Literal(comp, i, output, ref n);
            }
            else if (post < 3)
            {
                int take = Math.Min(post, size - n);
                Bin.Need(comp, i, take);
                comp.Slice(i, take).CopyTo(output.AsSpan(n));
                n += take;
                i += post;
            }
        }
        return output;
    }
    private static int At(ReadOnlySpan<byte> comp, int i)
    {
        return i < comp.Length
            ? comp[i]
            : throw new InvalidDataException($"TJZIP stream ends at 0x{comp.Length:X} in the middle of a code");
    }
    private static int Literal(ReadOnlySpan<byte> comp, int i, byte[] output, ref int n)
    {
        int count = At(comp, i);
        if (count != 0)
        {
            count += 2;
            i++;
        }
        else
        {
            count = At(comp, i + 1) | (At(comp, i + 2) << 8);
            i += 3;
        }
        int take = Math.Min(count, output.Length - n);
        if (i + take > comp.Length)
        {
            throw new InvalidDataException($"TJZIP literal run at 0x{i:X} runs past the end of the stream");
        }
        comp.Slice(i, take).CopyTo(output.AsSpan(n));
        n += take;
        return i + count;
    }
    private static void DictCode(ByteWriter output, int length, int dist, int post)
    {
        if (length <= 9 && dist <= 0x3FF)
        {
            output.Add((byte)(((length - 4) << 5) | ((dist >> 8) << 2) | post));
            output.Add((byte)(dist & 0xFF));
            return;
        }
        if (length <= 35 && dist <= 0x3FFF)
        {
            output.Add((byte)(0xC0 | (length - 4)));
            output.Add((byte)(((dist >> 8) << 2) | post));
            output.Add((byte)(dist & 0xFF));
            return;
        }
        int hi = 0xE0 | ((dist >> 14) << 4);
        int lo = (((dist >> 8) & 0x3F) << 2) | post;
        if (length <= 18)
        {
            output.Add((byte)(hi | (length - 3)));
            output.Add((byte)lo);
            output.Add((byte)(dist & 0xFF));
            return;
        }
        if (length <= 0x111)
        {
            output.Add((byte)hi);
            output.Add((byte)(length - 0x12));
            output.Add((byte)lo);
            output.Add((byte)(dist & 0xFF));
            return;
        }
        output.Add((byte)hi);
        output.Add(0);
        output.Add((byte)(length >> 8));
        output.Add((byte)(length & 0xFF));
        output.Add((byte)lo);
        output.Add((byte)(dist & 0xFF));
    }
    private static void RawBlock(ByteWriter output, ReadOnlySpan<byte> lit)
    {
        while (lit.Length > 0xFFFF)
        {
            output.Add(0);
            output.Add(0xFF);
            output.Add(0xFF);
            output.Add(lit[..0xFFFF]);
            output.Add([0xE0, 0, 0, 0, 0, 1]);
            lit = lit[0xFFFF..];
        }
        int n = lit.Length;
        if (n is >= 3 and <= 257)
        {
            output.Add((byte)(n - 2));
        }
        else
        {
            output.Add(0);
            output.Add((byte)(n & 0xFF));
            output.Add((byte)(n >> 8));
        }
        output.Add(lit);
    }
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        int n = data.Length;
        int[] table = new int[1 << 24];
        Array.Fill(table, -0x10000);
        ByteWriter output = new(Math.Max(256, n / 2));
        int litStart = 0;
        int pendingLength = -1;
        int pendingDist = 0;
        int i = 0;
        while (i + 4 < n)
        {
            uint h = 0x811C9DC5;
            h = (h ^ data[i]) * 0x01000193;
            h = (h ^ data[i + 1]) * 0x01000193;
            h = (h ^ data[i + 2]) * 0x01000193;
            h = (h ^ data[i + 3]) * 0x01000193;
            int slot = (int)(((h >> 24) ^ h) & 0xFFFFFF);
            int c = table[slot];
            if (i - c > MaxDist || (i - c >= 4 && !data.Slice(c, 4).SequenceEqual(data.Slice(i, 4))))
            {
                table[slot] = i;
            }
            else if (i - c >= 4)
            {
                int limit = Math.Min(MaxLen, Math.Min(n - 1 - i, i - c));
                int k = 4;
                while (k < limit && data[c + k] == data[i + k])
                {
                    k++;
                }
                Flush(output, data, litStart, i, pendingLength, pendingDist);
                pendingLength = k;
                pendingDist = i - c;
                i += k;
                litStart = i;
                continue;
            }
            i++;
        }
        Flush(output, data, litStart, n, pendingLength, pendingDist);
        return output.ToArray();
    }
    private static void Flush(ByteWriter output, ReadOnlySpan<byte> data, int litStart, int litEnd, int length, int dist)
    {
        ReadOnlySpan<byte> lit = data[litStart..litEnd];
        if (length < 0)
        {
            RawBlock(output, lit);
        }
        else if (lit.Length == 0)
        {
            DictCode(output, length, dist, 3);
        }
        else if (lit.Length <= 2)
        {
            DictCode(output, length, dist, lit.Length);
            output.Add(lit);
        }
        else
        {
            DictCode(output, length, dist, 0);
            RawBlock(output, lit);
        }
    }
}
