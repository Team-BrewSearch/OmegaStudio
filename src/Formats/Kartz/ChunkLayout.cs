using Kartz.Formats.Binary;
namespace Kartz.Formats;

public sealed class ChunkLayout
{
    public static ChunkLayout Narrow { get; } = new()
    {
        HeaderSize = 24,
        TextureRefSize = 12,
        TextureCountField = 0x10,
        PrefixWordField = 0x14,
    };
    public static ChunkLayout Wide { get; } = new()
    {
        HeaderSize = 108,
        TextureRefSize = 16,
        TextureCountField = 0x64,
    };
    private static readonly ChunkLayout[] All = [Narrow, Wide];
    private ChunkLayout()
    {
    }
    public required int HeaderSize { get; init; }
    public required int TextureRefSize { get; init; }
    public required int TextureCountField { get; init; }
    public int PrefixWordField { get; init; } = -1;
    public int ListStart(ReadOnlySpan<byte> blob, int at)
    {
        return at + HeaderSize
            + ((int)BinaryRead.U32(blob, at + TextureCountField) * TextureRefSize)
            + PrefixBytes(blob, at);
    }
    public int PrefixBytes(ReadOnlySpan<byte> blob, int at)
    {
        if (PrefixWordField < 0 || at + PrefixWordField + 4 > blob.Length)
        {
            return 0;
        }
        long words = BinaryRead.U32(blob, at + PrefixWordField);
        return words is > 0 and <= 64 ? (int)words * 4 : 0;
    }
    public long End(ReadOnlySpan<byte> blob, long at)
    {
        if (at < 0 || at + HeaderSize > blob.Length)
        {
            return -1;
        }
        long size = BinaryRead.U32(blob, (int)at + 4);
        long textures = BinaryRead.U32(blob, (int)at + TextureCountField);
        if (size % 4 != 0 || textures > 4096)
        {
            return -1;
        }
        long end = at + HeaderSize + (textures * TextureRefSize) + size;
        return end <= blob.Length ? end : -1;
    }
    public static ChunkLayout? Of(ReadOnlySpan<byte> blob, long at, long wanted = -1)
    {
        ChunkLayout? loose = null;
        foreach (ChunkLayout layout in All)
        {
            long end = layout.End(blob, at);
            if (end < 0)
            {
                continue;
            }
            if (wanted >= 0 && end == wanted)
            {
                return layout;
            }
            loose ??= layout;
        }
        return wanted >= 0 ? null : loose;
    }
}
