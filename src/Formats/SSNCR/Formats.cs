using System.Buffers.Binary;
using System.Text;
namespace SSNCR.Formats;

// ── COL (rigid-body collision mesh) ──────────────────────────────────────────
// magic 08 00 DC DC; version LE u32=1 at [4]; counts at [0x14/0x18/0x1C]

internal sealed record ColInfo(int NumMeshes, int NumVerts, int NumTris);

internal static class ColFile
{
    private static ReadOnlySpan<byte> Magic => [0x08, 0x00, 0xDC, 0xDC];

    public static ColInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20
            || !data[..4].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
            return null;
        return new ColInfo(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]));
    }
}

// ── WCOL (world terrain collision grid) ──────────────────────────────────────
// magic "TSEC"; counts at [4/8/0xC/0x10]; 2D AABB floats LE at [0x20..0x2F]

internal sealed record WcolInfo(int NumSectors, int NumVerts, int NumFaces, int NumTris,
    float MinX, float MinZ, float MaxX, float MaxZ);

internal static class WcolFile
{
    public static WcolInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x30 || !data[..4].SequenceEqual("TSEC"u8))
            return null;
        return new WcolInfo(
            NumSectors: (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            NumVerts:   (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            NumFaces:   (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            NumTris:    (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            MinX: BinaryPrimitives.ReadSingleLittleEndian(data[0x20..]),
            MinZ: BinaryPrimitives.ReadSingleLittleEndian(data[0x24..]),
            MaxX: BinaryPrimitives.ReadSingleLittleEndian(data[0x28..]),
            MaxZ: BinaryPrimitives.ReadSingleLittleEndian(data[0x2C..]));
    }
}

// ── PATH (race waypoints) ─────────────────────────────────────────────────────
// no magic; headerSize LE u32=0x10 at [8] is the discriminator

internal sealed record PathInfo(int NumPoints, int NumNodes, int DataOffset);

internal static class PathFile
{
    public static PathInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x10) return null;
        int numPoints  = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        int numNodes   = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (headerSize != 0x10 || numPoints <= 0 || numPoints > 10000) return null;
        int dataOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        return new PathInfo(numPoints, numNodes, dataOff);
    }
}

// ── TPL (Nintendo GameCube texture library) ───────────────────────────────────
// magic BE u32=0x0020AF30 at [0]; numTextures BE u32 at [4]; table at [8]=0x0C
// entry: texHdrOffset BE u32 at table+i*8; hdr: h u16, w u16, fmt u32, dataOff u32

internal sealed record TplTexture(int Index, int Width, int Height, uint GxFormat, int DataOffset, int PalHdrOffset);

internal static class TplFile
{
    private static ReadOnlySpan<byte> Magic => [0x00, 0x20, 0xAF, 0x30];

    public static bool IsTpl(ReadOnlySpan<byte> data)
        => data.Length >= 12 && data[..4].SequenceEqual(Magic);

    public static IReadOnlyList<TplTexture>? TryRead(ReadOnlySpan<byte> data)
    {
        if (!IsTpl(data)) return null;
        int numTex = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int tblOff = (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        if (numTex <= 0 || numTex > 4096 || tblOff + numTex * 8 > data.Length) return null;

        List<TplTexture> result = new(numTex);
        for (int i = 0; i < numTex; i++)
        {
            int tblEntry = tblOff + i * 8;
            int hdrOff   = (int)BinaryPrimitives.ReadUInt32BigEndian(data[tblEntry..]);
            int palHdrOff = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(tblEntry + 4)..]);
            if (hdrOff == 0 || hdrOff + 12 > data.Length) continue;
            int   h    = BinaryPrimitives.ReadUInt16BigEndian(data[hdrOff..]);
            int   w    = BinaryPrimitives.ReadUInt16BigEndian(data[(hdrOff + 2)..]);
            uint  fmt  = BinaryPrimitives.ReadUInt32BigEndian(data[(hdrOff + 4)..]);
            int   dOff = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(hdrOff + 8)..]);
            if (w > 0 && h > 0 && w <= 4096 && h <= 4096)
                result.Add(new TplTexture(i, w, h, fmt, dOff, palHdrOff));
        }
        return result;
    }

    public static string FormatName(uint fmt) => fmt switch
    {
        0  => "GX_TF_I4",
        1  => "GX_TF_I8",
        2  => "GX_TF_IA4",
        3  => "GX_TF_IA8",
        4  => "GX_TF_RGB565",
        5  => "GX_TF_RGB5A3",
        6  => "GX_TF_RGBA8",
        8  => "GX_TF_CI4",
        9  => "GX_TF_CI8",
        10 => "GX_TF_CI14x2",
        14 => "GX_TF_CMPR (DXT1)",
        _  => $"0x{fmt:X}",
    };

    // TPL also carries a palette header when texHdr's format is CI4/CI8/CI14x2
    // paletteHdrOffset: u32BE paletteEntryCount at [0], u16BE unknown at [4], u32BE palFormat at [6], u32BE dataOffset at [10]
    public static (byte[] data, uint fmt) ReadPalette(ReadOnlySpan<byte> file, int paletteHdrOffset)
    {
        if (paletteHdrOffset == 0 || paletteHdrOffset + 12 > file.Length)
            return ([], 1);
        int count  = (int)BinaryPrimitives.ReadUInt32BigEndian(file[paletteHdrOffset..]);
        uint palFmt = BinaryPrimitives.ReadUInt32BigEndian(file[(paletteHdrOffset + 6)..]);
        int  dOff  = (int)BinaryPrimitives.ReadUInt32BigEndian(file[(paletteHdrOffset + 10)..]);
        int  bytes = count * 2;
        if (dOff + bytes > file.Length) return ([], palFmt);
        return (file[dOff..(dOff + bytes)].ToArray(), palFmt);
    }
}

