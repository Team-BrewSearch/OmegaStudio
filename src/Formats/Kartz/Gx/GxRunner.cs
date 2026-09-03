using System.Buffers.Binary;
namespace Kartz.Formats.Gx;

public readonly record struct GxVertex(
    float X, float Y, float Z,
    float Nx, float Ny, float Nz,
    float U, float V,
    byte R, byte G, byte B,
    bool Textured);
public interface IGxSink
{
    void Begin(int mode);
    void TextureParam(uint param, int byteOffset);
    int Vertex(GxVertex vertex);
    void Triangle(int a, int b, int c, int mode);
}
public static class GxRunner
{
    private static readonly Dictionary<byte, int> s_params = new()
    {
        [0x00] = 0,
        [0x10] = 1,
        [0x11] = 0,
        [0x12] = 1,
        [0x13] = 12,
        [0x14] = 16,
        [0x15] = 12,
        [0x16] = 9,
        [0x17] = 3,
        [0x18] = 1,
        [0x19] = 1,
        [0x1A] = 1,
        [0x1B] = 3,
        [0x1C] = 3,
        [0x20] = 1,
        [0x21] = 1,
        [0x22] = 1,
        [0x23] = 2,
        [0x24] = 1,
        [0x25] = 1,
        [0x26] = 1,
        [0x27] = 1,
        [0x28] = 1,
        [0x29] = 1,
        [0x2A] = 1,
        [0x2B] = 1,
        [0x30] = 1,
        [0x31] = 1,
        [0x32] = 1,
        [0x33] = 1,
        [0x34] = 32,
        [0x40] = 1,
        [0x41] = 0,
        [0x50] = 1,
        [0x60] = 1,
        [0x70] = 3,
        [0x71] = 2,
        [0x72] = 1,
    };
    private const byte CmdMtxIdentity = 0x11;
    private const byte CmdMtxScale = 0x1B;
    private const byte CmdMtxTrans = 0x1C;
    private const byte CmdColor = 0x20;
    private const byte CmdNormal = 0x21;
    private const byte CmdTexcoord = 0x22;
    private const byte CmdVtx16 = 0x23;
    private const byte CmdVtx10 = 0x24;
    private const byte CmdVtxXy = 0x25;
    private const byte CmdVtxXz = 0x26;
    private const byte CmdVtxYz = 0x27;
    private const byte CmdVtxDiff = 0x28;
    private const byte CmdTeximageParam = 0x2A;
    private const byte CmdBeginVtxs = 0x40;
    private const byte CmdEndVtxs = 0x41;
    public static void Run(ReadOnlySpan<byte> displayList, IGxSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        byte[] words = displayList.ToArray();
        int wordCount = words.Length / 4;
        int word = 0;
        float cx = 0, cy = 0, cz = 0;
        float nx = 0, ny = 0, nz = 0;
        float tu = 0, tv = 0;
        byte cr = 255, cg = 255, cb = 255;
        float sx = 1, sy = 1, sz = 1;
        float tx = 0, ty = 0, tz = 0;
        int mode = 0;
        bool textured = false;
        List<int> strip = new(64);
        uint Read()
        {
            return word < wordCount
                ? BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(word++ * 4))
                : 0u;
        }
        void Emit()
        {
            int index = sink.Vertex(new GxVertex(
                (cx * sx) + tx, (cy * sy) + ty, (cz * sz) + tz,
                nx, ny, nz, tu, tv, cr, cg, cb, textured));
            strip.Add(index);
            textured = false;
        }
        while (word < wordCount)
        {
            uint pack = Read();
            for (int packed = 0; packed < 4; packed++)
            {
                byte cmd = (byte)((pack >> (packed * 8)) & 0xFF);
                int paramByteOff = word * 4;
                int count = s_params.TryGetValue(cmd, out int found) ? found : 0;
                uint p0 = count >= 1 ? Read() : 0u;
                uint p1 = count >= 2 ? Read() : 0u;
                uint p2 = count >= 3 ? Read() : 0u;
                for (int extra = 3; extra < count; extra++)
                {
                    _ = Read();
                }
                switch (cmd)
                {
                    case CmdMtxIdentity:
                        sx = sy = sz = 1;
                        tx = ty = tz = 0;
                        break;
                    case CmdMtxTrans:
                        tx = (int)p0 / 4096f;
                        ty = (int)p1 / 4096f;
                        tz = (int)p2 / 4096f;
                        break;
                    case CmdMtxScale:
                        sx = (int)p0 / 4096f;
                        sy = (int)p1 / 4096f;
                        sz = (int)p2 / 4096f;
                        break;
                    case CmdColor:
                        {
                            uint packedColor = p0 & 0x7FFF;
                            cr = Expand(packedColor & 0x1F);
                            cg = Expand((packedColor >> 5) & 0x1F);
                            cb = Expand((packedColor >> 10) & 0x1F);
                            break;
                        }
                    case CmdNormal:
                        nx = Signed10(p0 & 0x3FF) / 512f;
                        ny = Signed10((p0 >> 10) & 0x3FF) / 512f;
                        nz = Signed10((p0 >> 20) & 0x3FF) / 512f;
                        break;
                    case CmdTexcoord:
                        tu = (short)(p0 & 0xFFFF) / 16f;
                        tv = (short)((p0 >> 16) & 0xFFFF) / 16f;
                        textured = true;
                        break;
                    case CmdVtx16:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cy = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        cz = (short)(p1 & 0xFFFF) / 4096f;
                        Emit();
                        break;
                    case CmdVtx10:
                        cx = Signed10(p0 & 0x3FF) / 64f;
                        cy = Signed10((p0 >> 10) & 0x3FF) / 64f;
                        cz = Signed10((p0 >> 20) & 0x3FF) / 64f;
                        Emit();
                        break;
                    case CmdVtxXy:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cy = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        Emit();
                        break;
                    case CmdVtxXz:
                        cx = (short)(p0 & 0xFFFF) / 4096f;
                        cz = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        Emit();
                        break;
                    case CmdVtxYz:
                        cy = (short)(p0 & 0xFFFF) / 4096f;
                        cz = (short)((p0 >> 16) & 0xFFFF) / 4096f;
                        Emit();
                        break;
                    case CmdVtxDiff:
                        cx += Signed10(p0 & 0x3FF) / 4096f;
                        cy += Signed10((p0 >> 10) & 0x3FF) / 4096f;
                        cz += Signed10((p0 >> 20) & 0x3FF) / 4096f;
                        Emit();
                        break;
                    case CmdTeximageParam:
                        sink.TextureParam(p0, paramByteOff);
                        break;
                    case CmdBeginVtxs:
                        mode = (int)(p0 & 0x3);
                        strip.Clear();
                        sink.Begin(mode);
                        break;
                    case CmdEndVtxs:
                        break;
                }
                if (cmd is CmdVtx16 or CmdVtx10 or CmdVtxXy or CmdVtxXz or CmdVtxYz or CmdVtxDiff)
                {
                    Assemble(sink, strip, mode);
                }
            }
        }
    }
    private static void Assemble(IGxSink sink, List<int> strip, int mode)
    {
        int count = strip.Count;
        switch (mode)
        {
            case 0:
                if (count % 3 == 0 && count >= 3)
                {
                    sink.Triangle(strip[count - 3], strip[count - 2], strip[count - 1], mode);
                }
                break;
            case 1:
                if (count % 4 == 0 && count >= 4)
                {
                    sink.Triangle(strip[count - 4], strip[count - 3], strip[count - 2], mode);
                    sink.Triangle(strip[count - 4], strip[count - 2], strip[count - 1], mode);
                }
                break;
            case 2:
                if (count >= 3)
                {
                    if ((count - 1) % 2 == 0)
                    {
                        sink.Triangle(strip[count - 3], strip[count - 2], strip[count - 1], mode);
                    }
                    else
                    {
                        sink.Triangle(strip[count - 2], strip[count - 3], strip[count - 1], mode);
                    }
                }
                break;
            case 3:
                if (count >= 4 && count % 2 == 0)
                {
                    sink.Triangle(strip[count - 4], strip[count - 3], strip[count - 1], mode);
                    sink.Triangle(strip[count - 4], strip[count - 1], strip[count - 2], mode);
                }
                break;
        }
    }
    public static int Signed10(uint value)
    {
        return (value & 0x200) != 0 ? (int)value | unchecked((int)0xFFFFFC00) : (int)value;
    }
    public static byte Expand(uint value)
    {
        byte scaled = (byte)(value << 3);
        return (byte)(scaled | (scaled >> 5));
    }
}
