using DWSSK.Formats.Core;
using DWSSK.Formats.Data;
using static DWSSK.Formats.Shaders.Bits;

namespace DWSSK.Formats.Shaders;

internal static class Shader
{
    private static readonly string[] s_constTypes = ["bool", "int", "float24"];
    private static readonly string[] s_dvleTypes = ["vertex", "geometry"];

    private static readonly Dictionary<long, string> s_cgVar = new() { [4101] = "varying", [4102] = "uniform", [4103] = "constant", [4104] = "mixed" };
    private static readonly Dictionary<long, string> s_cgDir = new() { [4097] = "in", [4098] = "out", [4099] = "inout" };
    private static readonly string[] s_vpInfo = ["instructions", "instruction_slot", "registers", "inputs_mask", "outputs_mask", "user_clip_mask"];
    private static readonly string[] s_fpInfo = ["instructions", "inputs_mask", "partial_tex_type", "texcoords_mask", "texcoords_2d", "texcoords_centroid", "registers", "output_from_h0", "depth_replace", "pixel_kill"];
    private static readonly char[] s_fpInfoFmt = ['I', 'I', 'I', 'H', 'H', 'H', 'B', 'B', 'B', 'B'];
    private static readonly Dictionary<string, long> s_typeN = BuildTypeN();
    private static readonly Dictionary<string, long> s_resN = BuildResN();
    private static readonly Dictionary<long, string> s_rs = new() { [0] = "bool", [1] = "int4", [2] = "float4", [3] = "sampler" };
    private static readonly Dictionary<long, string> s_pc = new() { [0] = "scalar", [1] = "vector", [2] = "matrix_rows", [3] = "matrix_columns", [4] = "object", [5] = "struct" };
    private static readonly Dictionary<long, string> s_pt = new()
    {
        [0] = "void", [1] = "bool", [2] = "int", [3] = "float", [4] = "string", [5] = "texture", [10] = "sampler",
        [11] = "sampler1D", [12] = "sampler2D", [13] = "sampler3D", [14] = "samplerCUBE",
    };

    private static readonly string[] s_declUsage =
        ["position", "blendweight", "blendindices", "normal", "psize", "texcoord", "tangent", "binormal", "tessfactor", "positiont", "color", "fog", "depth", "sample"];

    private static readonly string[] s_xeInfo = ["field08", "field0C", "field10", "interpolator_info"];

    private static object CgType(long t)
    {
        if (t is >= 1025 and <= 1028)
        {
            return "half" + (t == 1025 ? string.Empty : D(t - 1024));
        }
        if (t is >= 1045 and <= 1048)
        {
            return "float" + (t == 1045 ? string.Empty : D(t - 1044));
        }
        if (t is >= 1049 and <= 1064)
        {
            long r = (t - 1049) / 4;
            long c = (t - 1049) % 4;
            return $"float{D(r + 1)}x{D(c + 1)}";
        }
        if (t is >= 1029 and <= 1044)
        {
            long r = (t - 1029) / 4;
            long c = (t - 1029) % 4;
            return $"half{D(r + 1)}x{D(c + 1)}";
        }
        return t switch
        {
            1065 => "sampler1D",
            1066 => "sampler2D",
            1067 => "sampler3D",
            1068 => "samplerRECT",
            1069 => "samplerCUBE",
            1 => "struct",
            2 => "array",
            _ => t,
        };
    }

    private static object? CgResource(long r)
    {
        if (r is >= 2048 and <= 2063)
        {
            return $"TEXUNIT{D(r - 2048)}";
        }
        if (r is >= 2113 and <= 2128)
        {
            return $"ATTR{D(r - 2113)}";
        }
        if (r is >= 3220 and <= 3229)
        {
            return $"TEXCOORD{D(r - 3220)}";
        }
        return r switch
        {
            2178 => "C",
            2243 => "HPOS",
            2245 => "COL0",
            2246 => "COL1",
            2757 => "COLOR0",
            2758 => "COLOR1",
            3256 => "UNDEFINED",
            0 => null,
            _ => r,
        };
    }

    private static Dictionary<string, long> BuildTypeN()
    {
        Dictionary<string, long> d = [];
        for (long t = 0; t < 4096; t++)
        {
            if (CgType(t) is string s)
            {
                d[s] = t;
            }
        }
        return d;
    }

    private static Dictionary<string, long> BuildResN()
    {
        Dictionary<string, long> d = [];
        for (long r = 0; r < 4096; r++)
        {
            if (CgResource(r) is string s)
            {
                d[s] = r;
            }
        }
        return d;
    }

    private static string? CStr(byte[] b, long o)
    {
        return o != 0 ? Bin.CString(b, (int)o) : null;
    }

    private static long Inv(Dictionary<long, string> table, object? v)
    {
        if (v is string s)
        {
            foreach ((long k, string x) in table)
            {
                if (x == s)
                {
                    return k;
                }
            }
            throw new StructException($"required argument is not an integer ('{s}')");
        }
        return J.L(v);
    }

