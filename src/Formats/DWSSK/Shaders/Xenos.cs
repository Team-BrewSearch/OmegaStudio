using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
using static DWSSK.Formats.Shaders.Bits;

namespace DWSSK.Formats.Shaders;

internal static class Xenos
{
    private static readonly string[] s_dims = ["1D", "2D", "3D", "Cube"];

    private const string Xyzw = "xyzw";

    public static readonly string[] Cf =
    [
        "nop", "exec", "exece", "cexec", "cexece", "cexec_pred", "cexece_pred", "loop", "endloop", "ccall", "ret",
        "cjmp", "alloc", "cexec_pred_clean", "cexece_pred_clean", "vfetch_done",
    ];

    private static readonly (string Name, int N)[] s_vec =
    [
        ("add", 2), ("mul", 2), ("max", 2), ("min", 2), ("seq", 2), ("sgt", 2), ("sge", 2), ("sne", 2),
        ("frc", 1), ("trunc", 1), ("floor", 1), ("mad", 3), ("cndeq", 3), ("cndge", 3), ("cndgt", 3), ("dp4", 2),
        ("dp3", 2), ("dp2add", 3), ("cube", 2), ("max4", 1), ("setp_eq_push", 2), ("setp_ne_push", 2),
        ("setp_gt_push", 2), ("setp_ge_push", 2), ("kill_eq", 2), ("kill_gt", 2), ("kill_ge", 2), ("kill_ne", 2),
        ("dst", 2), ("maxa", 2),
    ];

    private static readonly (string Name, string Kind)[] s_sca =
    [
        ("adds", "2"), ("adds_prev", "1"), ("muls", "2"), ("muls_prev", "1"), ("muls_prev2", "2"),
        ("maxs", "2"), ("mins", "2"), ("seqs", "1"), ("sgts", "1"), ("sges", "1"), ("snes", "1"), ("frcs", "1"),
        ("truncs", "1"), ("floors", "1"), ("exp", "1"), ("logc", "1"), ("log", "1"), ("rcpc", "1"),
        ("rcpf", "1"), ("rcp", "1"), ("rsqc", "1"), ("rsqf", "1"), ("rsq", "1"), ("maxas", "2"),
        ("maxasf", "2"), ("subs", "2"), ("subs_prev", "1"), ("setp_eq", "1"), ("setp_ne", "1"),
        ("setp_gt", "1"), ("setp_ge", "1"), ("setp_inv", "1"), ("setp_pop", "1"), ("setp_clr", "0"),
        ("setp_rstr", "1"), ("kills_eq", "1"), ("kills_gt", "1"), ("kills_ge", "1"), ("kills_ne", "1"),
        ("kills_one", "1"), ("sqrt", "1"), ("op41", "0"), ("mulsc", "c"), ("mulsc", "c"), ("addsc", "c"),
        ("addsc", "c"), ("subsc", "c"), ("subsc", "c"), ("sin", "1"), ("cos", "1"), ("retain_prev", "0"),
    ];

    private static readonly Dictionary<long, string> s_vfmt = new()
    {
        [6] = "8_8_8_8", [7] = "2_10_10_10", [10] = "10_11_11", [11] = "11_11_10", [25] = "16_16", [26] = "16_16_16_16",
        [31] = "16_16_FLOAT", [32] = "16_16_16_16_FLOAT", [33] = "32", [34] = "32_32", [35] = "32_32_32_32", [36] = "32_FLOAT",
        [37] = "32_32_FLOAT", [38] = "32_32_32_32_FLOAT", [57] = "32_32_32_FLOAT",
    };

    private static readonly string[] s_exec = ["exec", "exece", "cexec", "cexece", "cexec_pred", "cexece_pred", "cexec_pred_clean", "cexece_pred_clean"];

    private static readonly Dictionary<long, string> s_fetch = new()
    {
        [16] = "getBCF", [17] = "getCompTexLOD", [18] = "getGradients", [19] = "getWeights", [24] = "setTexLOD",
        [25] = "setGradientH", [26] = "setGradientV",
    };

    private static readonly Dictionary<string, long> s_vecN = s_vec.Select((v, k) => (v.Name, (long)k)).ToDictionary(t => t.Name, t => t.Item2);
    private static readonly Dictionary<string, long> s_scaN = BuildScaN();
    private static readonly Dictionary<string, long> s_vfmtN = s_vfmt.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static Dictionary<string, long> BuildScaN()
    {
        Dictionary<string, long> d = [];
        for (int k = 0; k < s_sca.Length; k++)
        {
            d.TryAdd(s_sca[k].Name, k);
        }
        return d;
    }

    public static bool IsExec(long hi)
    {
        return s_exec.Contains(Cf[B(hi, 12, 4)]);
    }

