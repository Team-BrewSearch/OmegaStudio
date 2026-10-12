using System.Buffers.Binary;
using System.Text;
namespace Polarbit.Formats;
internal sealed class Plate
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public List<(string Name, int X, int Y, int W, int H)> Rects { get; } = [];
    public static Plate Parse(ReadOnlySpan<byte> b)
    {
        return b.StartsWith("TLPB"u8) ? ParseTlpb(b) : ParsePlt(b);
    }
    private static Plate ParseTlpb(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 44 && Le.U32(b, 4) == 4 && Le.I32(b, 40) == -1, "TLPB header is not version 4.");
        Plate plate = new() { Width = (int)Le.U32(b, 8), Height = (int)Le.U32(b, 12) };
        int count = Le.Count(b, 36, 27, "TLPB entries");
        int p = 44;
        for (int i = 0; i < count; i++)
        {
            Le.Need(p + 26 <= b.Length, "TLPB entry runs past the data.");
            int length = Le.U16(b, p + 24);
            Le.Need(length > 0 && p + 26 + length <= b.Length, "TLPB name runs past the data.");
            string name = Encoding.Latin1.GetString(b.Slice(p + 26, length - 1));
            plate.Rects.Add((name, (int)System.Math.Round(Le.Fx(b, p) * plate.Width), (int)System.Math.Round(Le.Fx(b, p + 4) * plate.Height),
                Le.U16(b, p + 16), Le.U16(b, p + 18)));
            p += 26 + length;
        }
        Le.Need(p == b.Length, "TLPB has trailing bytes.");
        return plate;
    }
    private static Plate ParsePlt(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length >= 20 && Le.U16(b, 6) == 9 && b.Length == 20 + (40 * Le.U16(b, 4)), "PLT header does not match its rectangle count.");
        Plate plate = new() { Width = Le.U16(b, 0), Height = Le.U16(b, 2) };
        for (int i = 0; i < Le.U16(b, 4); i++)
        {
            ReadOnlySpan<byte> r = b.Slice(20 + (40 * i), 40);
            float u = BinaryPrimitives.ReadInt32BigEndian(r[8..]) / 65536f, v = BinaryPrimitives.ReadInt32BigEndian(r[12..]) / 65536f;
            plate.Rects.Add(($"#{i}", (int)System.Math.Round(u * plate.Width), (int)System.Math.Round(v * plate.Height),
                (int)BinaryPrimitives.ReadUInt32BigEndian(r), (int)BinaryPrimitives.ReadUInt32BigEndian(r[4..])));
        }
        return plate;
    }
}