    public static JObj DescribeCg(byte[] b)
    {
        uint[] h = Bin.U32s(b, 0, 8, true);
        long profile = h[0];
        object kind = profile switch
        {
            7003 => "vertex",
            7004 => "fragment",
            _ => profile,
        };
        List<object?> pars = [];
        for (int i = 0; i < h[3]; i++)
        {
            int at = (int)h[4] + (48 * i);
            long[] v = [.. Enumerable.Range(0, 12).Select(k => (long)Bin.I32(b, at + (4 * k), true))];
            object type = CgType(v[0]);
            JObj p = new()
            {
                ["name"] = CStr(b, v[4]),
                ["type"] = type,
                ["variability"] = s_cgVar.TryGetValue(v[2], out string? vv) ? vv : v[2],
                ["direction"] = s_cgDir.TryGetValue(v[8], out string? dd) ? dd : v[8],
                ["resource"] = CgResource(v[1]),
            };
            if (v[3] != -1)
            {
                p["resource_index"] = v[3];
            }
            if (v[7] != 0)
            {
                p["semantic"] = CStr(b, v[7]);
            }
            if (v[5] != 0)
            {
                bool matrix = (type is string ts ? ts : D((long)type)).Contains('x', StringComparison.Ordinal);
                p["default"] = J.Doubles(Bin.F32s(b, (int)v[5], matrix ? 16 : 4, true));
            }
            if (v[6] != 0)
            {
                int cnt = (int)Bin.U32(b, (int)v[6], true);
                p["embedded_at"] = J.Longs(Bin.U32s(b, (int)v[6] + 4, cnt, true));
            }
            p["number"] = v[9];
            p["referenced"] = v[10] != 0;
            p["shared"] = v[11] != 0;
            pars.Add(p);
        }
        JObj info = [];
        if (kind is "vertex")
        {
            uint[] w = Bin.U32s(b, (int)h[5], 6, true);
            for (int k = 0; k < 6; k++)
            {
                info[s_vpInfo[k]] = (long)w[k];
            }
        }
        else
        {
            int o = (int)h[5];
            for (int k = 0; k < s_fpInfo.Length; k++)
            {
                (long v, int size) = s_fpInfoFmt[k] switch
                {
                    'I' => ((long)Bin.U32(b, o, true), 4),
                    'H' => ((long)Bin.U16(b, o, true), 2),
                    _ => ((long)Bin.U8(b, o), 1),
                };
                info[s_fpInfo[k]] = v;
                o += size;
            }
        }
        byte[] ucode = Bin.Slice(b, (int)h[7], (int)(h[7] + h[6])).ToArray();
        Dictionary<long, string?> uniforms = [];
        foreach (object? po in pars)
        {
            JObj p = J.O(po);
            if (p.Get("embedded_at") is List<object?> offs)
            {
                foreach (object? o in offs)
                {
                    uniforms[J.L(o)] = p["name"] as string;
                }
            }
        }
        return new JObj
        {
            ["format"] = "CgBinaryProgram",
            ["profile"] = kind,
            ["revision"] = (long)h[1],
            ["program"] = info,
            ["parameters"] = pars,
            ["disassembly"] = J.List((kind is "vertex" ? Rsx.VpDisasm(ucode) : Rsx.FpDisasm(ucode, uniforms)).Select(x => (object?)x)),
        };
    }

