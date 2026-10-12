using System.Buffers.Binary;
namespace Polarbit.Formats;
internal sealed class PmodModule
{
    private const int HeaderSize = 42;
    private PmodModule()
    {
    }
    public int Speed { get; private init; }
    public int Bpm { get; private init; }
    public int SongLength { get; private init; }
    public int Patterns { get; private init; }
    public int Channels { get; private init; }
    public int Instruments { get; private init; }
    public byte[] Body { get; private init; } = [];
    public List<(int Offset, int Length)> Samples { get; } = [];
    public static PmodModule Parse(ReadOnlySpan<byte> b)
    {
        Le.Need(b.Length > HeaderSize && b.StartsWith("PMOD"u8) && Le.U32(b, 4) == b.Length - 8 && Le.U32(b, 8) == 30, "PMOD header is not a 30-byte module header.");
        int songLength = Le.U16(b, 18), patterns = Le.U16(b, 22), rows = Le.U16(b, 24), channels = Le.U16(b, 26);
        int samples = Le.U16(b, 30), instruments = Le.U16(b, 32);
        byte[] body = Zlib.TryInflate(b[HeaderSize..].ToArray()) ?? throw new InvalidDataException("PMOD body does not inflate.");
        PmodModule module = new()
        {
            Speed = b[16],
            Bpm = b[17],
            SongLength = songLength,
            Patterns = patterns,
            Channels = channels,
            Instruments = instruments,
            Body = body,
        };
        long p = songLength;
        long cells = 0;
        if (rows == 0)
        {
            for (int i = 0; i < patterns; i++)
            {
                Le.Need(p + 2 <= body.Length, "PMOD row table runs past the body.");
                cells += Le.U16(body, (int)p);
                p += 2;
            }
        }
        else
        {
            cells = (long)rows * patterns;
        }
        p += 5 * cells * channels;
        for (int i = 0; i < samples; i++)
        {
            Le.Need(p + 8 <= body.Length && body.AsSpan((int)p).StartsWith("PSND"u8), $"PMOD sample {i} is not a PSND block.");
            long length = 8L + Le.U32(body, (int)p + 4);
            Le.Need(p + length <= body.Length, $"PMOD sample {i} runs past the body.");
            module.Samples.Add(((int)p, (int)length));
            p += length;
        }
        return module;
    }
    public ReadOnlySpan<byte> Sample(int index)
    {
        (int offset, int length) = Samples[index];
        return Body.AsSpan(offset, length);
    }
    public byte[] WithSample(ReadOnlySpan<byte> original, int index, byte[] sample)
    {
        (int offset, int length) = Samples[index];
        byte[] body = [.. Body.AsSpan(0, offset), .. sample, .. Body.AsSpan(offset + length)];
        byte[] packed = Zlib.Deflate(body, 9);
        byte[] output = new byte[HeaderSize + packed.Length];
        original[..HeaderSize].CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)(output.Length - 8));
        packed.CopyTo(output, HeaderSize);
        return output;
    }
}