// ── FSB (FMOD Sound Bank v3) ──────────────────────────────────────────────────
// magic "FSB3"/"FSB4"/"FSB5"; numSamples u32LE at [4]; headerSize u32LE at [8]
// each sample entry: size u16LE, name[30], lengthSamples u32LE, ... defFreq s32LE, ... numChannels u16LE

internal sealed record FsbSample(string Name, int LengthSamples, int SampleRate, int Channels, int Mode);
internal sealed record FsbInfo(int Version, int NumSamples, IReadOnlyList<FsbSample> Samples);

internal static class FsbFile
{
    public static FsbInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x18) return null;
        if (data[0] != 'F' || data[1] != 'S' || data[2] != 'B') return null;
        int version = data[3] - '0'; // '3','4','5'
        if (version < 3 || version > 5) return null;
        int numSamples = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (numSamples <= 0 || numSamples > 10000) return null;

        // FSB3/4 have 24-byte file header; FSB5 has a different layout
        if (version >= 5) return new FsbInfo(version, numSamples, []);

        List<FsbSample> samples = [];
        int pos = 0x18; // first sample entry
        for (int i = 0; i < numSamples && pos + 32 <= data.Length; i++)
        {
            int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            if (entrySize < 64 || pos + entrySize > data.Length) break;

            ReadOnlySpan<byte> nameSpan = data.Slice(pos + 2, 30);
            int nameEnd = nameSpan.IndexOf((byte)0);
            string name = nameEnd >= 0
                ? Encoding.ASCII.GetString(nameSpan[..nameEnd])
                : Encoding.ASCII.GetString(nameSpan);

            int len      = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 32)..]);
            int mode     = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 48)..]);
            int freq     = (int)BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 52)..]);
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 58)..]);
            if (channels < 1 || channels > 8) channels = 1;
            if (freq <= 0) freq = 44100;
            samples.Add(new FsbSample(name, len, freq, channels, mode));
            pos += entrySize;
        }
        return new FsbInfo(version, numSamples, samples);
    }

    public static string ModeName(int mode)
    {
        // Key FMOD 3.x encoding flags (most significant bits 24-26)
        return (mode >> 24) switch
        {
            int m when (m & 0x01) != 0 => "GCN DSP ADPCM",
            int m when (m & 0x02) != 0 => "IMA ADPCM",
            int m when (m & 0x04) != 0 => "PSP VAG ADPCM",
            int m when (m & 0x40) != 0 => "MPEG",
            int m when (m & 0x08) != 0 => "GCADPCM",
            _ => (mode & 0x40) != 0 ? "16-bit PCM" : "PCM/unknown",
        };
    }
}

// ── THP (Nintendo GameCube video) ─────────────────────────────────────────────
// magic "THP\0"; version BE u32=0x00011000; fps BE f32 at [0x10]; numFrames BE u32 at [0x14]
// numComponents BE u32 at [0x30]; component types at [0x34]; video dims at [0x40/0x44]
// audio: channels BE u32 at [0x50]; sampleRate BE u32 at [0x54]

internal sealed record ThpInfo(float Fps, int NumFrames, int Width, int Height,
    int AudioChannels, int AudioRate);