    public static byte[] BuildCg(JObj d, byte[] ucode)
    {
        List<object?> pars = J.A(d["parameters"]);
        ByteWriter output = new();
        output.Zeros(32 + (48 * pars.Count));
        for (int i = 0; i < pars.Count; i++)
        {
            JObj p = J.O(pars[i]);
            long dflt = 0;
            long emb = 0;
            long sem = 0;
            if (p.Has("semantic"))
            {
                sem = output.Length;
                output.Add(Bin.Latin1(J.S(p["semantic"])));
                output.Add(0);
            }
            if (p.Has("default"))
            {
                output.Pad(16);
                dflt = output.Length;
                foreach (object? x in J.A(p["default"]))
                {
                    output.F32(J.D(x), true);
                }
            }
            if (p.Has("embedded_at"))
            {
                output.Pad(16);
                emb = output.Length;
                List<object?> at = J.A(p["embedded_at"]);
                output.U32((uint)at.Count, true);
                foreach (object? x in at)
                {
                    output.U32(J.U(x), true);
                }
            }
            long name = output.Length;
            output.Add(Bin.Latin1(J.S(p["name"])));
            output.Add(0);
            long type = p["type"] is string ts ? (s_typeN.TryGetValue(ts, out long tn) ? tn : throw new StructException("required argument is not an integer")) : J.L(p["type"]);
            long res = p["resource"] is null ? 0 : p["resource"] is string rs ? (s_resN.TryGetValue(rs, out long rn) ? rn : throw new StructException("required argument is not an integer")) : J.L(p["resource"]);
            long[] v =
            [
                type, res, Inv(s_cgVar, p["variability"]), J.L(p.Get("resource_index", -1L)), name, dflt, emb, sem,
                Inv(s_cgDir, p["direction"]), J.L(p["number"]), J.L(p["referenced"]), J.L(p["shared"]),
            ];
            for (int k = 0; k < 12; k++)
            {
                output.PatchU32(32 + (48 * i) + (4 * k), (uint)J.I32(v[k]), true);
            }
        }
        output.Pad(16);
        int prog = output.Length;
        JObj info = J.O(d["program"]).Copy();
        info["instructions"] = (long)(ucode.Length / 16);
        if (d["profile"] is "vertex")
        {
            foreach (string k in s_vpInfo)
            {
                output.U32(J.U(info[k]), true);
            }
        }
        else
        {
            for (int k = 0; k < s_fpInfo.Length; k++)
            {
                object? v = info[s_fpInfo[k]];
                switch (s_fpInfoFmt[k])
                {
                    case 'I':
                        output.U32(J.U(v), true);
                        break;
                    case 'H':
                        output.U16(J.U16(v), true);
                        break;
                    default:
                        output.Add(J.U8(v));
                        break;
                }
            }
        }
        output.Pad(16);
        int uoff = output.Length;
        output.Add(ucode);
        long profile = d["profile"] switch
        {
            "vertex" => 7003,
            "fragment" => 7004,
            object o => J.L(o),
            null => throw new StructException("required argument is not an integer"),
        };
        uint[] head = [(uint)profile, J.U(d["revision"]), (uint)output.Length, (uint)pars.Count, 32, (uint)prog, (uint)ucode.Length, (uint)uoff];
        for (int k = 0; k < 8; k++)
        {
            output.PatchU32(4 * k, head[k], true);
        }
        return output.ToArray();
    }

    private static JObj XeElement(long v)
    {
        return new JObj
        {
            ["usage"] = Usage((v >> 12) & 15),
            ["index"] = (v >> 16) & 15,
            ["address"] = v & 0xFFF,
            ["flags"] = v >> 20,
        };
    }

    private static JObj XeInterp(long v)
    {
        return new JObj
        {
            ["usage"] = Usage((v >> 4) & 15),
            ["index"] = v & 15,
            ["register"] = (v >> 8) & 15,
            ["flags"] = v >> 12,
        };
    }

    private static string Usage(long i)
    {
        return i < s_declUsage.Length ? s_declUsage[i] : throw new PyIndexException("list index out of range");
    }

    private static long UsageN(object? u)
    {
        int i = Array.IndexOf(s_declUsage, J.S(u));
        return i >= 0 ? i : throw new InvalidDataException($"'{u}' is not in list");
    }

