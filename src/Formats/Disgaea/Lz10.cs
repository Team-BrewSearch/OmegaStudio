using Omega.Plugins;
namespace Disgaea.Formats;
internal static class Lz10
{
    private const int Window = 0x1000;
    private const int MinMatch = 3;
    private const int MaxMatch = 18;
    private const int MaxSize = 0xFFFFFF;
    public static bool Claims(ReadOnlySpan<byte> data)
    {
        return data.Length >= 5 && data[0] == 0x10 && (BinaryRead.U32(data, 0) >> 8) > 0;
    }
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!Claims(data))
        {
            throw new InvalidDataException("not LZ10 data");
        }
        byte[] output = new byte[BinaryRead.U32(data, 0) >> 8];
        int read = 4;
        int written = 0;
        while (written < output.Length)
        {
            BinaryRead.Need(data, read, 1);
            byte flags = data[read++];
            for (int bit = 7; bit >= 0 && written < output.Length; bit--)
            {
                if ((flags >> bit & 1) == 0)
                {
                    BinaryRead.Need(data, read, 1);
                    output[written++] = data[read++];
                    continue;
                }
                BinaryRead.Need(data, read, 2);
                int count = (data[read] >> 4) + MinMatch;
                int from = written - (((data[read] & 0xF) << 8) | data[read + 1]) - 1;
                read += 2;
                if (from < 0)
                {
                    throw new InvalidDataException($"LZ10 back-reference at output 0x{written:X} points before the start");
                }
                for (int i = 0; i < count && written < output.Length; i++)
                {
                    output[written++] = output[from + i];
                }
            }
        }
        return output;
    }
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxSize)
        {
            throw new InvalidDataException($"LZ10 holds 1 to {MaxSize} bytes, not {data.Length}");
        }
        List<byte> output = new(data.Length / 2) { 0x10, (byte)data.Length, (byte)(data.Length >> 8), (byte)(data.Length >> 16) };
        int position = 0;
        while (position < data.Length)
        {
            int flagsAt = output.Count;
            output.Add(0);
            for (int bit = 7; bit >= 0 && position < data.Length; bit--)
            {
                (int length, int distance) = Longest(data, position);
                if (length < MinMatch)
                {
                    output.Add(data[position++]);
                    continue;
                }
                output[flagsAt] |= (byte)(1 << bit);
                output.Add((byte)(((length - MinMatch) << 4) | ((distance - 1) >> 8)));
                output.Add((byte)(distance - 1));
                position += length;
            }
        }
        while (output.Count % 4 != 0)
        {
            output.Add(0);
        }
        return [.. output];
    }
    private static (int Length, int Distance) Longest(ReadOnlySpan<byte> data, int position)
    {
        int limit = Math.Min(MaxMatch, data.Length - position);
        int bestLength = 0;
        int bestDistance = 0;
        for (int distance = 2; distance <= Math.Min(Window, position); distance++)
        {
            int length = 0;
            while (length < limit && data[position - distance + length] == data[position + length])
            {
                length++;
            }
            if (length > 0 && length >= bestLength)
            {
                bestLength = length;
                bestDistance = distance;
            }
        }
        return (bestLength, bestDistance);
    }
}