    public static string Export(long n, bool vs)
    {
        if (vs)
        {
            return n switch
            {
                62 => "oPos",
                63 => "oPts",
                32 => "eA",
                _ => n < 16 ? $"o{D(n)}" : n is >= 33 and <= 37 ? $"eM{D(n - 33)}" : $"export{D(n)}",
            };
        }
        return n switch
        {
            61 => "oDepth",
            32 => "eA",
            _ => n < 4 ? $"oC{D(n)}" : n is >= 33 and <= 37 ? $"eM{D(n - 33)}" : $"export{D(n)}",
        };
    }

    private static long ExportN(string name, bool vs)
    {
        for (int n = 0; n < 64; n++)
        {
            if (Export(n, vs) == name)
            {
                return n;
            }
        }
        throw new InvalidDataException(name);
    }

    public static string Alu(long d0, long d1, long d2, bool vs)
    {
        long export = B(d0, 15, 1);
        long vmask = B(d0, 16, 4);
        long smask = B(d0, 20, 4);
        long vopN = B(d2, 24, 5);
        (string vop, int vn) = vopN < s_vec.Length ? s_vec[vopN] : ($"vop{D(vopN)}", 2);
        long sopN = B(d0, 26, 6);
        (string sop, string sn) = sopN < s_sca.Length ? s_sca[sopN] : ($"sop{D(sopN)}", "1");
        long[] sels = [B(d2, 31, 1), B(d2, 30, 1), B(d2, 29, 1)];
        long[] regs = [B(d2, 16, 8), B(d2, 8, 8), B(d2, 0, 8)];
        long[] swz = [B(d1, 16, 8), B(d1, 8, 8), B(d1, 0, 8)];
        long[] neg = [B(d1, 26, 1), B(d1, 25, 1), B(d1, 24, 1)];
        long Addressed(int i)
        {
            if (i == 0)
            {
                return B(d1, 31, 1);
            }
            if (i == 1)
            {
                return sels[0] != 0 ? B(d1, 31, 1) : B(d1, 30, 1);
            }
            return sels[0] != 0 && sels[1] != 0 ? B(d1, 31, 1) : B(d1, 30, 1);
        }
        string Reg(int i)
        {
            long r = regs[i];
            if (sels[i] != 0)
            {
                string name = (r & 0x40) == 0 ? $"r{D(r & 0x3F)}" : $"r[{D(r & 0x3F)}+aL]";
                return (r & 0x80) != 0 ? "|" + name + "|" : name;
            }
            string idx = Addressed(i) != 0 ? (B(d1, 29, 1) != 0 ? $"c[{D(r)}+a0]" : $"c[{D(r)}+aL]") : $"c{D(r)}";
            return B(d0, 7, 1) != 0 ? $"|{idx}|" : idx;
        }
        string Src(int i)
        {
            string sw = string.Concat(Enumerable.Range(0, 4).Select(k => Xyzw[(int)((B(swz[i], 2 * k, 2) + k) & 3)]));
            return (neg[i] != 0 ? "-" : string.Empty) + Reg(i) + (sw == "xyzw" ? string.Empty : "." + sw);
        }
        string Dest(long n, bool rel, long mask, long const0 = 0, long const1 = 0)
        {
            string name = export != 0 ? Export(n, vs) : rel ? $"r[{D(n)}+aL]" : $"r{D(n)}";
            string m = string.Concat(Enumerable.Range(0, 4).Select(k =>
                ((const0 >> k) & 1) != 0 ? "0" : ((const1 >> k) & 1) != 0 ? "1" : ((mask >> k) & 1) != 0 ? Xyzw[k].ToString() : "_"));
            return name + (m == "xyzw" ? string.Empty : "." + m);
        }
        List<string> parts = [];
        string pred = B(d1, 28, 1) != 0 ? (B(d1, 27, 1) != 0 ? " (p0)" : " (!p0)") : string.Empty;
        long vecMask = export != 0 ? vmask & ~smask : vmask;
        long scaMask = export != 0 ? smask & ~vmask : smask;
        long c0 = export != 0 && B(d0, 14, 1) != 0 ? 0b1111 & ~(vmask | smask) : 0;
        long c1 = export != 0 ? vmask & smask : 0;
        if (vecMask != 0 || c0 != 0 || c1 != 0 || vop.StartsWith("kill", StringComparison.Ordinal) || vop.StartsWith("setp", StringComparison.Ordinal) || vop.StartsWith("maxa", StringComparison.Ordinal))
        {
            string sat = B(d0, 24, 1) != 0 ? "_sat" : string.Empty;
            List<string> ops = [.. Enumerable.Range(0, vn).Select(Src)];
            parts.Add($"{vop}{sat}{pred} {Dest(B(d0, 0, 6), B(d0, 6, 1) != 0, vecMask, c0, c1)}, " + string.Join(", ", ops));
        }
        if (scaMask != 0 || sop.StartsWith("kills", StringComparison.Ordinal) || sop.StartsWith("setp", StringComparison.Ordinal) || sop.StartsWith("maxas", StringComparison.Ordinal))
        {
            string sat = B(d0, 25, 1) != 0 ? "_sat" : string.Empty;
            long sdst = export != 0 ? B(d0, 0, 6) : B(d0, 8, 6);
            char a = Xyzw[(int)((B(swz[2], 6, 2) + 3) & 3)];
            char b = Xyzw[(int)B(swz[2], 0, 2)];
            List<string> ops;
            string n2 = neg[2] != 0 ? "-" : string.Empty;
            if (sn == "2")
            {
                ops = [n2 + Reg(2) + "." + a + b];
            }
            else if (sn == "1")
            {
                ops = [n2 + Reg(2) + "." + a];
            }
            else if (sn == "c")
            {
                long temp = (sopN & 1) | (sels[2] << 1) | (swz[2] & 0x3C);
                ops = [n2 + $"c{D(regs[2])}" + "." + a, n2 + $"r{D(temp)}." + b];
            }
            else
            {
                ops = [];
            }
            parts.Add($"{sop}{sat}{pred} {Dest(sdst, B(d0, 14, 1) != 0 && export == 0, scaMask)}" + string.Concat(ops.Select(o => ", " + o)));
        }
        return parts.Count > 0 ? string.Join(" + ", parts) : "nop";
    }