    public static JObj DescribeX360(byte[] b)
    {
        uint[] h = Bin.U32s(b, 0, 7, true);
        uint magic = h[0];
        int physOff = (int)h[1];
        int srcOff = (int)h[3];
        int ctBlock = (int)h[4];
        int defOff = (int)h[5];
        int infoOff = (int)h[6];
        uint srcId = Bin.U32(b, srcOff, true);
        int plen = (int)Bin.U32(b, srcOff + 4, true);
        int t = ctBlock + 4;
        uint[] ct = Bin.U32s(b, t, 7, true);
        uint creator = ct[1];
        uint version = ct[2];
        int n = (int)ct[3];
        int cinfo = (int)ct[4];
        uint flags = ct[5];
        uint target = ct[6];
        List<object?> consts = [];
        for (int i = 0; i < n; i++)
        {
            int q = t + cinfo + (20 * i);
            uint name = Bin.U32(b, q, true);
            long rset = Bin.U16(b, q + 4, true);
            long ridx = Bin.U16(b, q + 6, true);
            long rcnt = Bin.U16(b, q + 8, true);
            uint tinfo = Bin.U32(b, q + 12, true);
            int tq = t + (int)tinfo;
            long cls = Bin.U16(b, tq, true);
            long typ = Bin.U16(b, tq + 2, true);
            consts.Add(new JObj
            {
                ["name"] = CStr(b, t + name),
                ["register_set"] = s_rs.TryGetValue(rset, out string? rs) ? rs : rset,
                ["register"] = ridx,
                ["count"] = rcnt,
                ["class"] = s_pc.TryGetValue(cls, out string? pc) ? pc : cls,
                ["type"] = s_pt.TryGetValue(typ, out string? pt) ? pt : typ,
                ["rows"] = (long)Bin.U16(b, tq + 4, true),
                ["columns"] = (long)Bin.U16(b, tq + 6, true),
                ["elements"] = (long)Bin.U16(b, tq + 8, true),
            });
        }
        bool vs = magic == 0x102A1101;
        uint[] w = Bin.U32s(b, infoOff, 9, true);
        JObj prog = [];
        for (int k = 0; k < 4; k++)
        {
            prog[s_xeInfo[k]] = (long)w[2 + k];
        }
        int nint = (int)((w[5] >> 5) & 31);
        int kk = infoOff + 24;
        if (vs)
        {
            int f18 = (int)w[6];
            int nvert = (int)w[7];
            int f20 = (int)w[8];
            uint[] lst = Bin.U32s(b, kk + 12, f18 + nvert + nint + f20, true);
            prog["field18"] = J.Longs(lst[..f18]);
            prog["vertex_elements"] = J.List(lst[f18..(f18 + nvert)].Select(v => (object?)XeElement(v)));
            prog["interpolators"] = J.List(lst[(f18 + nvert)..(f18 + nvert + nint)].Select(v => (object?)XeInterp(v)));
            prog["field20"] = J.Longs(lst[(f18 + nvert + nint)..]);
        }
        else
        {
            prog["field18"] = (long)w[6];
            prog["outputs"] = (long)w[7];
            prog["interpolators"] = J.List(Bin.U32s(b, kk + 8, nint, true).Select(v => (object?)XeInterp(v)));
        }
        List<object?> literals = [];
        if (defOff != 0)
        {
            long count = Bin.FloorDiv((int)Bin.U32(b, defOff + 16, true), 4) - 3 >> 1;
            for (int k = 0; k < count; k++)
            {
                uint rc = Bin.U32(b, defOff + 20 + (8 * k), true);
                uint po = Bin.U32(b, defOff + 24 + (8 * k), true);
                literals.Add(new JObj
                {
                    ["register"] = (long)(rc >> 16),
                    ["values"] = J.Doubles(Bin.F32s(b, physOff + (int)po, (int)(rc & 0xFFFF), true)),
                });
            }
        }
        byte[] ucode = Bin.Slice(b, physOff + (int)w[0], physOff + (int)w[0] + (int)w[1]).ToArray();
        return new JObj
        {
            ["format"] = "Xbox 360 shader",
            ["kind"] = vs ? "vertex" : "pixel",
            ["profile"] = CStr(b, t + target),
            ["version"] = Py.Hex8(version),
            ["flags"] = (long)flags,
            ["creator"] = CStr(b, t + creator),
            ["source"] = Bin.Latin1(Bin.Slice(b, srcOff + 8, srcOff + 8 + plen)),
            ["source_id"] = Py.Hex8(srcId),
            ["constants"] = consts,
            ["literals"] = literals,
            ["program"] = prog,
            ["disassembly"] = J.List(Xenos.Disasm(ucode, vs).Select(x => (object?)x)),
        };
    }

    private static byte[] XeCtab(JObj d)
    {
        List<object?> consts = J.A(d["constants"]);
        ByteWriter output = new();
        output.Zeros(28 + (20 * consts.Count));
        Dictionary<(long, long, long, long, long), int> types = [];
        for (int i = 0; i < consts.Count; i++)
        {
            JObj c = J.O(consts[i]);
            int name = output.Length;
            output.Add(Bin.Latin1(J.S(c["name"])));
            output.Add(0);
            (long, long, long, long, long) key = (Inv(s_pc, c["class"]), Inv(s_pt, c["type"]), J.L(c["rows"]), J.L(c["columns"]), J.L(c["elements"]));
            if (!types.TryGetValue(key, out int at))
            {
                output.Fill(0xAB, Bin.Mod(-output.Length, 4));
                at = output.Length;
                types[key] = at;
                output.U16(J.U16(key.Item1), true);
                output.U16(J.U16(key.Item2), true);
                output.U16(J.U16(key.Item3), true);
                output.U16(J.U16(key.Item4), true);
                output.U16(J.U16(key.Item5), true);
                output.U16(0, true);
                output.U32(0, true);
            }
            int q = 28 + (20 * i);
            output.PatchU32(q, (uint)name, true);
            output.PatchU16(q + 4, J.U16(Inv(s_rs, c["register_set"])), true);
            output.PatchU16(q + 6, J.U16(c["register"]), true);
            output.PatchU16(q + 8, J.U16(c["count"]), true);
            output.PatchU16(q + 10, 0, true);
            output.PatchU32(q + 12, (uint)at, true);
            output.PatchU32(q + 16, 0, true);
        }
        int target = output.Length;
        output.Add(Bin.Latin1(J.S(d["profile"])));
        output.Add(0);
        int creator = output.Length;
        output.Add(Bin.Latin1(J.S(d["creator"])));
        output.Add(0);
        output.Fill(0xAB, Bin.Mod(-output.Length, 4));
        uint[] head = [28, (uint)creator, Py.ParseHex(J.S(d["version"])), (uint)consts.Count, consts.Count > 0 ? 28u : 0u, J.U(d["flags"]), (uint)target];
        for (int k = 0; k < 7; k++)
        {
            output.PatchU32(4 * k, head[k], true);
        }
        return output.ToArray();
    }

