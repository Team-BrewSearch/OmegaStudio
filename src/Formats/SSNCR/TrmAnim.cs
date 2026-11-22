using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Omega.Plugins;
namespace SSNCR.Formats;

internal sealed record TrmTrack(uint BoneHash, int Type, float[] Times, Quaternion[]? Rotations, Vector3[]? Translations, float[]? Scales);
internal sealed record TrmEvent(float Time, uint Id);
internal sealed record TrmAnimation(uint Hash, float Duration, int Flags, IReadOnlyList<TrmTrack> Tracks, IReadOnlyList<TrmEvent> Events);

internal static class TrmAnimReader
{
    public static int KeySize(int type)
    {
        if (type is < 1 or > 7) throw new InvalidDataException($"animation track type {type}");
        return ((type & 4) != 0 ? 8 : 0) + ((type & 2) != 0 ? 12 : 0) + ((type & 1) != 0 ? 4 : 0);
    }

    public static TrmTrack Track(uint hash, int type, int count, Func<int, (float Time, int Offset)> key, ReadOnlySpan<byte> tail)
    {
        int size = KeySize(type);
        var times = new float[count];
        Quaternion[]? rot = (type & 4) != 0 ? new Quaternion[count] : null;
        Vector3[]? pos = (type & 2) != 0 ? new Vector3[count] : null;
        float[]? scl = (type & 1) != 0 ? new float[count] : null;
        for (int k = 0; k < count; k++)
        {
            (float t, int off) = key(k);
            if (off < 0 || off + size > tail.Length) throw new InvalidDataException("animation key outside its key data");
            if (k > 0 && !(t > times[k - 1])) throw new InvalidDataException("animation key times do not increase");
            times[k] = t;
            ReadOnlySpan<byte> s = tail.Slice(off, size);
            int p = 0;
            if (rot is not null)
            {
                rot[k] = Quaternion.Normalize(new Quaternion(
                    BinaryPrimitives.ReadInt16LittleEndian(s) / 16384f, BinaryPrimitives.ReadInt16LittleEndian(s[2..]) / 16384f,
                    BinaryPrimitives.ReadInt16LittleEndian(s[4..]) / 16384f, BinaryPrimitives.ReadInt16LittleEndian(s[6..]) / 16384f));
                p = 8;
            }
            if (pos is not null)
            {
                pos[k] = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s[p..]), BinaryPrimitives.ReadSingleLittleEndian(s[(p + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(s[(p + 8)..]));
                p += 12;
            }
            if (scl is not null) scl[k] = BinaryPrimitives.ReadSingleLittleEndian(s[p..]);
        }
        return new TrmTrack(hash, type, times, rot, pos, scl);
    }

    public static List<TrmAnimation> ReadIndexed(ReadOnlySpan<byte> blob, bool be, int anims, int tracks, int keys, int events)
    {
        uint U32(ReadOnlySpan<byte> b, int o) => be ? BinaryPrimitives.ReadUInt32BigEndian(b[o..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
        ushort U16(ReadOnlySpan<byte> b, int o) => be ? BinaryPrimitives.ReadUInt16BigEndian(b[o..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
        int trackAt = 32 * anims, keyAt = trackAt + 16 * tracks, eventAt = keyAt + 8 * keys, tailAt = eventAt + 8 * events;
        if (tailAt > blob.Length) throw new InvalidDataException("animation tables overrun their blob");
        byte[] buf = blob.ToArray();
        List<TrmAnimation> list = [];
        for (int a = 0; a < anims; a++)
        {
            int r = 32 * a;
            uint hash = U32(blob, r);
            int nTracks = (int)U32(blob, r + 4);
            float dur = BitConverter.UInt32BitsToSingle(U32(blob, r + 8));
            int flags = U16(blob, r + 0x10), nEvents = U16(blob, r + 0x12);
            int t0 = (int)U32(blob, r + 0x14), e0 = (int)U32(blob, r + 0x18);
            if (t0 < 0 || t0 + nTracks > tracks || (nEvents > 0 && (e0 < 0 || e0 + nEvents > events)))
                throw new InvalidDataException($"animation {a} indexes outside its tables");
            List<TrmTrack> ts = [];
            for (int i = 0; i < nTracks; i++)
            {
                int q = trackAt + 16 * (t0 + i);
                int nk = (int)U32(blob, q + 4), type = (int)U32(blob, q + 8), k0 = (int)U32(blob, q + 12);
                if (k0 < 0 || nk < 0 || k0 + nk > keys) throw new InvalidDataException("animation track indexes outside the keys");
                ts.Add(Track(U32(blob, q), type, nk,
                    k => (BitConverter.UInt32BitsToSingle(U32(buf, keyAt + 8 * (k0 + k))), (int)U32(buf, keyAt + 8 * (k0 + k) + 4)),
                    blob[tailAt..]));
            }
            var ev = new List<TrmEvent>();
            for (int i = 0; i < nEvents; i++)
                ev.Add(new TrmEvent(BitConverter.UInt32BitsToSingle(U32(blob, eventAt + 8 * (e0 + i))), U32(blob, eventAt + 8 * (e0 + i) + 4)));
            list.Add(new TrmAnimation(hash, dur, flags, ts, ev));
        }
        return list;
    }

    public static List<TrmAnimation> ReadPsp(PspChunks c, byte[] m, int at, int anims)
    {
        uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        int Ptr(uint v) { (byte[] b, int off) = c.Resolve(v); if (b != m) throw new InvalidDataException("PSP animation pointer leaves the model block"); return off; }
        List<TrmAnimation> list = [];
        for (int a = 0; a < anims; a++)
        {
            int r = at + 32 * a;
            int nTracks = (int)U32(m, r + 4);
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(r + 0x10)), nEvents = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(r + 0x12));
            int trackAt = nTracks > 0 ? Ptr(U32(m, r + 0x14)) : 0;
            int eventAt = nEvents > 0 ? Ptr(U32(m, r + 0x18)) : 0;
            int tailAt = Ptr(U32(m, r + 0x1C));
            List<TrmTrack> ts = [];
            for (int i = 0; i < nTracks; i++)
            {
                int q = trackAt + 16 * i;
                int nk = (int)U32(m, q + 4), keyAt = nk > 0 ? Ptr(U32(m, q + 12)) : 0;
                ts.Add(Track(U32(m, q), (int)U32(m, q + 8), nk,
                    k => (BitConverter.UInt32BitsToSingle(U32(m, keyAt + 8 * k)), (int)U32(m, keyAt + 8 * k + 4)),
                    m.AsSpan(tailAt)));
            }
            var ev = new List<TrmEvent>();
            for (int i = 0; i < nEvents; i++) ev.Add(new TrmEvent(BitConverter.UInt32BitsToSingle(U32(m, eventAt + 8 * i)), U32(m, eventAt + 8 * i + 4)));
            list.Add(new TrmAnimation(U32(m, r), BitConverter.UInt32BitsToSingle(U32(m, r + 8)), flags, ts, ev));
        }
        return list;
    }
}

internal static class TrmPoser
{
    public const float Fps = 60f;

    public static Matrix4x4 Local(TrmTrack t, Matrix4x4 bind, float seconds)
    {
        if (t.Times.Length == 0) return bind;
        (Vector3 s, Quaternion r, Vector3 p) = Parts(t, bind, seconds);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(p);
    }

    // parts a track leaves out come from the bind local
    public static (Vector3 Scale, Quaternion Rotation, Vector3 Translation) Parts(TrmTrack t, Matrix4x4 bind, float seconds)
    {
        Matrix4x4.Decompose(bind, out Vector3 s, out Quaternion r, out Vector3 p);
        int n = t.Times.Length;
        int k1 = Array.FindIndex(t.Times, x => x > seconds);
        int k0 = k1 < 0 ? n - 1 : Math.Max(k1 - 1, 0);
        if (k1 < 0) k1 = n - 1;
        float span = t.Times[k1] - t.Times[k0];
        float f = span > 0 ? Math.Clamp((seconds - t.Times[k0]) / span, 0f, 1f) : 0f;
        if (t.Rotations is { } q) r = Quaternion.Normalize(Quaternion.Slerp(q[k0], q[k1], f));
        if (t.Translations is { } v) p = Vector3.Lerp(v[k0], v[k1], f);
        if (t.Scales is { } sc) s = new Vector3(sc[k0] + (sc[k1] - sc[k0]) * f);
        return (s, r, p);
    }

    public static Matrix4x4[] World(TrmModel model, TrmAnimation? anim, float seconds)
    {
        var byHash = new Dictionary<uint, TrmTrack>();
        if (anim is not null) foreach (TrmTrack t in anim.Tracks) byHash.TryAdd(t.BoneHash, t);
        var world = new Matrix4x4[model.Frames.Count];
        for (int i = 0; i < world.Length; i++)
        {
            TrmFrame f = model.Frames[i];
            Matrix4x4 local = f.NameHash != 0 && byHash.TryGetValue(f.NameHash, out TrmTrack? t) ? Local(t, f.Local, seconds) : f.Local;
            world[i] = f.Parent < 0 ? local : local * world[f.Parent];
        }
        return world;
    }

    public static AnimationClip Clip(TrmModel model, TrmAnimation anim, Func<int, string> jointName)
    {
        Dictionary<uint, int> frameOf = [];
        for (int i = 0; i < model.Frames.Count; i++) if (model.Frames[i].NameHash != 0) frameOf.TryAdd(model.Frames[i].NameHash, i);
        List<AnimationTrack> tracks = [];
        foreach (TrmTrack t in anim.Tracks)
        {
            if (!frameOf.TryGetValue(t.BoneHash, out int frame)) continue;
            tracks.Add(new AnimationTrack
            {
                Name = jointName(frame),
                Times = [.. t.Times.Select(x => x * Fps)],
                Matrices = [.. t.Times.Select(x => { var m = new float[16]; MatrixOps.CopyTo(Local(t, model.Frames[frame].Local, x), m); return m; })],
                Translations = [.. t.Times.SelectMany(x => { var v = Parts(t, model.Frames[frame].Local, x).Translation; return new[] { v.X, v.Y, v.Z }; })],
                Rotations = [.. t.Times.SelectMany(x => { var q = Parts(t, model.Frames[frame].Local, x).Rotation; return new[] { q.X, q.Y, q.Z, q.W }; })],
                Scales = [.. t.Times.SelectMany(x => { var v = Parts(t, model.Frames[frame].Local, x).Scale; return new[] { v.X, v.Y, v.Z }; })],
            });
        }
        return new AnimationClip
        {
            Name = $"anim_{anim.Hash:X8}",
            Frames = Math.Max(1, (int)MathF.Ceiling(anim.Duration * Fps)),
            Fps = Fps,
            Tracks = tracks,
            Extras = new Dictionary<string, JsonNode>
            {
                ["hash"] = anim.Hash,
                ["duration"] = anim.Duration,
                ["flags"] = anim.Flags,
                ["events"] = new JsonArray([.. anim.Events.Select(e => (JsonNode)new JsonObject { ["time"] = e.Time, ["id"] = e.Id })]),
            },
        };
    }
}