    public static string Fetch(long d0, long d1, long d2)
    {
        long op = B(d0, 0, 5);
        string pred = B(d1, 31, 1) != 0 ? (B(d2, 31, 1) != 0 ? " (p0)" : " (!p0)") : string.Empty;
        string dsw = string.Concat(Enumerable.Range(0, 4).Select(k => "xyzw01?_"[(int)B(d1, 3 * k, 3)]));
        string dst = $"r{D(B(d0, 12, 6))}" + (dsw == "xyzw" ? string.Empty : "." + dsw);
        if (op == 0)
        {
            long f = B(d1, 16, 6);
            string fmt = s_vfmt.TryGetValue(f, out string? fn) ? fn : D(f);
            long offset = B(d2, 8, 23) - (B(d2, 30, 1) != 0 ? 1L << 23 : 0);
            string text = $"vfetch{(B(d1, 30, 1) != 0 ? "_mini" : string.Empty)}{pred} {dst}, r{D(B(d0, 5, 6))}.{Xyzw[(int)B(d0, 30, 2)]}, "
                + $"vf{D((B(d0, 20, 5) * 3) + B(d0, 25, 2))}, format={fmt}, offset={D(offset)}, stride={D(B(d2, 0, 8))}";
            if (B(d1, 14, 1) != 0)
            {
                text += ", signed";
            }
            if (B(d1, 13, 1) != 0)
            {
                text += ", integer";
            }
            return text;
        }
        if (op == 1)
        {
            long dim = B(d2, 14, 2);
            int n = new[] { 1, 2, 3, 3 }[dim];
            string ssw = string.Concat(Enumerable.Range(0, n).Select(k => Xyzw[(int)B(d0, 26 + (2 * k), 2)]));
            return $"tfetch{new[] { "1D", "2D", "3D", "Cube" }[dim]}{pred} {dst}, r{D(B(d0, 5, 6))}.{ssw}, tf{D(B(d0, 20, 5))}"
                + (B(d2, 2, 7) != 0 ? ", lod_bias=" + D(B(d2, 2, 7) - (B(d2, 8, 1) != 0 ? 128 : 0)) : string.Empty);
        }
        return $"{(s_fetch.TryGetValue(op, out string? name) ? name : $"fetch{D(op)}")}{pred} {dst}, r{D(B(d0, 5, 6))}, tf{D(B(d0, 20, 5))}";
    }

    public static string CfText(long lo, long hi, bool vs)
    {
        string op = Cf[B(hi, 12, 4)];
        if (s_exec.Contains(op))
        {
            if (op.Contains("pred", StringComparison.Ordinal))
            {
                return $"{op} {(B(hi, 10, 1) != 0 ? string.Empty : "!")}p0";
            }
            if (op.StartsWith("cexec", StringComparison.Ordinal))
            {
                return $"{op} {(B(hi, 10, 1) != 0 ? string.Empty : "!")}b{D(B(hi, 2, 8))}";
            }
            return op;
        }
        if (op == "alloc")
        {
            long kind = B(hi, 9, 2);
            string kname = kind switch
            {
                1 => "position",
                2 => vs ? "interpolators" : "colors",
                3 => "export",
                _ => D(kind),
            };
            return $"alloc {kname}" + (kind == 3 ? $" = {D(B(lo, 0, 3))}" : string.Empty);
        }
        if (op is "loop" or "endloop")
        {
            return $"{op} i{D(B(lo, 16, 5))}, cf{D2(B(lo, 0, 13))}";
        }
        if (op is "ccall" or "cjmp")
        {
            string cond = B(lo, 13, 1) != 0 ? string.Empty : $" {(B(hi, 10, 1) != 0 ? string.Empty : "!")}b{D(B(hi, 2, 8))},";
            return $"{op}{cond} cf{D2(B(lo, 0, 13))}";
        }
        return op;
    }