    public static byte[] BuildX360(JObj d, byte[] ucode)
    {
        bool vs = J.S(d["kind"]) == "vertex";
        ByteWriter output = new();
        output.Zeros(0x24);
        byte[] path = Bin.Latin1(J.S(d["source"]));
        output.U32(Py.ParseHex(J.S(d["source_id"])), true);
        output.U32((uint)path.Length, true);
        output.Add(path);
        output.Pad(4);
        int ct = output.Length;
        byte[] tab = XeCtab(d);
        output.U32((uint)tab.Length, true);
        output.Add(tab);
        List<object?> literals = J.A(d["literals"]);
        ByteWriter phys = new();
        foreach (object? lo in literals)
        {
            foreach (object? v in J.A(J.O(lo)["values"]))
            {
                phys.F32(J.D(v), true);
            }
        }
        int defOff = 0;
        if (literals.Count > 0)
        {
            defOff = output.Length;
            int po = 0;
            output.U32(0, true);
            output.U32(1, true);
            output.U32(0, true);
            output.U32(0, true);
            output.U32((uint)((8 * literals.Count) + 12), true);
            foreach (object? lo in literals)
            {
                JObj x = J.O(lo);
                int count = J.A(x["values"]).Count;
                output.U32((J.U(x["register"]) << 16) | (uint)count, true);
                output.U32((uint)po, true);
                po += 4 * count;
            }
            output.Zeros(12);
        }
        int info = output.Length;
        JObj p = J.O(d["program"]);
        List<object?> interps = J.A(p["interpolators"]);
        long ii = Put(J.L(p["interpolator_info"]), 5, 5, interps.Count);
        output.U32((uint)phys.Length, true);
        output.U32((uint)ucode.Length, true);
        output.U32(J.U(p["field08"]), true);
        output.U32(J.U(p["field0C"]), true);
        output.U32(J.U(p["field10"]), true);
        output.U32((uint)ii, true);
        static long Elem(JObj e)
        {
            return J.L(e["address"]) | (UsageN(e["usage"]) << 12) | (J.L(e["index"]) << 16) | (J.L(e["flags"]) << 20);
        }
        static long Interp(JObj e)
        {
            return J.L(e["index"]) | (UsageN(e["usage"]) << 4) | (J.L(e["register"]) << 8) | (J.L(e["flags"]) << 12);
        }
        if (vs)
        {
            List<object?> f18 = J.A(p["field18"]);
            List<object?> elems = J.A(p["vertex_elements"]);
            List<object?> f20 = J.A(p["field20"]);
            output.U32((uint)f18.Count, true);
            output.U32((uint)elems.Count, true);
            output.U32((uint)f20.Count, true);
            foreach (long x in f18.Select(J.L).Concat(elems.Select(e => Elem(J.O(e)))).Concat(interps.Select(e => Interp(J.O(e)))).Concat(f20.Select(J.L)))
            {
                output.U32(J.U(x), true);
            }
        }
        else
        {
            output.U32(J.U(p["field18"]), true);
            output.U32(J.U(p["outputs"]), true);
            foreach (object? e in interps)
            {
                output.U32(J.U(Interp(J.O(e))), true);
            }
        }
        int physOff = output.Length;
        output.Add(phys.Span);
        output.Add(ucode);
        uint[] head = [vs ? 0x102A1101u : 0x102A1100u, (uint)physOff, (uint)(output.Length - physOff), 0x24, (uint)ct, (uint)defOff, (uint)info, 0, 0];
        for (int k = 0; k < 9; k++)
        {
            output.PatchU32(4 * k, head[k], true);
        }
        return output.ToArray();
    }