internal static class ThpFile
{
    public static ThpInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x60 || !data[..4].SequenceEqual("THP\0"u8)) return null;
        float fps       = BinaryPrimitives.ReadSingleBigEndian(data[0x10..]);
        int   frames    = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x14..]);
        int   numComp   = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x30..]);
        int   w = 0, h = 0, aCh = 0, aRate = 0;
        // Component info starts at 0x34 (up to 8 u8 component type IDs, 0xFF=end)
        for (int i = 0; i < 8 && i < numComp; i++)
        {
            byte ctype = data[0x34 + i];
            if (ctype == 0 && data.Length >= 0x50)
            {
                w = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x40..]);
                h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x44..]);
            }
            else if (ctype == 1 && data.Length >= 0x60)
            {
                aCh   = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x50..]);
                aRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x54..]);
            }
        }
        if (fps <= 0 || frames <= 0) return null;
        return new ThpInfo(fps, frames, w, h, aCh, aRate);
    }
}

// ── DAT (UI string table, UTF-16LE) ──────────────────────────────────────────
// 16-byte header; numStrings LE u16 at [0x0A]; string data offset LE u16 at [0x0E] (corrected)
// actual data starts 8 bytes after stated offset; strings are UTF-16LE null-terminated
// some strings preceded by 0xC5C5 language-fallback marker

internal sealed record DatInfo(int NumStrings, IReadOnlyList<string> FirstStrings);

internal static class DatFile
{
    public static DatInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20) return null;
        // Detect: strings are UTF-16LE, scan for the first valid string pattern
        // The table of numStrings × 4-byte entries occupies from 0x10 to stringDataStart
        int numStrings = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0A..]);
        if (numStrings <= 0 || numStrings > 10000) return null;
        int stringDataStart = 0x10 + numStrings * 4;
        if (stringDataStart + 2 > data.Length) return null;

        // Extract first few UTF-16LE strings from the data section
        List<string> first = [];
        int pos = stringDataStart;
        while (pos + 1 < data.Length && first.Count < 20)
        {
            // Skip 0xC5C5 language markers
            if (pos + 1 < data.Length && data[pos] == 0xC5 && data[pos+1] == 0xC5) { pos += 2; continue; }
            // Find null terminator (0x00 0x00 aligned)
            int start = pos;
            while (pos + 1 < data.Length && !(data[pos] == 0 && data[pos+1] == 0)) pos += 2;
            int len = pos - start;
            if (len >= 2)
            {
                string s = Encoding.Unicode.GetString(data[start..(start + len)]).Trim();
                if (s.Length > 0) first.Add(s);
            }
            pos += 2; // skip null terminator
        }
        return new DatInfo(numStrings, first);
    }
}

// ── RIFF-WAVE (WAV / ATRAC3) ──────────────────────────────────────────────────
// RIFF header: "RIFF" + size + "WAVE"; "fmt " chunk with codec, channels, rate
// ATRAC3 codec: 0x0270 (AT3), ATRAC3+ codec: 0x0271

internal sealed record RiffInfo(ushort Codec, int Channels, int SampleRate, long NumSamples);

internal static class RiffFile
{
    public static RiffInfo? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            return null;
        int pos = 12;
        ushort codec = 0; int ch = 0, rate = 0; long samples = 0;
        while (pos + 8 <= data.Length)
        {
            ReadOnlySpan<byte> tag = data[pos..(pos+4)];
            int chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos+4)..]);
            if (tag.SequenceEqual("fmt "u8) && chunkSize >= 14)
            {
                codec = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos+8)..]);
                ch    = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos+10)..]);
                rate  = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos+12)..]);
            }
            else if (tag.SequenceEqual("data"u8))
            {
                // For PCM: numSamples = chunkSize / (ch * 2); for compressed, approximate
                samples = rate > 0 && ch > 0 ? chunkSize / (long)(ch * 2) : chunkSize;
            }
            else if (tag.SequenceEqual("smpl"u8) && chunkSize >= 36)
            {
                // smpl chunk has samplePeriod, can refine
            }
            pos += 8 + ((chunkSize + 1) & ~1); // chunks are word-aligned
        }
        return codec == 0 ? null : new RiffInfo(codec, ch, rate, samples);
    }

    public static string CodecName(ushort codec) => codec switch
    {
        0x0001 => "PCM",
        0x0002 => "ADPCM",
        0x0270 => "ATRAC3",
        0x0271 => "ATRAC3+",
        0x0160 => "WMA",
        0x0161 => "WMA Pro",
        _      => $"0x{codec:X4}",
    };
}