    public static List<string> Disasm(ReadOnlySpan<byte> ucode, bool vs)
    {
        int nwords = ucode.Length / 4;
        long[] words = new long[nwords];
        for (int i = 0; i < nwords; i++)
        {
            words[i] = Bin.U32(ucode, i * 4, true);
        }
        List<long[]> slots = [];
        for (int k = 0; k < nwords; k += 3)
        {
            slots.Add(words[k..Math.Min(k + 3, nwords)]);
        }
        List<(long Lo, long Hi)> cfs = [];
        int idx = 0;
        int limit = slots.Count;
        while (idx < limit)
        {
            long[] s = slots[idx];
            if (s.Length != 3)
            {
                throw new InvalidDataException($"not enough values to unpack (expected 3, got {s.Length})");
            }
            (long a, long b2, long c) = (s[0], s[1], s[2]);
            foreach ((long lo, long hi) in new[] { (a, b2 & 0xFFFF), (((b2 >> 16) | (c << 16)) & 0xFFFFFFFF, c >> 16) })
            {
                cfs.Add((lo, hi));
                if (IsExec(hi))
                {
                    limit = (int)Math.Min(limit, B(lo, 0, 12));
                }
            }
            idx++;
        }
        List<string> output = [];
        HashSet<long> seen = [];
        for (int n = 0; n < cfs.Count; n++)
        {
            (long lo, long hi) = cfs[n];
            output.Add($"cf{D2(n)}: {Hex(hi, 4)}{Hex(lo, 8)}  {CfText(lo, hi, vs)}");
            if (IsExec(hi))
            {
                long addr = B(lo, 0, 12);
                long seq = B(lo, 16, 12);
                for (int i = 0; i < B(lo, 12, 3); i++)
                {
                    long[] s = addr + i < slots.Count ? slots[(int)(addr + i)] : [0, 0, 0];
                    if (s.Length != 3)
                    {
                        throw new InvalidDataException($"not enough values to unpack (expected 3, got {s.Length})");
                    }
                    string body = ((seq >> (2 * i)) & 1) != 0 ? Fetch(s[0], s[1], s[2]) : Alu(s[0], s[1], s[2], vs);
                    output.Add($"  {Hex(addr + i, 4)}: {string.Join(' ', s.Select(x => Hex(x, 8)))}  {body}"
                        + (((seq >> ((2 * i) + 1)) & 1) != 0 ? "  ; serialize" : string.Empty));
                    seen.Add(addr + i);
                }
            }
        }
        for (int i = 0; i < slots.Count; i++)
        {
            if (i >= limit && !seen.Contains(i))
            {
                output.Add($"  {Hex(i, 4)}: {string.Join(' ', slots[i].Select(x => Hex(x, 8)))}  data");
            }
        }
        return output;
    }

    private static (long Op, bool Sat, bool? Pred, List<string> Ops) SplitOp(string part, Dictionary<string, long> table)
    {
        Match m = Rx.Full(@"(\S+?)(_sat)?(?: \((!?)p0\))?(?: (.*))?", part) ?? throw new InvalidDataException($"'{part}' is not an instruction");
        string name = m.Groups[1].Value;
        long op;
        if (table.TryGetValue(name, out long found))
        {
            op = found;
        }
        else
        {
            Match n = Rx.Full(@"[vs]op(\d+)", name) ?? throw new InvalidDataException($"'{name}' is not an instruction");
            op = Rx.Int(n.Groups[1].Value);
        }
        bool? pred = m.Groups[3].Success ? m.Groups[3].Value.Length == 0 : null;
        List<string> ops = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? [.. m.Groups[4].Value.Split(", ")] : [];
        return (op, m.Groups[2].Success, pred, ops);
    }

