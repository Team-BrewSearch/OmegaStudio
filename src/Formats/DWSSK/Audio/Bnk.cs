using DWSSK.Formats.Core;

namespace DWSSK.Formats.Audio;

internal sealed record BnkChunk(string Id, int Offset, int Size);

internal static class Bnk
{
    public static bool IsBank(ReadOnlySpan<byte> b)
    {
        return b.Length >= 8 && b[..4].SequenceEqual("BKHD"u8);
    }

    public static bool Big(ReadOnlySpan<byte> b)
    {
        return b.Length >= 6 && b[4] == 0 && b[5] == 0;
    }

    public static (bool Big, List<BnkChunk> Chunks, byte[] Tail) Chunks(byte[] b)
    {
        bool big = Big(b);
        List<BnkChunk> output = [];
        int o = 0;
        while (o + 8 <= b.Length)
        {
            string cid = Bin.Latin1(b.AsSpan(o, 4));
            long size = Bin.U32(b, o + 4, big);
            output.Add(new BnkChunk(cid, o + 8, (int)Math.Min(size, int.MaxValue)));
            o = (int)Math.Min(int.MaxValue, o + 8 + size);
        }
        return (big, output, Bin.Slice(b, o, b.Length).ToArray());
    }

    public static (JObj Doc, Dictionary<long, byte[]> Media) Unpack(byte[] b, IReadOnlyDictionary<uint, string>? names)
    {
        (bool big, List<BnkChunk> cs, byte[] tail) = Chunks(b);
        List<object?> chunks = [];
        JObj doc = new() { ["endian"] = big ? "big" : "little", ["chunks"] = chunks, ["tail"] = Bin.Hex(tail) };
        List<(long Id, long Off, long Size)> didx = [];
        Dictionary<long, byte[]> media = [];
        foreach (BnkChunk c in cs)
        {
            ReadOnlySpan<byte> body = Bin.Slice(b, c.Offset, c.Offset + c.Size);
            switch (c.Id)
            {
                case "DIDX":
                {
                    didx = [];
                    for (int i = 0; i < c.Size / 12; i++)
                    {
                        didx.Add((Bin.U32(b, c.Offset + (i * 12), big), Bin.U32(b, c.Offset + (i * 12) + 4, big), Bin.U32(b, c.Offset + (i * 12) + 8, big)));
                    }
                    long align = didx.All(x => x.Off % 32 == 0) ? 32 : 16;
                    chunks.Add(new JObj
                    {
                        ["id"] = c.Id,
                        ["align"] = align,
                        ["media"] = J.List(didx.Select(x => (object?)new JObj { ["id"] = x.Id, ["offset"] = x.Off, ["size"] = x.Size })),
                    });
                    break;
                }
                case "DATA":
                {
                    foreach ((long id, long off, long size) in didx)
                    {
                        media[id] = Bin.Slice(b, (int)(c.Offset + off), (int)(c.Offset + off + size)).ToArray();
                    }
                    chunks.Add(new JObj { ["id"] = c.Id, ["size"] = (long)c.Size, ["gaps"] = Gaps(body, didx) });
                    break;
                }
                case "HIRC":
                {
                    long n = Bin.U32(b, c.Offset, big);
                    List<object?> objs = [];
                    int p = c.Offset + 4;
                    for (long k = 0; k < n; k++)
                    {
                        long t = Bin.U8(b, p);
                        long sz = Bin.U32(b, p + 1, big);
                        long oid = Bin.U32(b, p + 5, big);
                        JObj obj = new()
                        {
                            ["type"] = t,
                            ["kind"] = Hirc.Types.TryGetValue(t, out string? kind) ? kind : "unknown",
                            ["id"] = oid,
                        };
                        if (names is not null && t == 4 && names.TryGetValue((uint)oid, out string? name))
                        {
                            obj["name"] = name;
                        }
                        byte[] ob = Bin.Slice(b, p + 9, (int)(p + 5 + sz)).ToArray();
                        JObj? fields = Hirc.Decode(t, ob, big);
                        if (fields is null)
                        {
                            obj["body"] = Bin.Hex(ob);
                        }
                        else
                        {
                            foreach (KeyValuePair<string, object?> kv in fields)
                            {
                                obj[kv.Key] = kv.Value;
                            }
                        }
                        objs.Add(obj);
                        p = (int)(p + 5 + sz);
                    }
                    chunks.Add(new JObj { ["id"] = c.Id, ["objects"] = objs });
                    break;
                }
                default:
                {
                    JObj? fields = Hirc.IsDescribedChunk(c.Id) ? Hirc.DecodeChunk(c.Id, body.ToArray(), big) : null;
                    JObj ch = new() { ["id"] = c.Id };
                    if (fields is null)
                    {
                        ch["raw"] = Bin.Hex(body);
                    }
                    else
                    {
                        foreach (KeyValuePair<string, object?> kv in fields)
                        {
                            ch[kv.Key] = kv.Value;
                        }
                    }
                    chunks.Add(ch);
                    break;
                }
            }
        }
        return (doc, media);
    }