    public static JObj DescribeDvlb(byte[] b)
    {
        int n = (int)Bin.U32(b, 4, false);
        uint[] dvleOffs = Bin.U32s(b, 8, n, false);
        int p = 8 + (4 * n);
        uint[] dv = Bin.U32s(b, p + 4, 9, false);
        uint ver = dv[0];
        int codeOff = (int)dv[1];
        int codeLen = (int)dv[2];
        int odOff = (int)dv[3];
        int odCnt = (int)dv[4];
        int fnOff = (int)dv[7];
        long[] code = [.. Bin.U32s(b, p + codeOff, codeLen, false).Select(x => (long)x)];
        List<long[]> opdesc = [.. Enumerable.Range(0, odCnt).Select(k => new long[] { Bin.U32(b, p + odOff + (8 * k), false), Bin.U32(b, p + odOff + (8 * k) + 4, false) })];
        List<object?> entries = [];
        foreach (uint ou in dvleOffs)
        {
            int o = (int)ou;
            long ever = Bin.U16(b, o + 4, false);
            long typ = Bin.U8(b, o + 6);
            long merge = Bin.U8(b, o + 7);
            long main = Bin.U32(b, o + 8, false);
            long endmain = Bin.U32(b, o + 12, false);
            long inmask = Bin.U16(b, o + 16, false);
            long outmask = Bin.U16(b, o + 18, false);
            byte[] gs = Bin.Slice(b, o + 20, o + 24).ToArray();
            Bin.Need(b, o + 20, 4);
            uint[] tabs = Bin.U32s(b, o + 0x18, 10, false);
            int sym = o + (int)tabs[8];
            string? S(long off)
            {
                return off < tabs[9] ? CStr(b, sym + off) : null;
            }
            List<object?> consts = [];
            for (int k = 0; k < tabs[1]; k++)
            {
                int q = o + (int)tabs[0] + (20 * k);
                long t = Bin.U8(b, q);
                consts.Add(new JObj
                {
                    ["type"] = t < 3 ? s_constTypes[t] : t,
                    ["register"] = (long)Bin.U16(b, q + 2, false),
                    ["value"] = J.Longs(Bin.U32s(b, q + 4, 4, false)),
                });
            }
            List<object?> labels = [];
            for (int k = 0; k < tabs[3]; k++)
            {
                int q = o + (int)tabs[2] + (16 * k);
                labels.Add(new JObj
                {
                    ["name"] = S(Bin.U32(b, q + 12, false)),
                    ["offset"] = (long)Bin.U32(b, q + 4, false),
                    ["size"] = (long)Bin.I32(b, q + 8, false),
                });
            }
            List<object?> outputs = [];
            for (int k = 0; k < tabs[5]; k++)
            {
                int q = o + (int)tabs[4] + (8 * k);
                long type = Bin.U16(b, q, false);
                outputs.Add(new JObj
                {
                    ["type"] = Pica200.OutputTypes.TryGetValue(type, out string? ot) ? ot : type,
                    ["register"] = (long)Bin.U16(b, q + 2, false),
                    ["mask"] = (long)Bin.U16(b, q + 4, false),
                });
            }
            List<object?> uniforms = [];
            for (int k = 0; k < tabs[7]; k++)
            {
                int q = o + (int)tabs[6] + (8 * k);
                uniforms.Add(new JObj
                {
                    ["name"] = S(Bin.U32(b, q, false)),
                    ["first"] = Pica200.Ureg(Bin.U16(b, q + 4, false)),
                    ["last"] = Pica200.Ureg(Bin.U16(b, q + 6, false)),
                });
            }
            entries.Add(new JObj
            {
                ["version"] = ever,
                ["type"] = typ < 2 ? s_dvleTypes[typ] : typ,
                ["merge_outputs"] = merge,
                ["geometry"] = J.Longs(gs),
                ["main"] = main,
                ["endmain"] = endmain,
                ["input_mask"] = inmask,
                ["output_mask"] = outmask,
                ["constants"] = consts,
                ["labels"] = labels,
                ["outputs"] = outputs,
                ["uniforms"] = uniforms,
            });
        }
        Dictionary<long, string?> lab = Labels(entries);
        return new JObj
        {
            ["format"] = "DVLB",
            ["version"] = (long)ver,
            ["filename"] = CStr(b, p + fnOff),
            ["entries"] = entries,
            ["operand_descriptors"] = J.List(opdesc.Select(x => (object?)$"{Hex(x[0], 8)} {Hex(x[1])}")),
            ["disassembly"] = J.List(Pica200.Disasm(code, [.. opdesc.Select(x => x[0])], lab).Select(x => (object?)x)),
        };
    }

    private static Dictionary<long, string?> Labels(List<object?> entries)
    {
        Dictionary<long, string?> lab = [];
        foreach (object? e in entries)
        {
            foreach (object? x in J.A(J.O(e)["labels"]))
            {
                lab[J.L(J.O(x)["offset"])] = J.O(x)["name"] as string;
            }
        }
        return lab;
    }