    public static (long, long, long) AluParse(string text, long d0, long d1, long d2, bool vs)
    {
        List<string> parts = text == "nop" ? [] : [.. text.Split(" + ")];
        string? vec = parts.Count > 0 && s_vecN.ContainsKey(Regex.Replace(parts[0].Split(' ')[0], "_sat$", string.Empty)) ? parts[0] : null;
        string? sca = parts.Count > 0 && !(parts.Count == 1 && vec is not null) ? parts[^1] : null;
        long[] sels = [B(d2, 31, 1), B(d2, 30, 1), B(d2, 29, 1)];
        long[] regs = [B(d2, 16, 8), B(d2, 8, 8), B(d2, 0, 8)];
        long[] swz = [B(d1, 16, 8), B(d1, 8, 8), B(d1, 0, 8)];
        bool[] neg = [B(d1, 26, 1) != 0, B(d1, 25, 1) != 0, B(d1, 24, 1) != 0];
        List<(int I, bool Rel)> rel = [];
        bool? pred = null;
        void SetRel(int i, bool r)
        {
            int at = rel.FindIndex(x => x.I == i);
            if (at >= 0)
            {
                rel[at] = (i, r);
            }
            else
            {
                rel.Add((i, r));
            }
        }
        void Reg(int i, string t)
        {
            bool ab = t.StartsWith('|');
            t = ab ? t[1..^1] : t;
            if (t.StartsWith('r'))
            {
                Match m = Rx.Full(@"r(\d+)|r\[(\d+)\+aL\]", t) ?? throw new InvalidDataException($"'{t}' is not a register");
                sels[i] = 1;
                regs[i] = Rx.Int(m.G(1) ?? m.G(2)!) | (m.G(2) is not null ? 0x40L : 0L) | (ab ? 0x80L : 0L);
            }
            else
            {
                Match m = Rx.Full(@"c(\d+)|c\[(\d+)\+(a0|aL)\]", t) ?? throw new InvalidDataException($"'{t}' is not a constant");
                sels[i] = 0;
                regs[i] = Rx.Int(m.G(1) ?? m.G(2)!);
                SetRel(i, m.G(2) is not null);
                if (m.G(3) is { } a)
                {
                    d1 = Put(d1, 29, 1, a == "a0");
                }
                d0 = Put(d0, 7, 1, ab);
            }
        }
        void Src(int i, string t)
        {
            neg[i] = t.StartsWith('-');
            t = neg[i] ? t[1..] : t;
            Match m = Rx.Full(@"(.*?)((?:\.[xyzw]{4})?)", t)!;
            Reg(i, m.Groups[1].Value);
            int[] sel = Rsx.SwzSel(m.Groups[2].Value);
            long s = 0;
            for (int k = 0; k < sel.Length; k++)
            {
                s += ((sel[k] - k) & 3) << (2 * k);
            }
            swz[i] = s;
        }
        static (string Name, string Mask) Dest(string t)
        {
            int dot = t.IndexOf('.', StringComparison.Ordinal);
            string name = dot < 0 ? t : t[..dot];
            string mask = dot < 0 ? string.Empty : t[(dot + 1)..];
            return (name, mask.Length > 0 ? mask : Xyzw);
        }
        bool? export = null;
        long vmask = 0;
        long smask = 0;
        long c0 = 0;
        long c1 = 0;
        if (!string.IsNullOrEmpty(vec))
        {
            (long vop, bool sat, bool? p, List<string> ops) = SplitOp(vec, s_vecN);
            pred = p;
            d2 = Put(d2, 24, 5, vop);
            d0 = Put(d0, 24, 1, sat);
            (string name, string mask) = Dest(ops[0]);
            export = !name.StartsWith('r');
            if (export.Value)
            {
                d0 = Put(d0, 0, 6, ExportN(name, vs));
            }
            else
            {
                d0 = Put(Put(d0, 0, 6, Rx.Int(Rx.Digits(name))), 6, 1, name.Contains("aL", StringComparison.Ordinal));
            }
            for (int k = 0; k < mask.Length; k++)
            {
                char ch = mask[k];
                vmask |= (Xyzw.Contains(ch, StringComparison.Ordinal) ? 1L : 0) << k;
                c0 |= (ch == '0' ? 1L : 0) << k;
                c1 |= (ch == '1' ? 1L : 0) << k;
            }
            for (int i = 1; i < ops.Count; i++)
            {
                Src(i - 1, ops[i]);
            }
        }
        if (!string.IsNullOrEmpty(sca))
        {
            (long sop, bool sat, bool? p, List<string> ops) = SplitOp(sca, s_scaN);
            pred = pred ?? p;
            d0 = Put(d0, 25, 1, sat);
            (string name, string mask) = Dest(ops[0]);
            export ??= !name.StartsWith('r');
            if (export.Value)
            {
                d0 = Put(d0, 0, 6, ExportN(name, vs));
            }
            else
            {
                d0 = Put(Put(d0, 8, 6, Rx.Int(Rx.Digits(name))), 14, 1, name.Contains("aL", StringComparison.Ordinal));
            }
            for (int k = 0; k < mask.Length; k++)
            {
                smask |= (Xyzw.Contains(mask[k], StringComparison.Ordinal) ? 1L : 0) << k;
            }
            if (sop >= s_sca.Length)
            {
                throw new PyIndexException("tuple index out of range");
            }
            string kind = s_sca[sop].Kind;
            if (kind is "1" or "2")
            {
                string t = ops[1];
                neg[2] = t.StartsWith('-');
                t = neg[2] ? t[1..] : t;
                int dot = t.LastIndexOf('.');
                string r = dot < 0 ? string.Empty : t[..dot];
                string ab = dot < 0 ? t : t[(dot + 1)..];
                Reg(2, r);
                swz[2] = Put(swz[2], 6, 2, (Index(ab[0].ToString()) - 3) & 3);
                if (kind == "2")
                {
                    swz[2] = Put(swz[2], 0, 2, Index(ab[1].ToString()));
                }
            }
            else if (kind == "c")
            {
                neg[2] = ops[1].StartsWith('-');
                string[] ca = ops[1].TrimStart('-').Split('.');
                string[] tb = ops[2].TrimStart('-').Split('.');
                if (ca.Length != 2 || tb.Length != 2)
                {
                    throw new InvalidDataException("not enough values to unpack (expected 2)");
                }
                long temp = Rx.Int(tb[0][1..]);
                regs[2] = Rx.Int(ca[0][1..]);
                sels[2] = (temp >> 1) & 1;
                sop |= temp & 1;
                swz[2] = (long)Index(tb[1]) | (temp & 0x3C) | (long)(((Index(ca[1]) - 3) & 3) << 6);
            }
            d0 = Put(d0, 26, 6, sop);
        }
        if (export is not null)
        {
            d0 = Put(d0, 15, 1, export.Value);
        }
        if (export == true)
        {
            vmask |= c1;
            smask |= c1;
            if (c0 != 0 || (vmask | smask) != 15)
            {
                d0 = Put(d0, 14, 1, c0 != 0);
            }
        }
        bool hasVec = !string.IsNullOrEmpty(vec);
        bool hasSca = !string.IsNullOrEmpty(sca);
        if (hasVec || (export == true && hasSca))
        {
            d0 = Put(d0, 16, 4, vmask);
        }
        if (hasSca || (export == true && hasVec))
        {
            d0 = Put(d0, 20, 4, smask);
        }
        if (!hasVec && export != true)
        {
            d0 = Put(d0, 16, 4, 0);
        }
        if (!hasSca && export != true)
        {
            d0 = Put(d0, 20, 4, 0);
        }
        if (parts.Count == 0)
        {
            d0 = Put(Put(d0, 16, 4, 0), 20, 4, 0);
        }
        d1 = Put(d1, 28, 1, pred is not null);
        if (pred is not null)
        {
            d1 = Put(d1, 27, 1, pred.Value);
        }
        foreach ((int i, bool r) in rel)
        {
            int bit = i == 0 || (i == 1 && sels[0] != 0) || (i == 2 && sels[0] != 0 && sels[1] != 0) ? 31 : 30;
            d1 = Put(d1, bit, 1, r);
        }
        for (int i = 0; i < 3; i++)
        {
            d1 = Put(Put(d1, 16 - (8 * i), 8, swz[i]), 26 - i, 1, neg[i]);
            d2 = Put(Put(d2, 16 - (8 * i), 8, regs[i]), 31 - i, 1, sels[i]);
        }
        return (d0, d1, d2);
    }

