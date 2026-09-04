using Kartz.Formats.Binary;
using System.Buffers.Binary;
namespace Kartz.Formats;

public sealed class AniTrack
{
    public const int FrameSize = 64;
    public required int Bone { get; init; }
    public required int Offset { get; init; }
    public required int[] Frames { get; init; }
    public float Value(int frame, int element)
    {
        return Frames[(frame * 16) + element] / 4096f;
    }
    public float Translation(int frame, int axis)
    {
        return Frames[(frame * 16) + 12 + axis] / 4096f;
    }
}
public sealed class AniModel
{
    public const int NarrowHeaderSize = 8;
    public const int WideHeaderSize = 20;
    public const int WideTailStride = 12;
    public const float Fps = 8f;
    private AniModel(int bones, int frames, IReadOnlyList<AniTrack> tracks, int header)
    {
        BoneCount = bones;
        FrameCount = frames;
        Tracks = tracks;
        Header = header;
    }
    public int BoneCount { get; }
    public int FrameCount { get; }
    public IReadOnlyList<AniTrack> Tracks { get; }
    public int Header { get; }
    public static bool IsAnimation(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _);
    }
    public static AniModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out AniModel? model) && model is not null
            ? model
            : throw new InvalidDataException("not a Kartz .ani animation");
    }
    public byte[] Serialize()
    {
        return Write(Header, BoneCount, FrameCount, Tracks);
    }
    public static byte[] Write(int header, int bones, int frames, IReadOnlyList<AniTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int table = header + (bones * 4);
        int block = frames * AniTrack.FrameSize;
        int tail = header == WideHeaderSize ? (frames - 1) * WideTailStride : 0;
        int written = 0;
        foreach (AniTrack track in tracks)
        {
            if (track.Bone >= 0 && track.Bone < bones)
            {
                written++;
            }
        }
        byte[] output = new byte[table + (written * block) + tail];
        if (header == WideHeaderSize)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)bones);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)frames);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), WideHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)(table + (written * block)));
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)bones);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)frames);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), NarrowHeaderSize);
        }
        int at = table;
        foreach (AniTrack track in tracks)
        {
            if (track.Bone < 0 || track.Bone >= bones)
            {
                continue;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header + (track.Bone * 4)), (uint)at);
            for (int k = 0; k < track.Frames.Length; k++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + (k * 4)), track.Frames[k]);
            }
            at += block;
        }
        return output;
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, out AniModel? model)
    {
        return TryRead(blob, NarrowHeaderSize, out model) || TryRead(blob, WideHeaderSize, out model);
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, int header, out AniModel? model)
    {
        model = null;
        if (blob.Length < header + 4)
        {
            return false;
        }
        int bones;
        int frames;
        if (header == WideHeaderSize)
        {
            if (BinaryRead.U32(blob, 0) > 1024 || BinaryRead.U32(blob, 4) > 4096
                || BinaryRead.U32(blob, 8) != WideHeaderSize || BinaryRead.U32(blob, 16) != 0)
            {
                return false;
            }
            bones = (int)BinaryRead.U32(blob, 0);
            frames = (int)BinaryRead.U32(blob, 4);
        }
        else
        {
            bones = BinaryRead.U16(blob, 0);
            frames = BinaryRead.U16(blob, 2);
            if (BinaryRead.U32(blob, 4) != NarrowHeaderSize)
            {
                return false;
            }
        }
        if (bones == 0 || bones > 1024 || frames == 0 || frames > 4096)
        {
            return false;
        }
        long table = header + ((long)bones * 4);
        long block = (long)frames * AniTrack.FrameSize;
        long tail = header == WideHeaderSize ? (long)(frames - 1) * WideTailStride : 0;
        if (table > blob.Length)
        {
            return false;
        }
        List<AniTrack> tracks = [];
        long end = table;
        for (int bone = 0; bone < bones; bone++)
        {
            long at = BinaryRead.U32(blob, header + (bone * 4));
            if (at == 0)
            {
                continue;
            }
            if (at < table || at + block > blob.Length || (at - table) % block != 0)
            {
                return false;
            }
            int[] values = new int[frames * 16];
            for (int k = 0; k < values.Length; k++)
            {
                values[k] = (int)BinaryRead.U32(blob, (int)at + (k * 4));
            }
            for (int frame = 0; frame < frames; frame++)
            {
                int row = frame * 16;
                if (values[row + 3] != 0 || values[row + 7] != 0
                    || values[row + 11] != 0 || values[row + 15] != 4096)
                {
                    return false;
                }
            }
            tracks.Add(new AniTrack { Bone = bone, Offset = (int)at, Frames = values });
            end = Math.Max(end, at + block);
        }
        if (tracks.Count == 0 || end + tail != blob.Length)
        {
            return false;
        }
        if (header == WideHeaderSize && BinaryRead.U32(blob, 12) != end)
        {
            return false;
        }
        for (long at = end; at + 4 <= blob.Length; at += 4)
        {
            if (BinaryRead.U32(blob, (int)at) != 0)
            {
                return false;
            }
        }
        model = new AniModel(bones, frames, tracks, header);
        return true;
    }
}