    public static byte[] BuildDvlb(JObj d, List<long> code, List<long[]> opdesc)
    {
        List<object?> entries = J.A(d["entries"]);
        int n = entries.Count;
        byte[] fn = [.. Bin.Latin1(J.S(d["filename"])), 0];
        int odOff = 0x28 + (4 * code.Count);
        int fnOff = odOff + (8 * opdesc.Count);
        ByteWriter dvlp = new();
        dvlp.Add("DVLP"u8);
        foreach (uint x in new[] { J.U(d["version"]), 0x28u, (uint)code.Count, (uint)odOff, (uint)opdesc.Count, (uint)fnOff, 0u, (uint)fnOff, (uint)fn.Length })
        {
            dvlp.U32(x, false);
        }
        foreach (long c in code)
        {
            dvlp.U32(J.U(c), false);
        }
        foreach (long[] x in opdesc)
        {
            dvlp.U32(J.U(x[0]), false);
            dvlp.U32(J.U(x[1]), false);
        }
        dvlp.Add(fn);
        dvlp.Pad(4);
        ByteWriter output = new();
        output.Zeros(8 + (4 * n));
        output.Add(dvlp.Span);
        List<uint> offs = [];
        foreach (object? eo in entries)
        {
            JObj e = J.O(eo);
            offs.Add((uint)output.Length);
            ByteWriter syms = new();
            Dictionary<string, int> sym = [];
            List<object?> labels = J.A(e["labels"]);
            List<object?> uniforms = J.A(e["uniforms"]);
            foreach (object? x in labels.Concat(uniforms))
            {
                string nm = J.S(J.O(x)["name"]);
                sym[nm] = syms.Length;
                syms.Add(Bin.Latin1(nm));
                syms.Add(0);
            }
            ByteWriter body = new();
            List<object?> consts = J.A(e["constants"]);
            foreach (object? co in consts)
            {
                JObj c = J.O(co);
                long t = c["type"] is string ts ? Array.IndexOf(s_constTypes, ts) is int ti and >= 0 ? ti : throw new InvalidDataException($"'{ts}' is not in list") : J.L(c["type"]);
                body.Add(J.U8(t));
                body.Add(0);
                body.U16(J.U16(c["register"]), false);
                foreach (object? v in J.A(c["value"]))
                {
                    body.U32(J.U(v), false);
                }
            }
            for (int k = 0; k < labels.Count; k++)
            {
                JObj x = J.O(labels[k]);
                body.Add(J.U8((long)k));
                body.Add(0);
                body.Add(1);
                body.Add(0);
                body.U32(J.U(x["offset"]), false);
                body.I32(J.I32(x["size"]), false);
                body.U32((uint)sym[J.S(x["name"])], false);
            }
            List<object?> outputs = J.A(e["outputs"]);
            foreach (object? xo in outputs)
            {
                JObj x = J.O(xo);
                body.U16(J.U16(Inv(Pica200.OutputTypes, x["type"])), false);
                body.U16(J.U16(x["register"]), false);
                body.U16(J.U16(x["mask"]), false);
                body.U16(0, false);
            }
            foreach (object? xo in uniforms)
            {
                JObj x = J.O(xo);
                body.U32((uint)sym[J.S(x["name"])], false);
                body.U16(J.U16(Pica200.UregN(J.S(x["first"]))), false);
                body.U16(J.U16(Pica200.UregN(J.S(x["last"]))), false);
            }
            int[] counts = [consts.Count, labels.Count, outputs.Count, uniforms.Count];
            int[] sizes = [20, 16, 8, 8];
            List<uint> tabs = [];
            int o = 0x40;
            for (int k = 0; k < 4; k++)
            {
                tabs.Add((uint)o);
                tabs.Add((uint)counts[k]);
                o += counts[k] * sizes[k];
            }
            long typ = e["type"] is string tys ? Array.IndexOf(s_dvleTypes, tys) is int yi and >= 0 ? yi : throw new InvalidDataException($"'{tys}' is not in list") : J.L(e["type"]);
            output.Add("DVLE"u8);
            output.U16(J.U16(e["version"]), false);
            output.Add(J.U8(typ));
            output.Add(J.U8(e["merge_outputs"]));
            output.U32(J.U(e["main"]), false);
            output.U32(J.U(e["endmain"]), false);
            output.U16(J.U16(e["input_mask"]), false);
            output.U16(J.U16(e["output_mask"]), false);
            List<object?> geometry = J.A(e["geometry"]);
            byte[] g = new byte[4];
            for (int k = 0; k < Math.Min(4, geometry.Count); k++)
            {
                g[k] = J.U8(geometry[k]);
            }
            output.Add(g);
            foreach (uint x in tabs)
            {
                output.U32(x, false);
            }
            output.U32((uint)o, false);
            output.U32((uint)syms.Length, false);
            output.Add(body.Span);
            output.Add(syms.Span);
            output.Pad(4);
        }
        output.Write(0, "DVLB"u8);
        output.PatchU32(4, (uint)n, false);
        for (int k = 0; k < n; k++)
        {
            output.PatchU32(8 + (4 * k), offs[k], false);
        }
        return output.ToArray();
    }

