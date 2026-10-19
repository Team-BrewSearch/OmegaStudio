using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
using static DWSSK.Formats.Shaders.Bits;
namespace DWSSK.Formats.Shaders;
internal static class Pica200
{
    private const string Xyzw = "xyzw";
    private static readonly Dictionary<long, string> s_ops = new()
    {
        [0x00] = "add", [0x01] = "dp3", [0x02] = "dp4", [0x03] = "dph", [0x04] = "dst", [0x05] = "ex2", [0x06] = "lg2",
        [0x07] = "litp", [0x08] = "mul", [0x09] = "sge", [0x0A] = "slt", [0x0B] = "flr", [0x0C] = "max", [0x0D] = "min",
        [0x0E] = "rcp", [0x0F] = "rsq", [0x12] = "mova", [0x13] = "mov", [0x18] = "dphi", [0x19] = "dsti", [0x1A] = "sgei",
        [0x1B] = "slti", [0x20] = "break", [0x21] = "nop", [0x22] = "end", [0x23] = "breakc", [0x24] = "call", [0x25] = "callc",
        [0x26] = "callu", [0x27] = "ifu", [0x28] = "ifc", [0x29] = "loop", [0x2A] = "emit", [0x2B] = "setemit", [0x2C] = "jmpc",
        [0x2D] = "jmpu",
    };
    private static readonly HashSet<long> s_unary = [0x05, 0x06, 0x07, 0x0B, 0x0E, 0x0F, 0x12, 0x13];
    private static readonly HashSet<long> s_inverted = [0x18, 0x19, 0x1A, 0x1B];
    private static readonly HashSet<long> s_flow = [0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2C, 0x2D, 0x23];
    private static readonly string[] s_cmp = ["eq", "ne", "lt", "le", "gt", "ge"];
    private static readonly string[] s_conds = ["cc.x || cc.y", "cc.x && cc.y", "cc.x", "cc.y"];
    private static readonly string[] s_idx = [string.Empty, "a0.x", "a0.y", "aL"];
    private static readonly Dictionary<string, long> s_opsN = s_ops.ToDictionary(kv => kv.Value, kv => kv.Key);
    public static readonly Dictionary<long, string> OutputTypes = new()
    {
        [0] = "position", [1] = "normal_quat", [2] = "color", [3] = "texcoord0", [4] = "texcoord0w",
        [5] = "texcoord1", [6] = "texcoord2", [8] = "view",
    };
    public static string Reg(long r, bool dst = false)
    {
        if (dst)
        {
            return r < 0x10 ? $"o{D(r)}" : $"r{D(r - 0x10)}";
        }
        return r < 0x10 ? $"v{D(r)}" : r < 0x20 ? $"r{D(r - 0x10)}" : $"c{D(r - 0x20)}";
    }
    public static string Ureg(long r)
    {
        return r < 0x10 ? $"v{D(r)}" : r < 0x70 ? $"c{D(r - 0x10)}" : r < 0x78 ? $"i{D(r - 0x70)}" : $"b{D(r - 0x78)}";
    }
    public static long UregN(string t)
    {
        long b = t[0] switch
        {
            'v' => 0,
            'c' => 0x10,
            'i' => 0x70,
            'b' => 0x78,
            _ => throw new KeyNotFoundException($"'{t[0]}'"),
        };
        return b + Rx.Int(t[1..]);
    }
    private static string Swz(long sel)
    {
        return string.Concat(Enumerable.Range(0, 4).Select(k => Xyzw[(int)((sel >> (6 - (2 * k))) & 3)]));
    }
    private static string Src(long r, long idx, long neg, long sel)
    {
        string s = (neg != 0 ? "-" : string.Empty) + Reg(r);
        if (idx != 0 && r >= 0x20)
        {
            string num = D(r - 0x20);
            s = s[..^num.Length] + $"[{num} + {s_idx[idx]}]";
        }
        string sw = Swz(sel);
        return s + (sw == "xyzw" ? string.Empty : "." + sw);
    }
    private static string Mask(long d)
    {
        return string.Concat(Enumerable.Range(0, 4).Where(k => ((d >> (3 - k)) & 1) != 0).Select(k => Xyzw[k]));
    }
    private static long Desc(IReadOnlyList<long> opdesc, long i)
    {
        return i < opdesc.Count ? opdesc[(int)i] : throw new PyIndexException("list index out of range");
    }
    public static string Text(long w, IReadOnlyList<long> opdesc, IReadOnlyDictionary<long, string?>? labels)
    {
        long op = w >> 26;
        string? name = s_ops.TryGetValue(op, out string? n) ? n : null;
        if (op >= 0x30)
        {
            bool inv = op < 0x38;
            long d = Desc(opdesc, w & 0x1F);
            long dst = (w >> 24) & 0x1F;
            long idx = (w >> 22) & 3;
            (long s1, long s2, long s3) = inv
                ? ((w >> 17) & 0x1F, (w >> 12) & 0x1F, (w >> 5) & 0x7F)
                : ((w >> 17) & 0x1F, (w >> 10) & 0x7F, (w >> 5) & 0x1F);
            string[] ops =
            [
                Src(s1, 0, (d >> 4) & 1, (d >> 5) & 0xFF),
                Src(s2, inv ? 0 : idx, (d >> 13) & 1, (d >> 14) & 0xFF),
                Src(s3, inv ? idx : 0, (d >> 22) & 1, (d >> 23) & 0xFF),
            ];
            return $"{(inv ? "madi" : "mad")} {Reg(dst, true)}.{Mask(d)}, " + string.Join(", ", ops);
        }
        if (op is 0x2E or 0x2F)
        {
            long d = Desc(opdesc, w & 0x7F);
            long s1 = (w >> 12) & 0x7F;
            long s2 = (w >> 7) & 0x1F;
            long cx = (w >> 24) & 7;
            long cy = (w >> 21) & 7;
            return $"cmp {Src(s1, (w >> 19) & 3, (d >> 4) & 1, (d >> 5) & 0xFF)}, {(cx < 6 ? s_cmp[cx] : D(cx))}, "
                + $"{(cy < 6 ? s_cmp[cy] : D(cy))}, {Src(s2, 0, (d >> 13) & 1, (d >> 14) & 0xFF)}";
        }
        if (op is 0x20 or 0x21 or 0x22 or 0x2A)
        {
            return name!;
        }
        if (s_flow.Contains(op))
        {
            long dest = (w >> 10) & 0xFFF;
            long num = w & 0xFF;
            string cond = string.Empty;
            if (op is 0x25 or 0x28 or 0x2C or 0x23)
            {
                cond = $" {s_conds[(w >> 22) & 3]} (ref {D((w >> 25) & 1)}, {D((w >> 24) & 1)}),";
            }
            if (op is 0x26 or 0x27 or 0x2D)
            {
                cond = $" b{D((w >> 22) & 0xF)},";
            }
            if (op == 0x29)
            {
                cond = $" i{D((w >> 22) & 3)},";
            }
            string tgt = labels is { Count: > 0 } && labels.TryGetValue(dest, out string? l) ? l ?? "None" : D(dest);
            return $"{name}{cond} {tgt}" + (op is not (0x2C or 0x2D or 0x23) ? $", {D(num)}" : string.Empty);
        }
        if (op == 0x2B)
        {
            return $"setemit {D((w >> 24) & 3)}, prim {D((w >> 23) & 1)}, winding {D((w >> 22) & 1)}";
        }
        if (name is not null)
        {
            bool inv = s_inverted.Contains(op);
            long d = Desc(opdesc, w & 0x7F);
            long dst = (w >> 21) & 0x1F;
            long idx = (w >> 19) & 3;
            (long s1, long s2) = inv ? ((w >> 14) & 0x1F, (w >> 7) & 0x7F) : ((w >> 12) & 0x7F, (w >> 7) & 0x1F);
            List<string> ops = [Src(s1, inv ? 0 : idx, (d >> 4) & 1, (d >> 5) & 0xFF)];
            if (!s_unary.Contains(op))
            {
                ops.Add(Src(s2, inv ? idx : 0, (d >> 13) & 1, (d >> 14) & 0xFF));
            }
            return $"{name} {(op == 0x12 ? "a0" : Reg(dst, true))}.{Mask(d)}, " + string.Join(", ", ops);
        }
        return $".word 0x{Hex(w, 8)}";
    }
    public static List<string> Disasm(IReadOnlyList<long> code, IReadOnlyList<long> opdesc, IReadOnlyDictionary<long, string?>? labels)
    {
        return [.. code.Select((w, pc) => $"{Hex(pc, 4)}: {Hex(w, 8)}  {Text(w, opdesc, labels)}")];
    }
    private static (long Reg, long? Idx, bool Neg, long Sel) SrcParse(string t)
    {
        bool neg = t.StartsWith('-');
        Match m = Rx.Full(@"(.*?)((?:\.[xyzw]{4})?)", neg ? t[1..] : t)!;
        string reg = m.Groups[1].Value;
        string sw = m.Groups[2].Value;
        Match? rel = Rx.Full(@"c\[(\d+) \+ (a0\.x|a0\.y|aL)\]", reg);
        long r;
        long? idx;
        if (rel is not null)
        {
            r = 0x20 + Rx.Int(rel.Groups[1].Value);
            idx = Array.IndexOf(s_idx, rel.Groups[2].Value);
        }
        else
        {
            if (reg.Length == 0)
            {
                throw new PyIndexException("string index out of range");
            }
            long b = reg[0] switch
            {
                'v' => 0,
                'r' => 0x10,
                'c' => 0x20,
                _ => throw new KeyNotFoundException($"'{reg[0]}'"),
            };
            r = b + Rx.Int(reg[1..]);
            idx = null;
        }
        int[] sel = Rsx.SwzSel(sw);
        long s = 0;
        for (int k = 0; k < sel.Length; k++)
        {
            s += (long)sel[k] << (6 - (2 * k));
        }
        return (r, idx, neg, s);
    }
    private static long DescIndex(List<long[]> opdesc, string mask, List<(long Reg, long? Idx, bool Neg, long Sel)> srcs, int limit)
    {
        long d = 1L << 31;
        long used = 0xF;
        for (int k = 0; k < 4; k++)
        {
            d |= (mask.Contains(Xyzw[k], StringComparison.Ordinal) ? 1L : 0) << (3 - k);
        }
        for (int n = 0; n < srcs.Count; n++)
        {
            int lo = new[] { 4, 13, 22 }[n];
            d |= ((srcs[n].Neg ? 1L : 0) | (srcs[n].Sel << 1)) << lo;
            used |= 0x1FFL << lo;
        }
        long need = (2L << srcs.Count) - 1;
        for (int i = 0; i < opdesc.Count; i++)
        {
            if ((opdesc[i][0] & used) == (d & used))
            {
                opdesc[i][1] |= need;
                return i;
            }
        }
        if (opdesc.Count >= limit)
        {
            throw new InvalidDataException("operand descriptor table is full");
        }
        opdesc.Add([d, need]);
        return opdesc.Count - 1;
    }
    private static long Dst(string t)
    {
        return Rx.Int(t[1..]) + (t[0] == 'r' ? 0x10 : 0);
    }
    private static (string, string) SplitDot(string t)
    {
        string[] p = t.Split('.');
        return p.Length == 2 ? (p[0], p[1]) : throw new InvalidDataException($"not enough values to unpack (expected 2, got {p.Length})");
    }
    public static long Parse(string text, long w, List<long[]> opdesc, IReadOnlyDictionary<string, long>? names)
    {
        if (text.StartsWith(".word", StringComparison.Ordinal))
        {
            return Rx.Int(Py.SplitWhitespace(text)[1], 16);
        }
        int sp = text.IndexOf(' ', StringComparison.Ordinal);
        string name = sp < 0 ? text : text[..sp];
        string rest = sp < 0 ? string.Empty : text[(sp + 1)..];
        List<string> ops = rest.Length > 0 ? [.. rest.Split(", ")] : [];
        if (name is "mad" or "madi")
        {
            (string dst, string mask) = SplitDot(ops[0]);
            List<(long, long?, bool, long)> srcs = [.. ops.Skip(1).Select(SrcParse)];
            bool inv = name == "madi";
            long? idx = inv ? srcs[2].Item2 : srcs[1].Item2;
            w = Put(w, 29, 3, inv ? 6 : 7);
            w = Put(Put(w, 24, 5, Dst(dst)), 17, 5, srcs[0].Item1);
            w = inv
                ? Put(Put(w, 12, 5, srcs[1].Item1), 5, 7, srcs[2].Item1)
                : Put(Put(w, 10, 7, srcs[1].Item1), 5, 5, srcs[2].Item1);
            if (idx is { } ix)
            {
                w = Put(w, 22, 2, ix);
            }
            return Put(w, 0, 5, DescIndex(opdesc, mask, srcs, 32));
        }
        if (name == "cmp")
        {
            (long Reg, long? Idx, bool Neg, long Sel) s1 = SrcParse(ops[0]);
            (long Reg, long? Idx, bool Neg, long Sel) s2 = SrcParse(ops[3]);
            long cx = Array.IndexOf(s_cmp, ops[1]) is int a and >= 0 ? a : Rx.Int(ops[1]);
            long cy = Array.IndexOf(s_cmp, ops[2]) is int b and >= 0 ? b : Rx.Int(ops[2]);
            w = Put(Put(Put(w, 27, 5, 0x17), 24, 3, cx), 21, 3, cy);
            w = Put(Put(w, 12, 7, s1.Reg), 7, 5, s2.Reg);
            if (s1.Idx is { } ix)
            {
                w = Put(w, 19, 2, ix);
            }
            return Put(w, 0, 7, DescIndex(opdesc, "xyzw", [s1, s2], 128));
        }
        if (name == "setemit")
        {
            Match m = Rx.Full(@"(\d+), prim (\d+), winding (\d+)", rest) ?? throw new InvalidDataException($"'{rest}' is not a setemit operand list");
            return Put(Put(Put(Put(w, 26, 6, 0x2B), 24, 2, Rx.Int(m.Groups[1].Value)), 23, 1, Rx.Int(m.Groups[2].Value)), 22, 1, Rx.Int(m.Groups[3].Value));
        }
        long op = s_opsN.TryGetValue(name, out long found) ? found : throw new KeyNotFoundException($"'{name}'");
        w = Put(w, 26, 6, op);
        if (op is 0x20 or 0x21 or 0x22 or 0x2A)
        {
            return w;
        }
        if (s_flow.Contains(op))
        {
            Match m = Rx.Full(@"(?:(.+?) \(ref (\d), (\d)\),|b(\d+),|i(\d),)? ?(\S+?)(?:, (\d+))?", rest)
                ?? throw new InvalidDataException($"'{rest}' is not a branch operand list");
            if (m.G(1) is { Length: > 0 } c)
            {
                int ci = Array.IndexOf(s_conds, c);
                if (ci < 0)
                {
                    throw new InvalidDataException($"'{c}' is not in list");
                }
                w = Put(Put(Put(w, 25, 1, Rx.Int(m.Groups[2].Value)), 24, 1, Rx.Int(m.Groups[3].Value)), 22, 2, ci);
            }
            if (m.G(4) is { Length: > 0 } bb)
            {
                w = Put(w, 22, 4, Rx.Int(bb));
            }
            if (m.G(5) is { Length: > 0 } ii)
            {
                w = Put(w, 22, 2, Rx.Int(ii));
            }
            string target = m.Groups[6].Value;
            w = Put(w, 10, 12, names is not null && names.TryGetValue(target, out long tv) ? tv : Rx.Int(target));
            return m.G(7) is { Length: > 0 } num ? Put(w, 0, 8, Rx.Int(num)) : w;
        }
        (string d0, string msk) = SplitDot(ops[0]);
        List<(long Reg, long? Idx, bool Neg, long Sel)> sr = [.. ops.Skip(1).Select(SrcParse)];
        bool invd = s_inverted.Contains(op);
        if (d0 != "a0")
        {
            w = Put(w, 21, 5, Dst(d0));
        }
        if (invd)
        {
            w = Put(Put(w, 14, 5, sr[0].Reg), 7, 7, sr[1].Reg);
        }
        else
        {
            w = Put(w, 12, 7, sr[0].Reg);
            if (sr.Count > 1)
            {
                w = Put(w, 7, 5, sr[1].Reg);
            }
        }
        long? id = invd ? sr[1].Idx : sr[0].Idx;
        if (id is { } i2)
        {
            w = Put(w, 19, 2, i2);
        }
        return Put(w, 0, 7, DescIndex(opdesc, msk, sr, 128));
    }
    public static (List<long> Code, List<long[]> Opdesc) Asm(IReadOnlyList<string> lines, IEnumerable<long[]> pairs, IReadOnlyDictionary<long, string?>? labels)
    {
        List<long[]> opdesc = [.. pairs.Select(x => (long[])x.Clone())];
        Dictionary<string, long> names = [];
        if (labels is not null)
        {
            foreach ((long k, string? v) in labels)
            {
                names[v ?? "None"] = k;
            }
        }
        List<long> code = [];
        foreach (string line in lines)
        {
            Match m = Rx.Full(@"[0-9a-f]+: (?:([0-9a-f]{8})  )?(.*)", line) ?? throw new InvalidDataException($"'{line}' is not a disassembly line");
            long w = m.Groups[1].Success ? Rx.Int(m.Groups[1].Value, 16) : 0;
            if (!m.Groups[1].Success || Text(w, [.. opdesc.Select(x => x[0])], labels) != m.Groups[2].Value)
            {
                w = Parse(m.Groups[2].Value, w, opdesc, names);
            }
            code.Add(w);
        }
        return (code, opdesc);
    }
}
