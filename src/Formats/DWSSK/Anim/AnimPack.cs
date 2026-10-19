using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
namespace DWSSK.Formats.Anim;
internal static partial class AnimPack
{
    public static readonly string[] Channels = ["rotation", "translation", "scale"];
    public static readonly string[] ExtraNames =
    [
        "idleBreathe", "idle_sitting", "squished_front", "squished_side", "LostControl",
        "LostControl_w_item", "LostControlIdle", "lookAround", "idleWFlag", "wavingTwoHands",
        "item_switch_up_balloon", "item_takeout_balloon", "item_holdingidle_balloon",
    ];
    public const double Fps = 30;
    private static readonly float s_qmax = GameSqrt(0.5f);
    private static readonly float s_qstep = (s_qmax + s_qmax) / 1023f;
    [GeneratedRegex(@"\[\s+([^\[\]{}]+?)\s+\]")]
    private static partial Regex FlatList();
    [GeneratedRegex("\\{\\s+(\"frame\"[^{}]+?)\\s+\\}")]
    private static partial Regex KeyObject();
    public static bool IsAnimPack(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12)
        {
            return false;
        }
        bool big = b[0] == 0;
        return Bin.U32(b, 0, big) == 12;
    }
    private static double Short(double x)
    {
        return Py.ParseFloat(Py.FormatG(x, 9));
    }
    private static double SmRead(ReadOnlySpan<byte> b, int o, int width, float rng)
    {
        if (width == 0)
        {
            throw new InvalidDataException("negative shift count");
        }
        Bin.Need(b, o, width);
        long v = 0;
        for (int k = 0; k < width; k++)
        {
            v |= (long)b[o + k] << (8 * k);
        }
        long top = (1L << ((8 * width) - 1)) - 1;
        float x = SmValue(v & top, top, rng);
        return Short(v > top ? -x : x);
    }
    private static float SmValue(long n, long top, float rng)
    {
        return rng * (float)((double)(float)n / (float)top);
    }
    private static byte[] SmWrite(double x, float rng, int width)
    {
        long top = (1L << ((8 * width) - 1)) - 1;
        if (rng == 0)
        {
            throw new DivideByZeroException("float division by zero");
        }
        double r = Py.Round(Math.Abs(x) / rng * top);
        long n = Math.Min(top, (long)r);
        for (long m = Math.Max(0, n - 2); m <= Math.Min(top, n + 2); m++)
        {
            if (SmValue(m, top, rng) == (float)Math.Abs(x))
            {
                n = m;
                break;
            }
        }
        if (double.IsNegative(x))
        {
            n |= top + 1;
        }
        byte[] o = new byte[width];
        for (int k = 0; k < width; k++)
        {
            o[k] = (byte)(n >> (8 * k));
        }
        return o;
    }
    public static double[] QuatDecode(uint v)
    {
        float[] c = new float[3];
        float w = 1f;
        for (int j = 0; j < 3; j++)
        {
            c[j] = (float)(((((v >> (20 - (10 * j))) & 1023) - 0.5f) * (double)s_qstep) - s_qmax);
            w = (float)(w - ((double)c[j] * c[j]));
        }
        List<float> q = [.. c];
        q.Insert((int)(v >> 30), w > 0 ? GameSqrt(w) : 0f);
        float len = q[0] * q[0];
        len = (float)(((double)q[3] * q[3]) + len);
        len = (float)(((double)q[1] * q[1]) + len);
        len = (float)(((double)q[2] * q[2]) + len);
        float inv = 1f / (len == 0 ? 0f : GameSqrt(len));
        return [.. q.Select(x => (double)(x * inv))];
    }
    private static float GameSqrt(float x)
    {
        double y = 1.0 / Math.Sqrt(x);
        float t = (float)(3.0 - ((double)x * (float)(y * y)));
        return x * ((float)(0.5 * y) * t);
    }
    public static uint QuatEncode(IReadOnlyList<double> q0)
    {
        double sum = 0;
        foreach (double x in q0)
        {
            sum += x * x;
        }
        double norm = Math.Sqrt(sum);
        if (norm == 0)
        {
            norm = 1.0;
        }
        double[] q = Math.Abs(norm - 1) > 1e-6 ? [.. q0.Select(x => x / norm)] : [.. q0];
        int[] order = [.. Enumerable.Range(0, 4).Select(k => (k, -Math.Abs(q[k]))).OrderBy(t => t.Item2).ThenBy(t => t.k).Select(t => t.k)];
        double bestErr = 0;
        uint best = 0;
        bool have = false;
        foreach (int i in order)
        {
            double[] p = q[i] >= 0 ? q : [.. q.Select(x => -x)];
            List<long> bits = [];
            for (int k = 0; k < 4; k++)
            {
                if (k != i)
                {
                    bits.Add(Math.Min(1023, Math.Max(0, (long)Py.Round(p[k] / s_qstep) + 512)));
                }
            }
            uint v = (uint)(((long)i << 30) | (bits[0] << 20) | (bits[1] << 10) | bits[2]);
            double[] dec = QuatDecode(v);
            double err = Math.Abs(dec[0] - p[0]);
            for (int k = 1; k < 4; k++)
            {
                double e = Math.Abs(dec[k] - p[k]);
                if (e > err)
                {
                    err = e;
                }
            }
            if (!have || err < bestErr)
            {
                bestErr = err;
                best = v;
                have = true;
            }
        }
        return best;
    }
    public static JObj Read(ReadOnlySpan<byte> b)
    {
        bool big = Bin.U8(b, 0) == 0;
        uint first = Bin.U32(b, 0, big);
        double unk = Bin.F32(b, 4, big);
        uint left = Bin.U32(b, 8, big);
        List<object?> anims = [];
        JObj doc = new()
        {
            ["endian"] = big ? "big" : "little",
            ["header"] = new JObj { ["unknown_04"] = unk, ["leftover_08"] = (long)left },
            ["animations"] = anims,
        };
        long o = first;
        long limit = b.Length;
        while (o < limit)
        {
            int eo = (int)o;
            ReadOnlySpan<byte> e = Bin.Slice(b, eo, eo + 0x80);
            Bin.Need(e, 0, 0x54);
            uint h = Bin.U32(e, 0, big);
            uint tuning = Bin.U32(e, 4, big);
            uint x08 = Bin.U32(e, 8, big);
            uint tt = Bin.U32(e, 0x14, big);
            uint end = Bin.U32(e, 0x2C, big);
            double f30 = Bin.F32(e, 0x30, big);
            double dur = Bin.F32(e, 0x34, big);
            double f40 = Bin.F32(e, 0x40, big);
            double f44 = Bin.F32(e, 0x44, big);
            double f48 = Bin.F32(e, 0x48, big);
            double f4c = Bin.F32(e, 0x4C, big);
            int tw = e[0x50];
            int sw = e[0x51];
            if (Bin.AnyNonZero(e[0x18..0x2C]) || Bin.AnyNonZero(e[0x3E..0x40]))
            {
                throw new InvalidDataException($"entry 0x{o:x}: unexpected data in reserved fields");
            }
            ReadOnlySpan<byte> nameField = e[0x0C..0x14];
            int nul = nameField.IndexOf((byte)0);
            List<object?> tracks = [];
            JObj a = new()
            {
                ["name"] = Bin.Latin1(nul < 0 ? nameField : nameField[..nul]),
                ["hash"] = (long)h,
                ["tuning_hash"] = (long)tuning,
                ["duration"] = dur,
                ["unknown_30"] = f30,
                ["translation_range"] = f40,
                ["scale_range"] = f44,
                ["unknown_48"] = f48,
                ["unknown_4c"] = f4c,
                ["translation_bytes"] = (long)tw,
                ["scale_bytes"] = (long)sw,
                ["unknown_52"] = (long)e[0x52],
                ["unknown_53"] = (long)e[0x53],
                ["track_counts"] = J.Longs(e[0x38..0x3E]),
                ["name_bytes"] = Bin.Hex(nameField),
                ["leftover_08"] = (long)x08,
                ["leftover_54"] = Bin.Hex(e[0x54..]),
                ["tracks"] = tracks,
            };
            float rt = 100f * (float)f40;
            float rs = (float)f44;
            for (int i = 0; i < e[0x38]; i++)
            {
                int r = (int)tt + (i * 16);
                uint cnt = Bin.U32(b, r, big);
                uint ko = Bin.U32(b, r + 4, big);
                int bone = Bin.U8(b, r + 8);
                int ch = Bin.U8(b, r + 9);
                if (Bin.AnyNonZero(Bin.Slice(b, r + 10, r + 16)) || (ch & ~7) != 0)
                {
                    throw new InvalidDataException($"track 0x{r:x}: unknown flags");
                }
                List<object?> keys = [];
                int p = (int)ko;
                for (uint k = 0; k < cnt; k++)
                {
                    JObj key = new() { ["frame"] = (long)Bin.U8(b, p) };
                    p++;
                    if ((ch & 1) != 0)
                    {
                        key["rotation"] = J.Doubles(QuatDecode(Bin.U32(b, p, big)).Select(Short));
                        p += 4;
                    }
                    if ((ch & 2) != 0)
                    {
                        key["translation"] = J.Doubles([SmRead(b, p, tw, rt), SmRead(b, p + tw, tw, rt), SmRead(b, p + (2 * tw), tw, rt)]);
                        p += 3 * tw;
                    }
                    if ((ch & 4) != 0)
                    {
                        key["scale"] = J.Doubles([SmRead(b, p, sw, rs), SmRead(b, p + sw, sw, rs), SmRead(b, p + (2 * sw), sw, rs)]);
                        p += 3 * sw;
                    }
                    keys.Add(key);
                }
                int padEnd = (Bin.CeilDiv(p - (int)ko, 4) * 4) + (int)ko;
                ReadOnlySpan<byte> pad = Bin.Slice(b, p, padEnd);
                JObj t = new()
                {
                    ["bone"] = (long)bone,
                    ["channels"] = J.List(Enumerable.Range(0, 3).Where(j => ((ch >> j) & 1) != 0).Select(j => (object?)Channels[j])),
                    ["keys"] = keys,
                };
                if (pad.IndexOfAnyExcept((byte)0xFE) >= 0)
                {
                    t["padding"] = Bin.Hex(pad);
                }
                tracks.Add(t);
                limit = Math.Min(limit, ko);
            }
            limit = Math.Min(limit, tracks.Count > 0 ? tt : end);
            anims.Add(a);
            o += 0x80;
        }
        return doc;
    }
    public static byte[] Write(JObj doc)
    {
        bool big = J.S(doc["endian"]) == "big";
        JObj hd = J.O(doc["header"]);
        List<object?> anims = J.A(doc["animations"]);
        List<byte> buf = [];
        ByteWriter head = new();
        head.U32(12, big);
        head.F32(J.D(hd["unknown_04"]), big);
        head.U32(J.U(hd["leftover_08"]), big);
        buf.AddRange(head.Span);
        buf.AddRange(new byte[0x80 * anims.Count]);
        for (int n = 0; n < anims.Count; n++)
        {
            JObj a = J.O(anims[n]);
            int tw = J.I(a["translation_bytes"]);
            int sw = J.I(a["scale_bytes"]);
            float rt = 100f * (float)J.D(a["translation_range"]);
            float rs = (float)J.D(a["scale_range"]);
            List<object?> tracks = J.A(a["tracks"]);
            List<int> offs = [];
            foreach (object? to in tracks)
            {
                JObj t = J.O(to);
                List<object?> channels = J.A(t["channels"]);
                offs.Add(buf.Count);
                int start = buf.Count;
                foreach (object? ko in J.A(t["keys"]))
                {
                    JObj k = J.O(ko);
                    long frame = J.L(k["frame"]);
                    if (frame is < 0 or > 255)
                    {
                        throw new InvalidDataException("byte must be in range(0, 256)");
                    }
                    buf.Add((byte)frame);
                    if (channels.Contains("rotation"))
                    {
                        ByteWriter q = new(4);
                        q.U32(QuatEncode([.. J.A(k["rotation"]).Select(J.D)]), big);
                        buf.AddRange(q.Span);
                    }
                    foreach ((string nm, int w, float rng) in new[] { ("translation", tw, rt), ("scale", sw, rs) })
                    {
                        if (channels.Contains(nm))
                        {
                            foreach (object? x in J.A(k[nm]))
                            {
                                buf.AddRange(SmWrite(J.D(x), rng, w));
                            }
                        }
                    }
                }
                int pad = Bin.Mod(-(buf.Count - start), 4);
                byte[] stored = Bin.FromHex(t.Get("padding") as string ?? string.Empty);
                if (stored.Length == pad)
                {
                    buf.AddRange(stored);
                }
                else
                {
                    buf.AddRange(Enumerable.Repeat((byte)0xFE, pad));
                }
            }
            int tt = tracks.Count > 0 ? buf.Count : 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                JObj t = J.O(tracks[i]);
                int ch = 0;
                foreach (object? c in J.A(t["channels"]))
                {
                    int idx = Array.IndexOf(Channels, J.S(c));
                    if (idx < 0)
                    {
                        throw new InvalidDataException($"'{c}' is not in list");
                    }
                    ch += 1 << idx;
                }
                ByteWriter tr = new(16);
                tr.U32((uint)J.A(t["keys"]).Count, big);
                tr.U32((uint)offs[i], big);
                tr.Add(J.U8(t["bone"]));
                tr.Add(J.U8((long)ch));
                tr.Zeros(6);
                buf.AddRange(tr.Span);
            }
            List<long> counts = [.. J.A(a["track_counts"]).Select(J.L)];
            counts[0] = tracks.Count;
            byte[] name = Bin.FromHex(J.S(a["name_bytes"]));
            int nul = Array.IndexOf(name, (byte)0);
            if (Bin.Latin1(nul < 0 ? name : name[..nul]) != J.S(a["name"]))
            {
                byte[] raw = Bin.Latin1(J.S(a["name"]));
                name = new byte[8];
                raw.AsSpan(0, Math.Min(8, raw.Length)).CopyTo(name);
            }
            ByteWriter e = new(0x80);
            e.U32(J.U(a["hash"]), big);
            e.U32(J.U(a["tuning_hash"]), big);
            e.U32(J.U(a["leftover_08"]), big);
            e.Add(name);
            e.U32((uint)tt, big);
            e.Zeros(20);
            e.U32((uint)buf.Count, big);
            e.F32(J.D(a["unknown_30"]), big);
            e.F32(J.D(a["duration"]), big);
            foreach (long c in counts)
            {
                e.Add(J.U8(c));
            }
            e.Zeros(2);
            e.F32(J.D(a["translation_range"]), big);
            e.F32(J.D(a["scale_range"]), big);
            e.F32(J.D(a["unknown_48"]), big);
            e.F32(J.D(a["unknown_4c"]), big);
            e.Add((byte)tw);
            e.Add((byte)sw);
            e.Add(J.U8(a["unknown_52"]));
            e.Add(J.U8(a["unknown_53"]));
            e.Add(Bin.FromHex(J.S(a["leftover_54"])));
            int at = 12 + (n * 0x80);
            int stop = Math.Min(at + 0x80, buf.Count);
            buf.RemoveRange(at, stop - at);
            buf.InsertRange(at, e.ToArray());
        }
        return [.. buf];
    }
    public static string Dumps(JObj doc)
    {
        string s = PyJson.Dumps(doc, 1);
        s = FlatList().Replace(s, m => "[" + string.Join(' ', Py.SplitWhitespace(m.Groups[1].Value)) + "]");
        return KeyObject().Replace(s, m => "{" + string.Join(' ', Py.SplitWhitespace(m.Groups[1].Value)) + "}");
    }
    public static void AddNames(JObj doc, IEnumerable<string> names)
    {
        Dictionary<uint, string> table = [];
        foreach (string n in names)
        {
            if (Hashes.TryUpperCrc(n, out uint h))
            {
                table[h] = n;
            }
        }
        foreach (object? ao in J.A(doc["animations"]))
        {
            JObj a = J.O(ao);
            if (table.TryGetValue(J.U(a["hash"]), out string? full))
            {
                a["full_name"] = full;
            }
        }
    }
}
