using System.Buffers.Binary;
using System.Text;
using Omega.Plugins;
using OmegaStudio.Audio;
namespace OmegaStudio.Services;
internal static class MatroskaWriter
{
    private const uint SegmentId = 0x18538067;
    private const uint SeekHeadId = 0x114D9B74;
    private const uint InfoId = 0x1549A966;
    private const uint TracksId = 0x1654AE6B;
    private const uint ClusterId = 0x1F43B675;
    private const uint CuesId = 0x1C53BB6B;
    private const int ClusterMilliseconds = 1000;
    public static void Write(string path, IVideoReader reader, byte[] soundtrack, Func<ImageData, byte[]> jpeg, Action<string> log)
    {
        VideoInfo info = reader.Info;
        IAudioDecoder? audio = soundtrack.Length > 0 ? AudioInfoHelper.CreateDecoder(soundtrack) : null;
        byte[] pcm = [];
        if (audio is not null)
        {
            pcm = new byte[audio.TotalBytes];
            audio.ReadAll(pcm);
        }
        int frameBytes = audio is null ? 0 : audio.Channels * (audio.BitsPerSample / 8);
        long totalMs = (long)Math.Round(info.Duration.TotalMilliseconds);
        string temporary = path + ".partial";
        using (FileStream file = new(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            file.Write(Master(0x1A45DFA3,
                UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
                Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2)));
            Id(file, SegmentId);
            long segmentSize = file.Position;
            file.Write([0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            long segmentStart = file.Position;
            uint[] seekTargets = [InfoId, TracksId, CuesId];
            long seekHead = file.Position;
            file.Write(SeekHead(seekTargets, new long[seekTargets.Length]));
            long infoAt = file.Position - segmentStart;
            file.Write(Master(InfoId,
                UInt(0x2AD7B1, 1000000),
                Text(0x4D80, "Omega Studio"),
                Text(0x5741, "Omega Studio"),
                Float(0x4489, totalMs)));
            long tracksAt = file.Position - segmentStart;
            List<byte[]> tracks =
            [
                Master(0xAE,
                    UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 1), UInt(0x9C, 0),
                    Text(0x86, "V_MJPEG"),
                    UInt(0x23E383, (ulong)Math.Round(1e9 * info.RateDenominator / info.RateNumerator)),
                    Master(0xE0, UInt(0xB0, (ulong)info.Width), UInt(0xBA, (ulong)info.Height))),
            ];
            if (audio is not null)
            {
                tracks.Add(Master(0xAE,
                    UInt(0xD7, 2), UInt(0x73C5, 2), UInt(0x83, 2), UInt(0x9C, 0),
                    Text(0x86, "A_PCM/INT/LIT"),
                    Master(0xE1, Float(0xB5, audio.SampleRate), UInt(0x9F, (ulong)audio.Channels), UInt(0x6264, (ulong)audio.BitsPerSample))));
            }
            file.Write(Master(TracksId, [.. tracks]));
            List<(long Time, long Position)> cues = [];
            MemoryStream cluster = new();
            long clusterTime = -1;
            int batch = Math.Max(1, Environment.ProcessorCount);
            int written = 0;
            int lastPercent = -1;
            reader.Seek(0);
            while (written < info.FrameCount)
            {
                List<byte[]> pixels = [];
                for (int k = 0; k < batch && written + k < info.FrameCount; k++)
                {
                    byte[] rgba = new byte[info.FrameBytes];
                    if (!reader.ReadFrame(rgba))
                    {
                        break;
                    }
                    pixels.Add(rgba);
                }
                if (pixels.Count == 0)
                {
                    break;
                }
                byte[][] encoded = new byte[pixels.Count][];
                int first = written;
                Parallel.For(0, pixels.Count, k => encoded[k] = jpeg(ImageData.Create($"frame{first + k}", info.Width, info.Height, pixels[k])));
                for (int k = 0; k < encoded.Length; k++, written++)
                {
                    long time = (long)Math.Round(info.TimeOf(written).TotalMilliseconds);
                    if (clusterTime < 0 || time - clusterTime >= ClusterMilliseconds)
                    {
                        if (clusterTime >= 0)
                        {
                            file.Write(Element(ClusterId, cluster.ToArray()));
                        }
                        cues.Add((time, file.Position - segmentStart));
                        cluster = new MemoryStream();
                        cluster.Write(UInt(0xE7, (ulong)time));
                        clusterTime = time;
                    }
                    cluster.Write(Block(1, (short)(time - clusterTime), encoded[k]));
                    if (audio is not null)
                    {
                        long from = written * (long)audio.SampleRate * info.RateDenominator / info.RateNumerator;
                        long to = written == info.FrameCount - 1 ? pcm.Length / frameBytes : (written + 1) * (long)audio.SampleRate * info.RateDenominator / info.RateNumerator;
                        from = Math.Min(from, pcm.Length / frameBytes);
                        to = Math.Min(to, pcm.Length / frameBytes);
                        if (to > from)
                        {
                            long audioTime = (long)Math.Round(from * 1000.0 / audio.SampleRate);
                            cluster.Write(Block(2, (short)(audioTime - clusterTime), pcm.AsSpan((int)(from * frameBytes), (int)((to - from) * frameBytes))));
                        }
                    }
                }
                int percent = (int)(written * 100L / Math.Max(1, info.FrameCount));
                if (percent / 10 != lastPercent / 10)
                {
                    lastPercent = percent;
                    log($"Exporting video: {written:N0} of {info.FrameCount:N0} frames ({percent}%).");
                }
            }
            if (clusterTime >= 0)
            {
                file.Write(Element(ClusterId, cluster.ToArray()));
            }
            long cuesAt = file.Position - segmentStart;
            file.Write(Master(CuesId, [.. cues.Select(c => Master(0xBB,
                UInt(0xB3, (ulong)c.Time),
                Master(0xB7, UInt(0xF7, 1), UInt(0xF1, (ulong)c.Position))))]));
            long end = file.Position;
            file.Position = seekHead;
            file.Write(SeekHead(seekTargets, [infoAt, tracksAt, cuesAt]));
            file.Position = segmentSize;
            byte[] size = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)(end - segmentStart) | (1UL << 56));
            file.Write(size);
        }
        File.Move(temporary, path, true);
    }
    private static byte[] SeekHead(uint[] targets, long[] positions)
    {
        return Master(SeekHeadId, [.. targets.Select((id, i) => Master(0x4DBB, Element(0x53AB, IdBytes(id)), Fixed(0x53AC, (ulong)positions[i])))]);
    }
    private static byte[] Block(int track, short time, ReadOnlySpan<byte> data)
    {
        byte[] body = new byte[4 + data.Length];
        body[0] = (byte)(0x80 | track);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(1), time);
        body[3] = 0x80;
        data.CopyTo(body.AsSpan(4));
        return Element(0xA3, body);
    }
    private static byte[] IdBytes(uint id)
    {
        int length = id > 0xFFFFFF ? 4 : id > 0xFFFF ? 3 : id > 0xFF ? 2 : 1;
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(id >> (8 * (length - 1 - i)));
        }
        return bytes;
    }
    private static void Id(Stream stream, uint id)
    {
        stream.Write(IdBytes(id));
    }
    private static byte[] SizeBytes(long size)
    {
        int length = 1;
        while (length < 8 && size >= (1L << (7 * length)) - 1)
        {
            length++;
        }
        byte[] bytes = new byte[length];
        ulong value = (ulong)size | (1UL << (7 * length));
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(value >> (8 * (length - 1 - i)));
        }
        return bytes;
    }
    private static byte[] Element(uint id, ReadOnlySpan<byte> body)
    {
        byte[] head = [.. IdBytes(id), .. SizeBytes(body.Length)];
        byte[] all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all.AsSpan(head.Length));
        return all;
    }
    private static byte[] Master(uint id, params byte[][] children)
    {
        return Element(id, children.SelectMany(c => c).ToArray());
    }
    private static byte[] UInt(uint id, ulong value)
    {
        int length = 1;
        while (length < 8 && value >> (8 * length) != 0)
        {
            length++;
        }
        byte[] body = new byte[length];
        for (int i = 0; i < length; i++)
        {
            body[i] = (byte)(value >> (8 * (length - 1 - i)));
        }
        return Element(id, body);
    }
    private static byte[] Fixed(uint id, ulong value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(body, value);
        return Element(id, body);
    }
    private static byte[] Float(uint id, double value)
    {
        byte[] body = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(body, value);
        return Element(id, body);
    }
    private static byte[] Text(uint id, string value)
    {
        return Element(id, Encoding.ASCII.GetBytes(value));
    }
}