    public static (long, long, long) FetchParse(string text, long d0, long d1, long d2)
    {
        Match m = Rx.Full(@"(\w+?)(?: \((!?)p0\))? (.*)", text) ?? throw new InvalidDataException($"'{text}' is not a fetch");
        string name = m.Groups[1].Value;
        string? pred = m.G(2);
        List<string> ops = [.. m.Groups[3].Value.Split(", ")];
        d1 = Put(d1, 31, 1, pred is not null);
        if (pred is not null)
        {
            d2 = Put(d2, 31, 1, pred.Length == 0);
        }
        (string dr, string dsw) = Part(ops[0]);
        d0 = Put(d0, 12, 6, Rx.Int(dr[1..]));
        string dmask = dsw.Length > 0 ? dsw : Xyzw;
        for (int k = 0; k < dmask.Length; k++)
        {
            d1 = Put(d1, 3 * k, 3, "xyzw01?_".IndexOf(dmask[k], StringComparison.Ordinal) is int x and >= 0 ? x : throw new InvalidDataException($"'{dmask[k]}' is not a swizzle"));
        }
        (string sr, string ssw) = Part(ops[1]);
        d0 = Put(d0, 5, 6, Rx.Int(sr[1..]));
        if (name.StartsWith("vfetch", StringComparison.Ordinal))
        {
            d0 = Put(d0, 0, 5, 0);
            d1 = Put(d1, 30, 1, name == "vfetch_mini");
            d0 = Put(Put(d0, 30, 2, Index(ssw)), 20, 5, Rx.Int(ops[2][2..]) / 3);
            d0 = Put(d0, 25, 2, Rx.Int(ops[2][2..]) % 3);
            Dictionary<string, string> kv = [];
            foreach (string o in ops.Skip(3).Where(o => o.Contains('=', StringComparison.Ordinal)))
            {
                string[] pair = o.Split('=');
                if (pair.Length != 2)
                {
                    throw new InvalidDataException($"dictionary update sequence element has length {pair.Length}; 2 is required");
                }
                kv[pair[0]] = pair[1];
            }
            string fmt = kv["format"];
            d1 = Put(d1, 16, 6, s_vfmtN.TryGetValue(fmt, out long fv) ? fv : Rx.Int(fmt));
            d2 = Put(Put(d2, 8, 23, Rx.Int(kv["offset"])), 0, 8, Rx.Int(kv["stride"]));
            d1 = Put(Put(d1, 14, 1, ops.Contains("signed")), 13, 1, ops.Contains("integer"));
        }
        else if (name.StartsWith("tfetch", StringComparison.Ordinal))
        {
            int dim = Array.IndexOf(s_dims, name[6..]);
            if (dim < 0)
            {
                throw new InvalidDataException($"'{name[6..]}' is not in tuple");
            }
            d0 = Put(d0, 0, 5, 1);
            d2 = Put(d2, 14, 2, dim);
            for (int k = 0; k < ssw.Length; k++)
            {
                d0 = Put(d0, 26 + (2 * k), 2, Index(ssw[k].ToString()));
            }
            d0 = Put(d0, 20, 5, Rx.Int(ops[2][2..]));
            long lod = ops.Count > 3 ? Rx.Int(ops[3].Split('=')[1]) : 0;
            d2 = Put(d2, 2, 7, lod < 0 ? lod + 128 : lod);
        }
        else
        {
            long op = s_fetch.FirstOrDefault(kv => kv.Value == name).Key;
            if (op == 0)
            {
                op = Rx.Int(name[5..]);
            }
            d0 = Put(Put(d0, 0, 5, op), 20, 5, Rx.Int(ops[2][2..]));
        }
        return (d0, d1, d2);
    }