    public static bool IsShader(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[..4].SequenceEqual("DVLB"u8))
        {
            return true;
        }
        if (b.Length >= 6 && b[..6].SequenceEqual("UNUSED"u8))
        {
            return true;
        }
        if (b.Length < 4)
        {
            return false;
        }
        uint magic = Bin.U32(b, 0, true);
        return magic is 0x102A1101 or 0x102A1100 or 7003 or 7004;
    }

    public static string Format(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4 && b[..4].SequenceEqual("DVLB"u8))
        {
            return "PICA200 vertex shader (DVLB)";
        }
        if (P2b.IsP2b(b))
        {
            return "fixed-function setup (p2b)";
        }
        if (b.Length >= 6 && b[..6].SequenceEqual("UNUSED"u8))
        {
            return "unused";
        }
        if (b.Length < 4)
        {
            return "unknown";
        }
        return Bin.U32(b, 0, true) switch
        {
            0x102A1101 => "Xenos vertex shader",
            0x102A1100 => "Xenos pixel shader",
            7003 => "RSX vertex program (Cg)",
            7004 => "RSX fragment program (Cg)",
            _ => "unknown",
        };
    }

    public static JObj Describe(byte[] b)
    {
        if (b.Length >= 4 && b.AsSpan(0, 4).SequenceEqual("DVLB"u8))
        {
            return DescribeDvlb(b);
        }
        if (b.Length >= 2 && ((b[0] == 0 && b[1] == 0x0A) || (b[0] == 0x0A && b[1] == 0)))
        {
            JObj doc = new() { ["format"] = "p2b" };
            foreach (KeyValuePair<string, object?> kv in P2b.Read(b))
            {
                doc[kv.Key] = kv.Value;
            }
            return doc;
        }
        if (b.Length >= 6 && b.AsSpan(0, 6).SequenceEqual("UNUSED"u8))
        {
            return new JObj { ["format"] = "text", ["text"] = Bin.Latin1(b) };
        }
        uint magic = Bin.U32(b, 0, true);
        if (magic is 0x102A1101 or 0x102A1100)
        {
            return DescribeX360(b);
        }
        return magic is 7003 or 7004 ? DescribeCg(b) : throw new InvalidDataException("unknown shader format");
    }

    public static byte[] Build(JObj doc)
    {
        string f = J.S(doc["format"]);
        switch (f)
        {
            case "CgBinaryProgram":
            {
                List<string> lines = [.. J.A(doc["disassembly"]).Select(J.S)];
                if (doc["profile"] is "vertex")
                {
                    return BuildCg(doc, Rsx.VpAsm(lines));
                }
                (byte[] ucode, Dictionary<string, List<long>> uni) = Rsx.FpAsm(lines);
                List<object?> pars = J.A(doc["parameters"]);
                SortedDictionary<long, string> oldMap = [];
                foreach (object? po in pars)
                {
                    JObj p = J.O(po);
                    if (p.Get("embedded_at") is List<object?> offs)
                    {
                        foreach (object? o in offs)
                        {
                            oldMap[J.L(o)] = p["name"] as string ?? "\u0000null";
                        }
                    }
                }
                List<(long, string)> old = [.. oldMap.Select(kv => (kv.Key, kv.Value))];
                List<(long, string)> nw = [.. uni.SelectMany(kv => kv.Value.Select(o => (o, kv.Key))).OrderBy(t => t.o).ThenBy(t => t.Key, StringComparer.Ordinal)];
                if (old.Select(x => x.Item2).SequenceEqual(nw.Select(x => x.Item2)))
                {
                    Dictionary<long, long> moved = [];
                    for (int i = 0; i < old.Count; i++)
                    {
                        moved[old[i].Item1] = nw[i].Item1;
                    }
                    foreach (object? po in pars)
                    {
                        JObj p = J.O(po);
                        if (p.Get("embedded_at") is List<object?> offs)
                        {
                            p["embedded_at"] = J.Longs(offs.Select(o => moved[J.L(o)]));
                        }
                    }
                }
                else
                {
                    HashSet<string> names = [.. pars.Select(p => J.O(p)["name"] as string ?? "\u0000null")];
                    List<string> missing = [.. uni.Keys.Where(k => !names.Contains(k)).Order(StringComparer.Ordinal)];
                    if (missing.Count > 0)
                    {
                        throw new InvalidDataException($"no parameter named [{string.Join(", ", missing.Select(m => $"'{m}'"))}]");
                    }
                    foreach (object? po in pars)
                    {
                        JObj p = J.O(po);
                        p.Remove("embedded_at");
                        if (p["name"] is string pn && uni.TryGetValue(pn, out List<long>? offs))
                        {
                            p["embedded_at"] = J.Longs(offs);
                        }
                    }
                }
                return BuildCg(doc, ucode);
            }
            case "Xbox 360 shader":
                return BuildX360(doc, Xenos.Asm([.. J.A(doc["disassembly"]).Select(J.S)], J.S(doc["kind"]) == "vertex"));
            case "DVLB":
            {
                Dictionary<long, string?> labels = Labels(J.A(doc["entries"]));
                List<long[]> pairs = [.. J.A(doc["operand_descriptors"]).Select(t => J.S(t).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Rx.Int(x, 16)).ToArray())];
                (List<long> code, List<long[]> opdesc) = Pica200.Asm([.. J.A(doc["disassembly"]).Select(J.S)], pairs, labels);
                return BuildDvlb(doc, code, opdesc);
            }
            case "p2b":
                return P2b.Write(doc);
            case "text":
                return Bin.Latin1(J.S(doc["text"]));
            default:
                throw new InvalidDataException($"unknown shader format {f}");
        }
    }
}