    private static List<object?> Gaps(ReadOnlySpan<byte> data, List<(long Id, long Off, long Size)> didx)
    {
        bool[] covered = new bool[data.Length];
        foreach ((_, long off, long size) in didx)
        {
            for (long i = off; i < Math.Min(off + size, data.Length); i++)
            {
                covered[i] = true;
            }
        }
        List<object?> gaps = [];
        int k = 0;
        while (k < data.Length)
        {
            if (!covered[k])
            {
                int j = k;
                while (j < data.Length && !covered[j])
                {
                    j++;
                }
                if (Bin.AnyNonZero(data[k..j]))
                {
                    gaps.Add(new List<object?> { (long)k, Bin.Hex(data[k..j]) });
                }
                k = j;
            }
            else
            {
                k++;
            }
        }
        return gaps;
    }

    public static long Codec(ReadOnlySpan<byte> wem, bool big)
    {
        if (wem.Length < 16 || !wem[8..16].SequenceEqual("WAVEfmt "u8))
        {
            return 0;
        }
        long tag = Bin.U16(wem, 20, big);
        long size = Bin.U32(wem, 16, big);
        if (tag == 2)
        {
            return size >= 0x40 ? 0x50001 : 0x20001;
        }
        return tag switch
        {
            0xFFFE or 1 => 0x10001,
            0x166 => 0x30001,
            0xFFFF => 0x40001,
            _ => 0,
        };
    }

    private static void MoveSource(JObj o, long bank, long dataAt, Dictionary<long, (long Off, long Size)> old, List<object?> didx, Dictionary<long, byte[]> media, bool big)
    {
        long sid = J.L(o["source"]);
        long stream = J.L(o["stream"]);
        if (stream is not (0 or 2) || !old.TryGetValue(sid, out (long Off, long Size) was))
        {
            return;
        }
        long codec = Codec(media[sid], big);
        if (codec != 0)
        {
            o["codec"] = codec;
        }
        JObj nw = didx.Select(J.O).First(m => J.L(m["id"]) == sid);
        if (stream == 0 && J.L(o["file"]) == bank && J.L(o["offset"]) == dataAt + was.Off && J.L(o["size"]) == was.Size)
        {
            o["offset"] = dataAt + J.L(nw["offset"]);
            o["size"] = nw["size"];
        }
        else if (stream == 2 && J.L(o["offset"]) == 0 && J.L(o["size"]) == was.Size)
        {
            o["size"] = nw["size"];
        }
    }

