using DWSSK.Formats.Core;
using static DWSSK.Formats.Shaders.Bits;
namespace DWSSK.Formats.Shaders;
internal static class Rsx
{
    public const string Xyzw = "xyzw";
    private static readonly Dictionary<long, string> s_vpVec = new()
    {
        [0] = "nop", [1] = "mov", [2] = "mul", [3] = "add", [4] = "mad", [5] = "dp3", [6] = "dph", [7] = "dp4", [8] = "dst",
        [9] = "min", [10] = "max", [11] = "slt", [12] = "sge", [13] = "arl", [14] = "frc", [15] = "flr", [16] = "seq",
        [17] = "sfl", [18] = "sgt", [19] = "sle", [20] = "sne", [21] = "str", [22] = "ssg", [25] = "txl",
    };
    private static readonly Dictionary<long, string> s_vpSca = new()
    {
        [0] = "nop", [1] = "mov", [2] = "rcp", [3] = "rcc", [4] = "rsq", [5] = "exp", [6] = "log", [7] = "lit", [8] = "bra",
        [9] = "bri", [10] = "cal", [11] = "cli", [12] = "ret", [13] = "lg2", [14] = "ex2", [15] = "sin", [16] = "cos",
        [17] = "brb", [18] = "clb", [19] = "psh", [20] = "pop",
    };
    private static readonly Dictionary<string, int[]> s_vpSrcs = new()
    {
        ["mov"] = [0], ["mul"] = [0, 1], ["add"] = [0, 2], ["mad"] = [0, 1, 2], ["dp3"] = [0, 1], ["dph"] = [0, 1],
        ["dp4"] = [0, 1], ["dst"] = [0, 1], ["min"] = [0, 1], ["max"] = [0, 1], ["slt"] = [0, 1], ["sge"] = [0, 1],
        ["arl"] = [0], ["frc"] = [0], ["flr"] = [0], ["seq"] = [0, 1], ["sfl"] = [0, 1], ["sgt"] = [0, 1], ["sle"] = [0, 1],
        ["sne"] = [0, 1], ["str"] = [0, 1], ["ssg"] = [0], ["txl"] = [0],
    };
    private static readonly string[] s_vpIn =
        ["pos", "weight", "normal", "color0", "color1", "fog", "psize", "attr7", "tc0", "tc1", "tc2", "tc3", "tc4", "tc5", "tc6", "tc7"];
    private static readonly string[] s_vpOut =
        ["HPOS", "COL0", "COL1", "BFC0", "BFC1", "FOGC", "PSZ", "TEX0", "TEX1", "TEX2", "TEX3", "TEX4", "TEX5", "TEX6", "TEX7", "CLIP"];
    private static readonly Dictionary<long, string> s_conds = new()
    {
        [1] = "lt", [2] = "eq", [3] = "le", [4] = "gt", [5] = "ne", [6] = "ge", [7] = "tr", [0] = "fl",
    };
    private static readonly Dictionary<string, long> s_vpVecN = s_vpVec.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly Dictionary<string, long> s_vpScaN = s_vpSca.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly Dictionary<string, long> s_condsN = s_conds.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly string[] s_branch = ["bra", "bri", "cal", "cli", "brb", "clb"];
    private static readonly string[] s_branchRet = ["bra", "bri", "cal", "cli", "brb", "clb", "ret"];
    public static string Swz4(long x, long y, long z, long w)
    {
        string s = $"{Xyzw[(int)x]}{Xyzw[(int)y]}{Xyzw[(int)z]}{Xyzw[(int)w]}";
        return s == "xyzw" ? string.Empty : "." + s;
    }
    private static string Out(long n)
    {
        return n < s_vpOut.Length ? s_vpOut[n] : D(n);
    }
    public static string VpText(long d0, long d1, long d2, long d3)
    {
        long[] srcs = [B(d2, 23, 9) | (B(d1, 0, 8) << 9), B(d2, 6, 17), B(d3, 21, 11) | (B(d2, 0, 6) << 11)];
        long[] absf = [B(d0, 21, 1), B(d0, 22, 1), B(d0, 23, 1)];
        string Src(int n)
        {
            long v = srcs[n];
            long typ = B(v, 0, 2);
            long tmp = B(v, 2, 6);
            string r;
            if (typ == 1)
            {
                r = $"r{D(tmp)}";
            }
            else if (typ == 2)
            {
                r = $"v[{s_vpIn[B(d1, 8, 4)]}]";
            }
            else if (typ == 3)
            {
                long c = B(d1, 12, 10);
                r = B(d3, 1, 1) != 0 ? $"c[a{D(B(d0, 24, 1))}.{Xyzw[(int)B(d0, 0, 2)]} + {D(c)}]" : $"c[{D(c)}]";
            }
            else
            {
                r = "?";
            }
            r += Swz4(B(v, 14, 2), B(v, 12, 2), B(v, 10, 2), B(v, 8, 2));
            if (absf[n] != 0)
            {
                r = $"|{r}|";
            }
            return (B(v, 16, 1) != 0 ? "-" : string.Empty) + r;
        }
        string cond = string.Empty;
        if (B(d0, 13, 1) != 0 && B(d0, 10, 3) != 7)
        {
            cond = $" ({s_conds[B(d0, 10, 3)]} cc{D(B(d0, 25, 1))}{Swz4(B(d0, 8, 2), B(d0, 6, 2), B(d0, 4, 2), B(d0, 2, 2))})";
        }
        string sat = B(d0, 26, 1) != 0 ? "_sat" : string.Empty;
        string cc = B(d0, 14, 1) != 0 || B(d0, 29, 1) != 0 ? "c" : string.Empty;
        long dstOut = B(d3, 2, 5);
        long vecResult = B(d0, 30, 1);
        List<string> parts = [];
        long vopN = B(d1, 22, 5);
        string vop = s_vpVec.TryGetValue(vopN, out string? vn) ? vn : $"vec{D(vopN)}";
        string vmask = string.Concat(Enumerable.Range(0, 4).Where(k => B(d3, 16 - k, 1) != 0).Select(k => Xyzw[k]));
        if (vop != "nop")
        {
            List<string> dsts = [];
            if (vecResult != 0 && dstOut != 0x1F)
            {
                dsts.Add($"o[{Out(dstOut)}]");
            }
            if (B(d0, 15, 6) != 0x3F)
            {
                dsts.Add((vop == "arl" ? "a" : "r") + D(B(d0, 15, 6)));
            }
            if (dsts.Count == 0)
            {
                dsts.Add($"cc{D(B(d0, 25, 1))}");
            }
            List<string> ops = [.. (s_vpSrcs.TryGetValue(vop, out int[]? ss) ? ss : []).Select(Src)];
            if (vop == "txl")
            {
                ops.Add($"tex{D(B(d2, 8, 2))}");
            }
            parts.Add($"{vop}{cc}{sat} " + string.Join(", ", dsts.Select(d => $"{d}.{vmask}")) + cond + (ops.Count == 0 ? string.Empty : ", " + string.Join(", ", ops)));
        }
        long sopN = B(d1, 27, 5);
        string sop = s_vpSca.TryGetValue(sopN, out string? sn) ? sn : $"sca{D(sopN)}";
        if (s_branch.Contains(sop))
        {
            long target = (B(d0, 23, 1) << 9) | (B(d2, 0, 6) << 3) | B(d3, 29, 3);
            string extra = sop is "brb" or "clb" ? $" (bit {D(B(d3, 24, 5))} {(B(d3, 28, 1) != 0 ? "set" : "clear")})" : string.Empty;
            parts.Add($"{sop}{cond}{extra} {Hex(target, 4)}");
        }
        else if (sop == "ret")
        {
            parts.Add("ret" + cond);
        }
        else if (sop != "nop")
        {
            string smask = string.Concat(Enumerable.Range(0, 4).Where(k => B(d3, 20 - k, 1) != 0).Select(k => Xyzw[k]));
            List<string> dsts = [];
            if (vecResult == 0 && dstOut != 0x1F)
            {
                dsts.Add($"o[{Out(dstOut)}]");
            }
            if (B(d3, 7, 6) != 0x3F)
            {
                dsts.Add($"r{D(B(d3, 7, 6))}");
            }
            if (dsts.Count == 0)
            {
                dsts.Add($"cc{D(B(d0, 25, 1))}");
            }
            parts.Add($"{sop}{cc}{sat} " + string.Join(", ", dsts.Select(d => $"{d}.{smask}")) + cond + ", " + Src(2));
        }
        string text = parts.Count > 0 ? string.Join(" ; ", parts) : "nop";
        return text + (B(d3, 0, 1) != 0 ? " ; end" : string.Empty);
    }
    public static List<string> VpDisasm(ReadOnlySpan<byte> ucode)
    {
        List<string> output = [];
        for (int pc = 0; pc < ucode.Length / 16; pc++)
        {
            long[] w = [Bin.U32(ucode, pc * 16, true), Bin.U32(ucode, (pc * 16) + 4, true), Bin.U32(ucode, (pc * 16) + 8, true), Bin.U32(ucode, (pc * 16) + 12, true)];
            output.Add($"{Hex(pc, 4)}: {string.Join(' ', w.Select(x => Hex(x, 8)))}  {VpText(w[0], w[1], w[2], w[3])}");
            if ((w[3] & 1) != 0)
            {
                break;
            }
        }
        return output;
    }
    private static bool[] MaskBits(string mask)
    {
        return [.. Xyzw.Select(c => mask.Contains(c, StringComparison.Ordinal))];
    }
    private static (long Op, long Cc, bool Sat) OpName(string name, Dictionary<string, long> table)
    {
        bool sat = name.EndsWith("_sat", StringComparison.Ordinal);
        name = sat ? name[..^4] : name;
        foreach ((string nm, long cc) in new[] { (name, 0L), (name.Length > 0 ? name[..^1] : name, 1L) })
        {
            if (table.TryGetValue(nm, out long op))
            {
                return (op, cc, sat);
            }
        }
        System.Text.RegularExpressions.Match m = Rx.Full(@"(?:vec|sca|op)(\d+)(c?)", name)
            ?? throw new InvalidDataException($"'{name}' is not an instruction");
        return (Rx.Int(m.Groups[1].Value), m.Groups[2].Value.Length > 0 ? 1 : 0, sat);
    }
    public static int[] SwzSel(string text)
    {
        string s = text.Length > 0 ? text[1..] : Xyzw;
        return [.. s.Select(c => Xyzw.IndexOf(c, StringComparison.Ordinal) is int i and >= 0 ? i : throw new InvalidDataException($"'{c}' is not a swizzle component"))];
    }
    private static List<string> PySlice(List<string> items, int stop)
    {
        int n = items.Count;
        int end = stop < 0 ? Math.Max(0, n + stop) : Math.Min(stop, n);
        return items.GetRange(0, end);
    }
    private static List<string> PySliceFrom(List<string> items, int start)
    {
        int n = items.Count;
        int s = start < 0 ? Math.Max(0, n + start) : Math.Min(start, n);
        return items.GetRange(s, n - s);
    }
    public static (long, long, long, long) VpParse(string text, long d0, long d1, long d2, long d3)
    {
        bool end = text.EndsWith(" ; end", StringComparison.Ordinal);
        text = end ? text[..^6] : text;
        d3 = Put(d3, 0, 1, end);
        List<string?> parts = text == "nop" ? [] : [.. text.Split(" ; ")];
        if (parts.Count == 1)
        {
            string head = parts[0]!.Split(' ')[0].Replace("_sat", string.Empty, StringComparison.Ordinal);
            if (!s_vpVecN.ContainsKey(head) && !s_vpVecN.ContainsKey(head.Length > 0 ? head[..^1] : head))
            {
                parts.Insert(0, null);
            }
        }
        parts.Add(null);
        parts.Add(null);
        string? vec = parts[0];
        string? sca = parts[1];
        long[] srcv = [B(d2, 23, 9) | (B(d1, 0, 8) << 9), B(d2, 6, 17), B(d3, 21, 11) | (B(d2, 0, 6) << 11)];
        long? outReg = null;
        string? cond = null;
        string SplitCond(string item)
        {
            int i = item.IndexOf(" (", StringComparison.Ordinal);
            if (i >= 0)
            {
                cond = item[(i + 2)..];
                return item[..i];
            }
            return item;
        }
        void Src(int n, string t)
        {
            long v = srcv[n];
            bool neg = t.StartsWith('-');
            t = neg ? t[1..] : t;
            bool ab = t.StartsWith('|');
            t = ab ? t[1..^1] : t;
            System.Text.RegularExpressions.Match m = Rx.Full(@"(.*?)((?:\.[xyzw]{4})?)", t)!;
            string reg = m.Groups[1].Value;
            int[] sw = SwzSel(m.Groups[2].Value);
            if (reg.Length == 0)
            {
                throw new PyIndexException("string index out of range");
            }
            if (reg[0] == 'r')
            {
                v = Put(Put(v, 0, 2, 1), 2, 6, Rx.Int(reg[1..]));
            }
            else if (reg[0] == 'v')
            {
                v = Put(v, 0, 2, 2);
                int idx = Array.IndexOf(s_vpIn, reg[2..^1]);
                d1 = Put(d1, 8, 4, idx >= 0 ? idx : throw new InvalidDataException($"'{reg[2..^1]}' is not in list"));
            }
            else if (reg[0] == 'c')
            {
                v = Put(v, 0, 2, 3);
                System.Text.RegularExpressions.Match? rel = Rx.Full(@"c\[a(\d)\.([xyzw]) \+ (\d+)\]", reg);
                d3 = Put(d3, 1, 1, rel is not null);
                if (rel is not null)
                {
                    d0 = Put(Put(d0, 24, 1, Rx.Int(rel.Groups[1].Value)), 0, 2, Xyzw.IndexOf(rel.Groups[2].Value[0], StringComparison.Ordinal));
                }
                d1 = Put(d1, 12, 10, Rx.Int(rel is not null ? rel.Groups[3].Value : reg[2..^1]));
            }
            else
            {
                v = Put(v, 0, 2, 0);
            }
            for (int k = 0; k < sw.Length; k++)
            {
                v = Put(v, 14 - (2 * k), 2, sw[k]);
            }
            srcv[n] = Put(v, 16, 1, neg);
            d0 = Put(d0, 21 + n, 1, ab);
        }
        (long Tmp, bool[] Mask) Dsts(List<string> items)
        {
            long tmp = 0x3F;
            string mask = string.Empty;
            foreach (string it in items)
            {
                string item = SplitCond(it);
                int dot = item.LastIndexOf('.');
                if (dot < 0)
                {
                    throw new InvalidDataException("not enough values to unpack (expected 2, got 1)");
                }
                string reg = item[..dot];
                mask = item[(dot + 1)..];
                if (reg.StartsWith("o[", StringComparison.Ordinal))
                {
                    string inner = reg[2..^1];
                    int idx = Array.IndexOf(s_vpOut, inner);
                    outReg = idx >= 0 ? idx : Rx.Int(inner);
                }
                else if (reg.StartsWith("cc", StringComparison.Ordinal))
                {
                    d0 = Put(d0, 25, 1, Rx.Int(reg[2..]));
                }
                else
                {
                    tmp = Rx.Int(reg[1..]);
                }
            }
            return (tmp, MaskBits(mask));
        }
        long vop = 0;
        long sop = 0;
        (long Cc, bool Sat)? flags = null;
        if (!string.IsNullOrEmpty(vec))
        {
            int sp = vec.IndexOf(' ', StringComparison.Ordinal);
            string name = sp < 0 ? vec : vec[..sp];
            string rest = sp < 0 ? string.Empty : vec[(sp + 1)..];
            (vop, long cc, bool sat) = OpName(name, s_vpVecN);
            flags = (cc, sat);
            List<string> items = [.. rest.Split(", ")];
            int[] srcIdx = s_vpVec.TryGetValue(vop, out string? vname) && s_vpSrcs.TryGetValue(vname, out int[]? si) ? si : [];
            int ns = srcIdx.Length + (vop == 25 ? 1 : 0);
            (long tmp, bool[] mask) = Dsts(PySlice(items, items.Count - ns));
            d0 = Put(d0, 15, 6, tmp);
            for (int k = 0; k < 4; k++)
            {
                d3 = Put(d3, 16 - k, 1, mask[k]);
            }
            if (outReg is not null)
            {
                d0 = Put(d0, 30, 1, 1);
            }
            List<string> srcTexts = PySliceFrom(items, items.Count - ns);
            for (int i = 0; i < Math.Min(srcIdx.Length, srcTexts.Count); i++)
            {
                Src(srcIdx[i], srcTexts[i]);
            }
            if (vop == 25)
            {
                d2 = Put(d2, 8, 2, Rx.Int(items[^1][3..]));
            }
        }
        else
        {
            d0 = Put(d0, 15, 6, 0x3F);
        }
        if (!string.IsNullOrEmpty(sca))
        {
            int sp = sca.IndexOf(' ', StringComparison.Ordinal);
            string name = sp < 0 ? sca : sca[..sp];
            string rest = sp < 0 ? string.Empty : sca[(sp + 1)..];
            (sop, long cc, bool sat) = OpName(name, s_vpScaN);
            string? sopName = s_vpSca.TryGetValue(sop, out string? sname) ? sname : null;
            if (sopName is not null && s_branchRet.Contains(sopName))
            {
                int pi = sca.IndexOf(" (", StringComparison.Ordinal);
                if (pi >= 0)
                {
                    string after = sca[(pi + 2)..];
                    int close = after.IndexOf(')', StringComparison.Ordinal);
                    cond = close >= 0 ? after[..close] : after;
                }
                System.Text.RegularExpressions.Match? bit = Rx.Search(@"\(bit (\d+) (set|clear)\)", sca);
                if (bit is not null)
                {
                    d3 = Put(Put(d3, 24, 5, Rx.Int(bit.Groups[1].Value)), 28, 1, bit.Groups[2].Value == "set");
                }
                if (sopName != "ret")
                {
                    long t = Rx.Int(sca[(sca.LastIndexOf(' ') + 1)..], 16);
                    d0 = Put(d0, 23, 1, t >> 9);
                    d2 = Put(d2, 0, 6, t >> 3);
                    d3 = Put(d3, 29, 3, t);
                }
            }
            else
            {
                flags ??= (cc, sat);
                List<string> items = [.. rest.Split(", ")];
                long? had = outReg;
                (long tmp, bool[] mask) = Dsts(PySlice(items, -1));
                d3 = Put(d3, 7, 6, tmp);
                for (int k = 0; k < 4; k++)
                {
                    d3 = Put(d3, 20 - k, 1, mask[k]);
                }
                if (outReg is not null && outReg != had)
                {
                    d0 = Put(d0, 30, 1, 0);
                }
                Src(2, items[^1]);
            }
        }
        else
        {
            d3 = Put(d3, 7, 6, 0x3F);
        }
        if (flags is { } f)
        {
            d0 = Put(Put(Put(d0, 14, 1, f.Cc), 29, 1, f.Cc), 26, 1, f.Sat);
        }
        d1 = Put(Put(d1, 22, 5, vop), 27, 5, sop);
        d3 = Put(d3, 2, 5, outReg ?? 0x1F);
        if (!string.IsNullOrEmpty(cond))
        {
            System.Text.RegularExpressions.Match m = Rx.Full(@"(\w+) cc(\d)((?:\.[xyzw]{4})?)\)?", cond)
                ?? throw new InvalidDataException($"'{cond}' is not a condition");
            d0 = Put(Put(Put(d0, 13, 1, 1), 10, 3, s_condsN[m.Groups[1].Value]), 25, 1, Rx.Int(m.Groups[2].Value));
            int[] sw = SwzSel(m.Groups[3].Value);
            for (int k = 0; k < sw.Length; k++)
            {
                d0 = Put(d0, 8 - (2 * k), 2, sw[k]);
            }
        }
        else if (B(d0, 13, 1) != 0)
        {
            d0 = Put(d0, 10, 3, 7);
        }
        d1 = Put(d1, 0, 8, srcv[0] >> 9);
        string? sopFinal = s_vpSca.TryGetValue(sop, out string? sf) ? sf : null;
        bool branch = !string.IsNullOrEmpty(sca) && sopFinal is not null && s_branch.Contains(sopFinal);
        bool branchRet = !string.IsNullOrEmpty(sca) && sopFinal is not null && s_branchRet.Contains(sopFinal);
        d2 = !branch
            ? Put(Put(Put(d2, 23, 9, srcv[0]), 6, 17, srcv[1]), 0, 6, srcv[2] >> 11)
            : Put(Put(d2, 23, 9, srcv[0]), 6, 17, srcv[1]);
        d3 = !branchRet ? Put(d3, 21, 11, srcv[2]) : d3;
        return (d0, d1, d2, d3);
    }
    public static byte[] VpAsm(IReadOnlyList<string> lines)
    {
        ByteWriter output = new();
        foreach (string line in lines)
        {
            System.Text.RegularExpressions.Match m = Rx.Full(@"[0-9a-f]+: (?:((?:[0-9a-f]{8} ?){4}) )?(.*)", line)
                ?? throw new InvalidDataException($"'{line}' is not a disassembly line");
            long[] w = m.Groups[1].Success
                ? [.. m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Rx.Int(x, 16))]
                : [0, 0, 0, 0];
            string text = m.Groups[2].Value;
            (long a, long b, long c, long d) = VpText(w[0], w[1], w[2], w[3]) == text ? (w[0], w[1], w[2], w[3]) : VpParse(text, w[0], w[1], w[2], w[3]);
            foreach (long x in new[] { a, b, c, d })
            {
                output.U32((uint)x, true);
            }
        }
        return output.ToArray();
    }
    public static readonly string[] FpOps =
    [
        "nop", "mov", "mul", "add", "mad", "dp3", "dp4", "dst", "min", "max", "slt", "sge", "sle", "sgt", "sne",
        "seq", "frc", "flr", "kil", "pk4", "up4", "ddx", "ddy", "tex", "txp", "txd", "rcp", "rsq", "ex2", "lg2",
        "lit", "lrp", "str", "sfl", "cos", "sin", "pk2", "up2", "pow", "pkb", "upb", "pk16", "up16", "bem", "pkg",
        "upg", "dp2a", "txl", "op48", "txb", "op50", "texbem", "txpbem", "bemlum", "refl", "timeswtex", "dp2",
        "nrm", "div", "divsq", "lif", "fenct", "fencb", "op63", "brk", "cal", "ife", "loop", "rep", "ret",
    ];
    private static readonly Dictionary<string, int> s_fpSrcs = new()
    {
        ["mov"] = 1, ["mul"] = 2, ["add"] = 2, ["mad"] = 3, ["dp3"] = 2, ["dp4"] = 2, ["dst"] = 2, ["min"] = 2, ["max"] = 2,
        ["slt"] = 2, ["sge"] = 2, ["sle"] = 2, ["sgt"] = 2, ["sne"] = 2, ["seq"] = 2, ["frc"] = 1, ["flr"] = 1, ["pk4"] = 1,
        ["up4"] = 1, ["ddx"] = 1, ["ddy"] = 1, ["tex"] = 1, ["txp"] = 1, ["txd"] = 3, ["rcp"] = 1, ["rsq"] = 1, ["ex2"] = 1,
        ["lg2"] = 1, ["lit"] = 1, ["lrp"] = 3, ["str"] = 2, ["sfl"] = 2, ["cos"] = 1, ["sin"] = 1, ["pk2"] = 1, ["up2"] = 1,
        ["pow"] = 2, ["pkb"] = 1, ["upb"] = 1, ["pk16"] = 1, ["up16"] = 1, ["bem"] = 3, ["pkg"] = 1, ["upg"] = 1, ["dp2a"] = 3,
        ["txl"] = 1, ["txb"] = 1, ["texbem"] = 3, ["txpbem"] = 3, ["refl"] = 2, ["dp2"] = 2, ["nrm"] = 1, ["div"] = 2,
        ["divsq"] = 2, ["lif"] = 1,
    };
    private static readonly string[] s_fpIn = ["WPOS", "COL0", "COL1", "FOGC", "TEX0", "TEX1", "TEX2", "TEX3", "TEX4", "TEX5", "TEX6", "TEX7", "TEX8", "TEX9", "SSA"];
    private static readonly Dictionary<long, string> s_fpScale = new() { [1] = "_x2", [2] = "_x4", [3] = "_x8", [5] = "_d2", [6] = "_d4", [7] = "_d8" };
    private static readonly string[] s_fpPrec = ["r", "h", "x", "b"];
    private static readonly string[] s_fpControl = ["ife", "loop", "rep", "brk", "cal", "ret", "fenct", "fencb", "nop", "kil"];
    private static readonly string[] s_fpTex = ["tex", "txp", "txd", "txl", "txb", "texbem", "txpbem"];
    private static readonly string[] s_fpOpsByLen = [.. FpOps.Select((o, i) => (o, i)).OrderByDescending(t => t.o.Length).ThenBy(t => t.i).Select(t => t.o)];
    private static readonly Dictionary<(long, long, long), string> s_condNames = new()
    {
        [(1, 0, 0)] = "lt", [(0, 1, 0)] = "eq", [(1, 1, 0)] = "le", [(0, 0, 1)] = "gt", [(1, 0, 1)] = "ne",
        [(0, 1, 1)] = "ge", [(0, 0, 0)] = "fl",
    };
    private static readonly Dictionary<string, (long, long, long)> s_fpConds = new()
    {
        ["lt"] = (1, 0, 0), ["eq"] = (0, 1, 0), ["le"] = (1, 1, 0), ["gt"] = (0, 0, 1), ["ne"] = (1, 0, 1),
        ["ge"] = (0, 1, 1), ["fl"] = (0, 0, 0),
    };
    public static long FpWord(ReadOnlySpan<byte> b, int o)
    {
        long w = Bin.U32(b, o, true);
        return ((w << 16) | (w >> 16)) & 0xFFFFFFFF;
    }
    public static long Swap(long w)
    {
        return ((w << 16) | (w >> 16)) & 0xFFFFFFFF;
    }
    public static string F32(double x)
    {
        uint want = BitConverter.SingleToUInt32Bits((float)x);
        for (int p = 6; p < 10; p++)
        {
            string t = Py.FormatG(x, p);
            if (BitConverter.SingleToUInt32Bits(Py.ToSingle(Py.ParseFloat(t))) == want)
            {
                return t;
            }
        }
        return Py.Repr(x);
    }
    private static string FpOp(long opN)
    {
        return opN < FpOps.Length ? FpOps[opN] : $"op{D(opN)}";
    }
    public static string FpText(long dst, long s0, long s1, long s2, double[]? cnst, string? uniform)
    {
        long opN = B(dst, 24, 6) | (B(s1, 31, 1) << 6);
        string op = FpOp(opN);
        long[] srcw = [s0, s1, s2];
        string Src(int n)
        {
            long w = srcw[n];
            long typ = B(w, 0, 2);
            long idx = B(w, 2, 6);
            string r;
            if (typ == 0)
            {
                r = (B(w, 8, 1) != 0 ? "h" : "r") + D(idx);
            }
            else if (typ == 1)
            {
                long i = B(dst, 13, 4);
                r = $"f[{(i < s_fpIn.Length ? s_fpIn[i] : D(i))}]";
            }
            else if (typ == 2)
            {
                r = !string.IsNullOrEmpty(uniform) ? uniform : cnst is { Length: > 0 } ? "{" + string.Join(", ", cnst.Select(F32)) + "}" : "c";
            }
            else
            {
                r = "?";
            }
            r += Swz4(B(w, 9, 2), B(w, 11, 2), B(w, 13, 2), B(w, 15, 2));
            if (B(w, n == 0 ? 29 : 18, 1) != 0)
            {
                r = $"|{r}|";
            }
            return (B(w, 17, 1) != 0 ? "-" : string.Empty) + r;
        }
        long lt = B(s0, 18, 1);
        long eq = B(s0, 19, 1);
        long gt = B(s0, 20, 1);
        string cond = string.Empty;
        if (!(lt != 0 && eq != 0 && gt != 0))
        {
            cond = $" ({s_condNames[(lt, eq, gt)]} cc{D(B(s0, 31, 1))}{Swz4(B(s0, 21, 2), B(s0, 23, 2), B(s0, 25, 2), B(s0, 27, 2))})";
        }
        string text;
        if (s_fpControl.Contains(op))
        {
            text = op == "ife"
                ? $"ife{cond} else {Hex(B(s1, 0, 31))}, endif {Hex(s2)}"
                : op is "loop" or "rep"
                ? $"{op}{cond} count {D(B(s1, 2, 8))}, init {D(B(s1, 10, 8))}, step {D(B(s1, 19, 8))}, end {Hex(s2)}"
                : op + cond;
        }
        else
        {
            string mods = (B(dst, 22, 2) != 0 ? s_fpPrec[B(dst, 22, 2)] : string.Empty) + (B(dst, 8, 1) != 0 ? "c" : string.Empty)
                + (s_fpScale.TryGetValue(B(s1, 28, 3), out string? scale) ? scale : string.Empty)
                + (B(dst, 31, 1) != 0 ? "_sat" : string.Empty) + (B(dst, 21, 1) != 0 ? "_bx2" : string.Empty);
            string mask = string.Concat(Enumerable.Range(0, 4).Where(k => B(dst, 9 + k, 1) != 0).Select(k => Xyzw[k]));
            string d = B(dst, 30, 1) != 0 ? $"cc{D(B(s0, 30, 1))}" : (B(dst, 7, 1) != 0 ? "h" : "r") + D(B(dst, 1, 6));
            List<string> ops = [.. Enumerable.Range(0, s_fpSrcs.GetValueOrDefault(op, 0)).Select(Src)];
            if (s_fpTex.Contains(op))
            {
                ops.Add($"tex{D(B(dst, 17, 4))}");
            }
            text = $"{op}{mods} {d}{(mask != "xyzw" ? "." + mask : string.Empty)}{cond}" + (ops.Count == 0 ? string.Empty : ", " + string.Join(", ", ops));
        }
        return text + (B(dst, 0, 1) != 0 ? " ; end" : string.Empty);
    }
    public static bool FpUsesConst(long dst, long s0, long s1, long s2)
    {
        long opN = B(dst, 24, 6) | (B(s1, 31, 1) << 6);
        string op = opN < FpOps.Length ? FpOps[opN] : string.Empty;
        long[] w = [s0, s1, s2];
        int n = Math.Min(3, s_fpSrcs.GetValueOrDefault(op, 0));
        for (int i = 0; i < n; i++)
        {
            if (B(w[i], 0, 2) == 2)
            {
                return true;
            }
        }
        return false;
    }
    public static ((long, long, long, long) Words, long[]? Const, string? Uniform) FpParse(string text, long dst, long s0, long s1, long s2, long[]? cnst)
    {
        string? uniform = null;
        bool end = text.EndsWith(" ; end", StringComparison.Ordinal);
        text = end ? text[..^6] : text;
        dst = Put(dst, 0, 1, end);
        int sp = text.IndexOf(' ', StringComparison.Ordinal);
        string head = sp < 0 ? text : text[..sp];
        string rest = sp < 0 ? string.Empty : text[(sp + 1)..];
        string? cond = null;
        if (rest.StartsWith('('))
        {
            string inner = rest[1..];
            int close = inner.IndexOf(')', StringComparison.Ordinal);
            cond = close < 0 ? inner : inner[..close];
            rest = close < 0 ? string.Empty : inner[(close + 1)..];
            rest = rest.TrimStart(',', ' ').TrimStart();
        }
        string op = head;
        System.Text.RegularExpressions.Match? m = null;
        foreach (string candidate in s_fpOpsByLen)
        {
            if (head.StartsWith(candidate, StringComparison.Ordinal) && Rx.Full(FpMods, head[candidate.Length..]) is { } mm)
            {
                op = candidate;
                m = mm;
                break;
            }
        }
        if (m is null)
        {
            op = head;
            m = Rx.Full(FpMods, string.Empty)!;
        }
        int known = Array.IndexOf(FpOps, op);
        long opN = known >= 0 ? known : Rx.Int(op[2..]);
        dst = Put(dst, 24, 6, opN);
        s1 = Put(s1, 31, 1, opN >> 6);
        long[] srcw = [s0, s1, s2];
        if (s_fpControl.Contains(op))
        {
            if (op == "ife")
            {
                System.Text.RegularExpressions.Match g = Rx.Full(@"else (\w+), endif (\w+)", rest) ?? throw new InvalidDataException($"'{rest}' is not an ife operand list");
                srcw[1] = Put(srcw[1], 0, 31, Rx.Int(g.Groups[1].Value, 16));
                srcw[2] = Rx.Int(g.Groups[2].Value, 16);
            }
            else if (op is "loop" or "rep")
            {
                System.Text.RegularExpressions.Match g = Rx.Full(@"count (\d+), init (\d+), step (\d+), end (\w+)", rest) ?? throw new InvalidDataException($"'{rest}' is not a loop operand list");
                srcw[1] = Put(Put(Put(srcw[1], 2, 8, Rx.Int(g.Groups[1].Value)), 10, 8, Rx.Int(g.Groups[2].Value)), 19, 8, Rx.Int(g.Groups[3].Value));
                srcw[2] = Rx.Int(g.Groups[4].Value, 16);
            }
        }
        else
        {
            string prec = m.Groups[1].Value;
            bool cc = m.Groups[2].Value.Length > 0;
            string? scale = m.G(3);
            bool sat = m.G(4) is not null;
            bool bx2 = m.G(5) is not null;
            dst = Put(Put(Put(dst, 22, 2, prec.Length > 0 ? " hxb".IndexOf(prec[0], StringComparison.Ordinal) : 0), 8, 1, cc), 31, 1, sat);
            dst = Put(dst, 21, 1, bx2);
            long scaleN = s_fpScale.FirstOrDefault(kv => kv.Value == scale).Key;
            srcw[1] = Put(srcw[1], 28, 3, scale is null ? 0 : scaleN);
            string[] items = Rx.Plain(@", (?![^{]*\})").Split(rest);
            string d = items[0];
            if (d.Contains(" (", StringComparison.Ordinal))
            {
                string[] two = d.Split(" (");
                if (two.Length != 2)
                {
                    throw new InvalidDataException("too many values to unpack (expected 2)");
                }
                d = two[0];
                cond = two[1][..^1];
            }
            int dot = d.IndexOf('.', StringComparison.Ordinal);
            string reg = dot < 0 ? d : d[..dot];
            string mask = dot < 0 ? string.Empty : d[(dot + 1)..];
            if (reg.StartsWith("cc", StringComparison.Ordinal))
            {
                dst = Put(dst, 30, 1, 1);
                srcw[0] = Put(srcw[0], 30, 1, Rx.Int(reg[2..]));
            }
            else
            {
                dst = Put(Put(Put(dst, 30, 1, 0), 7, 1, reg[0] == 'h'), 1, 6, Rx.Int(reg[1..]));
            }
            bool[] bits = MaskBits(d.Contains('.', StringComparison.Ordinal) ? mask : Xyzw);
            for (int k = 0; k < 4; k++)
            {
                dst = Put(dst, 9 + k, 1, bits[k]);
            }
            int count = s_fpSrcs.GetValueOrDefault(op, 0);
            string[] srcTexts = items.Skip(1).Take(count).ToArray();
            for (int n = 0; n < srcTexts.Length; n++)
            {
                string t = srcTexts[n];
                long v = srcw[n];
                bool neg = t.StartsWith('-');
                t = neg ? t[1..] : t;
                bool ab = t.StartsWith('|');
                t = ab ? t[1..^1] : t;
                string sw = string.Empty;
                System.Text.RegularExpressions.Match? mm = Rx.Full(@"(.*?)(\.[xyzw]{4})", t);
                if (mm is not null && !t.EndsWith('}'))
                {
                    t = mm.Groups[1].Value;
                    sw = mm.Groups[2].Value;
                }
                else if (t.StartsWith('{') && t.Contains("}.", StringComparison.Ordinal))
                {
                    int ld = t.LastIndexOf('.');
                    sw = "." + t[(ld + 1)..];
                    t = t[..ld];
                }
                if (t.Length == 0)
                {
                    throw new PyIndexException("string index out of range");
                }
                if ((t[0] == 'r' || t[0] == 'h') && Rx.IsDigits(t[1..]))
                {
                    v = Put(Put(Put(v, 0, 2, 0), 2, 6, Rx.Int(t[1..])), 8, 1, t[0] == 'h');
                }
                else if (t.StartsWith("f[", StringComparison.Ordinal))
                {
                    v = Put(v, 0, 2, 1);
                    string inner = t[2..^1];
                    int fi = Array.IndexOf(s_fpIn, inner);
                    dst = Put(dst, 13, 4, fi >= 0 ? fi : Rx.Int(inner));
                }
                else if (t == "?")
                {
                    v = Put(v, 0, 2, 3);
                }
                else
                {
                    v = Put(v, 0, 2, 2);
                    uniform = t.StartsWith('{') || t == "c" ? null : t;
                    if (t.StartsWith('{'))
                    {
                        cnst = [.. t[1..^1].Split(", ").Select(x => (long)BitConverter.SingleToUInt32Bits(Py.ToSingle(Py.ParseFloat(x))))];
                    }
                    cnst = cnst is { Length: > 0 } ? cnst : [0, 0, 0, 0];
                }
                int[] sel = SwzSel(sw);
                for (int k = 0; k < sel.Length; k++)
                {
                    v = Put(v, 9 + (2 * k), 2, sel[k]);
                }
                srcw[n] = Put(Put(v, 17, 1, neg), n == 0 ? 29 : 18, 1, ab);
            }
            if (s_fpTex.Contains(op))
            {
                dst = Put(dst, 17, 4, Rx.Int(items[^1][3..]));
            }
        }
        (long clt, long ceq, long cgt) = !string.IsNullOrEmpty(cond) ? s_fpConds[cond.Split(' ')[0]] : (1, 1, 1);
        s0 = Put(Put(Put(srcw[0], 18, 1, clt), 19, 1, ceq), 20, 1, cgt);
        if (!string.IsNullOrEmpty(cond))
        {
            System.Text.RegularExpressions.Match cm = Rx.Full(@"\w+ cc(\d)((?:\.[xyzw]{4})?)", cond) ?? throw new InvalidDataException($"'{cond}' is not a condition");
            s0 = Put(s0, 31, 1, Rx.Int(cm.Groups[1].Value));
            int[] sel = SwzSel(cm.Groups[2].Value);
            for (int k = 0; k < sel.Length; k++)
            {
                s0 = Put(s0, 21 + (2 * k), 2, sel[k]);
            }
        }
        (long, long, long, long) words = (dst, s0, srcw[1], srcw[2]);
        return (words, FpUsesConst(dst, s0, srcw[1], srcw[2]) ? cnst : null, uniform);
    }
    private const string FpMods = @"([hxb]?)(c?)(_x2|_x4|_x8|_d2|_d4|_d8)?(_sat)?(_bx2)?";
    public static List<string> FpDisasm(ReadOnlySpan<byte> ucode, IReadOnlyDictionary<long, string?>? uniforms)
    {
        List<string> output = [];
        int o = 0;
        while (o + 16 <= ucode.Length)
        {
            long[] w = [FpWord(ucode, o), FpWord(ucode, o + 4), FpWord(ucode, o + 8), FpWord(ucode, o + 12)];
            int n = FpUsesConst(w[0], w[1], w[2], w[3]) && o + 32 <= ucode.Length ? 32 : 16;
            double[]? cnst = n == 32
                ? [Single(FpWord(ucode, o + 16)), Single(FpWord(ucode, o + 20)), Single(FpWord(ucode, o + 24)), Single(FpWord(ucode, o + 28))]
                : null;
            List<string> raw = [];
            for (int k = 0; k < n; k += 4)
            {
                raw.Add(Bin.Hex(ucode.Slice(o + k, 4)));
            }
            string? uniform = uniforms is not null && uniforms.TryGetValue(o + 16, out string? u) ? u : null;
            output.Add($"{Hex(o / 16, 4)}: {string.Join(' ', raw)}  {FpText(w[0], w[1], w[2], w[3], cnst, uniform)}");
            o += n;
            if ((w[0] & 1) != 0)
            {
                break;
            }
        }
        return output;
    }
    private static double Single(long bits)
    {
        return BitConverter.UInt32BitsToSingle((uint)bits);
    }
    public static (byte[] Ucode, Dictionary<string, List<long>> Uniforms) FpAsm(IReadOnlyList<string> lines)
    {
        ByteWriter output = new();
        Dictionary<string, List<long>> uniforms = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            System.Text.RegularExpressions.Match m = Rx.Full(@"[0-9a-f]+: (?:((?:[0-9a-f]{8} ?){4,8}) )?(.*)", line)
                ?? throw new InvalidDataException($"'{line}' is not a disassembly line");
            long[] raw = m.Groups[1].Success
                ? [.. m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Swap(Rx.Int(x, 16)))]
                : [0, 0, 0, 0];
            long[]? rest = raw.Length > 4 ? raw[4..] : null;
            ((long a, long b, long c, long d), long[]? cnst, string? uniform) = FpParse(m.Groups[2].Value, raw[0], raw[1], raw[2], raw[3], rest);
            if (!string.IsNullOrEmpty(uniform))
            {
                if (!uniforms.TryGetValue(uniform, out List<long>? list))
                {
                    uniforms[uniform] = list = [];
                }
                list.Add(output.Length + 16);
            }
            foreach (long x in new[] { a, b, c, d })
            {
                output.U32((uint)Swap(x), true);
            }
            if (cnst is { Length: > 0 })
            {
                if (cnst.Length != 4)
                {
                    throw new StructException($"pack expected 4 items for packing (got {cnst.Length})");
                }
                foreach (long x in cnst)
                {
                    output.U32((uint)Swap(x), true);
                }
            }
        }
        return (output.ToArray(), uniforms);
    }
}