    private static int Index(string c)
    {
        int i = Xyzw.IndexOf(c, StringComparison.Ordinal);
        return i >= 0 ? i : throw new InvalidDataException("substring not found");
    }

    private static (string, string) Part(string t)
    {
        int dot = t.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (t, string.Empty) : (t[..dot], t[(dot + 1)..]);
    }

    public static (long, long) CfParse(string text, long lo, long hi)
    {
        int sp = text.IndexOf(' ', StringComparison.Ordinal);
        string op = sp < 0 ? text : text[..sp];
        string rest = sp < 0 ? string.Empty : text[(sp + 1)..];
        int opIndex = Array.IndexOf(Cf, op);
        if (opIndex < 0)
        {
            throw new InvalidDataException($"'{op}' is not in list");
        }
        hi = Put(hi, 12, 4, opIndex);
        if (s_exec.Contains(op) && rest.Length > 0)
        {
            hi = Put(hi, 10, 1, !rest.StartsWith('!'));
            string r = rest.TrimStart('!');
            if (r.StartsWith('b'))
            {
                hi = Put(hi, 2, 8, Rx.Int(r[1..]));
            }
        }
        else if (op == "alloc")
        {
            int eq = rest.IndexOf(" = ", StringComparison.Ordinal);
            string kind = eq < 0 ? rest : rest[..eq];
            string n = eq < 0 ? string.Empty : rest[(eq + 3)..];
            long k = kind switch
            {
                "position" => 1,
                "interpolators" or "colors" => 2,
                "export" => 3,
                _ => Rx.Int(kind),
            };
            hi = Put(hi, 9, 2, k);
            if (n.Length > 0)
            {
                lo = Put(lo, 0, 3, Rx.Int(n));
            }
        }
        else if (op is "loop" or "endloop")
        {
            Match m = Rx.Full(@"i(\d+), cf(\d+)", rest) ?? throw new InvalidDataException($"'{rest}' is not a loop target");
            lo = Put(Put(lo, 16, 5, Rx.Int(m.Groups[1].Value)), 0, 13, Rx.Int(m.Groups[2].Value));
        }
        else if (op is "ccall" or "cjmp")
        {
            Match m = Rx.Full(@"(?:(!?)b(\d+), )?cf(\d+)", rest) ?? throw new InvalidDataException($"'{rest}' is not a call target");
            lo = Put(Put(lo, 13, 1, !m.Groups[2].Success), 0, 13, Rx.Int(m.Groups[3].Value));
            if (m.Groups[2].Success)
            {
                hi = Put(Put(hi, 10, 1, m.Groups[1].Value.Length == 0), 2, 8, Rx.Int(m.Groups[2].Value));
            }
        }
        return (lo, hi);
    }