    public static byte[] Pack(JObj doc, IReadOnlyDictionary<long, byte[]> files)
    {
        bool big = J.S(doc["endian"]) == "big";
        ByteWriter buf = new();
        Dictionary<long, byte[]> media = [];
        Dictionary<long, (long Off, long Size)> old = [];
        List<object?> didx = [];
        long bank = 0;
        long dataAt = 0;
        long dataSize = 0;
        List<object?> chunks = J.A(doc["chunks"]);
        foreach (object? co in chunks)
        {
            JObj c = J.O(co);
            string id = J.S(c["id"]);
            byte[] body;
            switch (id)
            {
                case "DIDX":
                {
                    List<object?> list = J.A(c["media"]);
                    media = [];
                    foreach (object? mo in list)
                    {
                        long mid = J.L(J.O(mo)["id"]);
                        media[mid] = files.TryGetValue(mid, out byte[]? f) ? f : throw new FileNotFoundException($"media/{mid}.wem is missing");
                    }
                    old = [];
                    foreach (object? mo in list)
                    {
                        JObj m = J.O(mo);
                        old[J.L(m["id"])] = (J.L(m["offset"]), J.L(m["size"]));
                    }
                    long align = J.L(c["align"]);
                    long off = 0;
                    ByteWriter w = new();
                    foreach (object? mo in list)
                    {
                        JObj m = J.O(mo);
                        off = (off + align - 1) / align * align;
                        m["offset"] = off;
                        m["size"] = (long)media[J.L(m["id"])].Length;
                        w.U32(J.U(m["id"]), big);
                        w.U32((uint)off, big);
                        w.U32(J.U(m["size"]), big);
                        off += J.L(m["size"]);
                    }
                    dataSize = off;
                    body = w.ToArray();
                    break;
                }
                case "DATA":
                {
                    long size = J.L(c["size"]);
                    byte[] data = new byte[media.Count > 0 ? Math.Max(dataSize, size) : size];
                    didx = J.A(chunks.Select(J.O).First(x => J.S(x["id"]) == "DIDX")["media"]);
                    foreach (object? mo in didx)
                    {
                        JObj m = J.O(mo);
                        media[J.L(m["id"])].CopyTo(data, J.L(m["offset"]));
                    }
                    foreach (object? go in J.A(c["gaps"]))
                    {
                        List<object?> g = J.A(go);
                        byte[] hex = Bin.FromHex(J.S(g[1]));
                        long at = J.L(g[0]);
                        if (at + hex.Length > data.Length)
                        {
                            Array.Resize(ref data, (int)(at + hex.Length));
                        }
                        hex.CopyTo(data, at);
                    }
                    body = data;
                    break;
                }
                case "HIRC":
                {
                    List<object?> objs = J.A(c["objects"]);
                    ByteWriter w = new();
                    w.U32((uint)objs.Count, big);
                    foreach (object? oo in objs)
                    {
                        JObj o = J.O(oo);
                        byte[] raw;
                        if (o.Has("body"))
                        {
                            raw = Bin.FromHex(J.S(o["body"]));
                        }
                        else
                        {
                            if (J.L(o["type"]) == 2 && media.Count > 0)
                            {
                                MoveSource(o, bank, dataAt, old, didx, media, big);
                            }
                            raw = Hirc.Encode(J.L(o["type"]), o, big);
                        }
                        w.Add(J.U8(o["type"]));
                        w.U32((uint)(raw.Length + 4), big);
                        w.U32(J.U(o["id"]), big);
                        w.Add(raw);
                    }
                    body = w.ToArray();
                    break;
                }
                default:
                    body = c.Has("raw") ? Bin.FromHex(J.S(c["raw"])) : Hirc.EncodeChunk(id, c, big);
                    if (id == "BKHD")
                    {
                        bank = Bin.U32(body, 4, big);
                    }
                    break;
            }
            if (id == "DATA")
            {
                dataAt = buf.Length + 8;
            }
            buf.Add(Bin.Latin1(id));
            buf.U32((uint)body.Length, big);
            buf.Add(body);
        }
        buf.Add(Bin.FromHex(J.S(doc["tail"])));
        return buf.ToArray();
    }

    public static Dictionary<uint, string> EventNames(IEnumerable<string> names)
    {
        Dictionary<uint, string> table = [];
        foreach (string n in names)
        {
            if (n.All(c => c <= 0xFF))
            {
                table[Hashes.WwiseId(n)] = n;
            }
        }
        return table;
    }
}