    private static bool IsFetchText(string text)
    {
        string head = text.Split(' ')[0];
        return new[] { "vfetch", "tfetch1D", "tfetch2D", "tfetch3D", "tfetchCube" }.Contains(head.Split('_')[0])
            || s_fetch.ContainsValue(head)
            || Rx.Prefix(@"fetch\d", text) is not null;
    }

    public static byte[] Asm(IReadOnlyList<string> lines, bool vs)
    {
        List<(long Lo, long Hi, List<(long Addr, bool Fetch, bool Ser)> Slots)> cfs = [];
        Dictionary<long, long[]> slots = [];
        foreach (string line in lines)
        {
            Match m = Rx.Full(@"\s*(cf\d+|[0-9a-f]{4}): (?:([0-9a-f ]{12,26})  )?(.*?)(  ; serialize)?", line)
                ?? throw new InvalidDataException($"'{line}' is not a disassembly line");
            string tag = m.Groups[1].Value;
            string? raw = m.G(2);
            string text = m.Groups[3].Value;
            bool ser = m.Groups[4].Success;
            if (tag.StartsWith("cf", StringComparison.Ordinal))
            {
                long v = raw is not null ? Rx.Int(raw, 16) : 0;
                long lo = v & 0xFFFFFFFF;
                long hi = v >> 32;
                if (CfText(lo, hi, vs) != text)
                {
                    (lo, hi) = CfParse(text, lo, hi);
                }
                cfs.Add((lo, hi, []));
                continue;
            }
            long[] w = raw is not null ? [.. raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Rx.Int(x, 16))] : [0, 0, 0];
            if (w.Length != 3)
            {
                throw new InvalidDataException($"not enough values to unpack (expected 3, got {w.Length})");
            }
            bool fetch = IsFetchText(text);
            long addr = Rx.Int(tag, 16);
            if (text != "data")
            {
                if ((fetch ? Fetch(w[0], w[1], w[2]) : Alu(w[0], w[1], w[2], vs)) != text)
                {
                    (long a, long b, long c) = fetch ? FetchParse(text, w[0], w[1], w[2]) : AluParse(text, w[0], w[1], w[2], vs);
                    w = [a, b, c];
                }
                if (cfs.Count == 0)
                {
                    throw new InvalidDataException("'NoneType' object is not subscriptable");
                }
                cfs[^1].Slots.Add((addr, fetch, ser));
            }
            slots[addr] = w;
        }
        for (int i = 0; i < cfs.Count; i++)
        {
            (long lo, long hi, List<(long Addr, bool Fetch, bool Ser)> sl) = cfs[i];
            if (IsExec(hi) && sl.Count > 0)
            {
                long seq = B(lo, 16, 12);
                for (int k = 0; k < sl.Count; k++)
                {
                    seq = Put(Put(seq, 2 * k, 1, sl[k].Fetch), (2 * k) + 1, 1, sl[k].Ser);
                }
                lo = Put(Put(Put(lo, 0, 12, sl[0].Addr), 12, 3, sl.Count), 16, 12, seq);
                cfs[i] = (lo, hi, sl);
            }
        }
        List<long> words = [];
        for (int k = 0; k < cfs.Count; k += 2)
        {
            if (k + 1 >= cfs.Count)
            {
                throw new PyIndexException("list index out of range");
            }
            (long lo0, long hi0, _) = cfs[k];
            (long lo1, long hi1, _) = cfs[k + 1];
            words.Add(lo0);
            words.Add(hi0 | ((lo1 & 0xFFFF) << 16));
            words.Add((lo1 >> 16) | (hi1 << 16));
        }
        long max = slots.Count > 0 ? slots.Keys.Max() + 1 : 0;
        for (long i = words.Count / 3; i < max; i++)
        {
            words.AddRange(slots.TryGetValue(i, out long[]? w) ? w : [0, 0, 0]);
        }
        ByteWriter o = new(words.Count * 4);
        foreach (long x in words)
        {
            if (x is < 0 or > uint.MaxValue)
            {
                throw new StructException($"'I' format requires 0 <= number <= 4294967295");
            }
            o.U32((uint)x, true);
        }
        return o.ToArray();
    }
}
