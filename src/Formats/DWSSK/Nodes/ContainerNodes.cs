using System.Globalization;
using System.Text;
using DWSSK.Formats.Audio;
using DWSSK.Formats.Core;
using DWSSK.Formats.Data;
using DWSSK.Formats.Models;
using DWSSK.Formats.Textures;
using DWSSK.Formats.Wad;
using Omega.Plugins;

namespace DWSSK.Formats.Nodes;

internal static class NodeFactory
{
    public static IAssetNode Create(Slot slot, string name, string path)
    {
        if (slot is WadSlot { Entry.IsAlias: true } alias)
        {
            return new AliasNode(alias, name, path);
        }
        string lower = slot.Name.ToLowerInvariant();
        if (GdFile.IsGd(lower))
        {
            return new GdNode(slot, name, path);
        }
        if (DocCodec.Of(lower) is { } codec)
        {
            return ReferenceEquals(codec, DocCodec.Lcl) ? new LclNode(slot, name, path)
                : codec.Obj is not null ? new MeshDocNode(slot, name, path, codec)
                : new DocNode(slot, name, path, codec);
        }
        return Path.GetExtension(lower) switch
        {
            ".htx" => new HtxNode(slot, name, path),
            ".animpack" => new AnimPackNode(slot, name, path),
            ".tpl" => new TplFileNode(slot, name, path),
            ".arc" => new ArcNode(slot, name, path),
            ".swf" => new SwfNode(slot, name, path),
            ".bnk" => new BankNode(slot, name, path),
            ".wem" => new WemNode(slot, name, path),
            ".bik" => new BinkNode(slot, name, path),
            _ => new RawNode(slot, name, path),
        };
    }

    public static IEnumerable<IAssetNode> Tree<T>(IEnumerable<T> slots, Func<T, string> pathOf, string root)
        where T : Slot
    {
        return PathTree.Build(slots, pathOf, (n, p, s) => Create(s, n, p), root).GetChildren();
    }

    public static string Inside(string dir, string relative)
    {
        string root = Path.GetFullPath(dir);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new InvalidDataException($"'{relative}' points outside {dir}");
    }

    public static void Save(string dir, string relative, byte[] data)
    {
        string full = Inside(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
    }

    public static byte[] Utf8(string text)
    {
        return new UTF8Encoding(false).GetBytes(text);
    }
}

internal sealed class WadNode : AssetNode, IDescribable, ICommandSource
{
    public WadNode(WadSession session, string name)
        : base(name, string.Empty, AssetKinds.Container, Describe(session.Archive))
    {
        Session = session;
        ChildFactory = () => NodeFactory.Tree(Session.Slots, s => s.File, name);
    }

    public WadSession Session { get; }
    private WadArchive Archive => Session.Archive;

    private static string Describe(WadArchive a)
    {
        return $"{a.Entries.Count} entries, {a.Platform.Label()}";
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Platform", Archive.Platform.Label());
        yield return PropertyRow.Of("Entries", Archive.Entries.Count);
        yield return PropertyRow.Of("Aliases", Archive.Entries.Count(e => e.IsAlias));
        yield return PropertyRow.Of("Data", Sizes.Describe(Archive.Entries.Sum(e => (long)e.Data.Length)));
        yield return PropertyRow.Of("Alignment", $"0x{Archive.Alignment:X}");
        yield return PropertyRow.Of("Image CRC", $"{Archive.Crc:X8}");
        yield return PropertyRow.Of("Path prefix", Archive.PathPrefix());
        yield return PropertyRow.Of("Compression", "TJZIP (LZ77), rebuilt byte for byte when nothing changed");
        yield return PropertyRow.Of("Writable", Session.Writable ? "yes" : "no: the container it sits in cannot be written back");
        yield return PropertyRow.Of("Unsaved changes", Archive.IsDirty ? "yes" : "no");
    }

    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Extract all",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("WAD manifest (files go beside it)", "_manifest.json")],
            SuggestedFileName = "_manifest.json",
            Execute = context =>
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                int n = 0;
                foreach (WadEntry e in Archive.Entries.Where(e => !e.IsAlias))
                {
                    NodeFactory.Save(dir, e.File, e.Data);
                    n++;
                }
                AtomicSave.Write(context.Path!, NodeFactory.Utf8(PyJson.Dumps(Archive.Manifest(), 1)));
                context.Log($"Extracted {n} files from {Name} to {dir}, with {Path.GetFileName(context.Path!)} for rebuilding.");
            },
        };
        if (!Session.Writable)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Rebuild from folder",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("WAD manifest", "_manifest.json", "*.json")],
            Execute = Rebuild,
        };
    }

    private void Rebuild(AssetCommandContext context)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
        JObj man = PyJson.Loads(File.ReadAllBytes(context.Path!)) as JObj ?? throw new InvalidDataException("the manifest is not a JSON object");
        if (Platforms.Parse(J.S(man["platform"])) != Archive.Platform || J.U(man["alignment"]) != Archive.Alignment)
        {
            throw new InvalidDataException($"the manifest is for {J.S(man["platform"])} with alignment {man["alignment"]}, this container is {Archive.Platform.Name()} with 0x{Archive.Alignment:X}");
        }
        byte[] Read(string f)
        {
            return File.ReadAllBytes(NodeFactory.Inside(dir, f));
        }
        WadArchive fresh = WadArchive.FromManifest(man, Read);
        HashSet<string> listed = [.. fresh.Entries.Select(e => e.File)];
        List<WadEntry> entries = [.. fresh.Entries];
        List<string> added = [];
        foreach (string rel in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(f) != "_manifest.json")
                     .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
                     .OrderBy(r => r.Replace('/', '\0'), StringComparer.Ordinal))
        {
            if (listed.Contains(rel))
            {
                continue;
            }
            entries.Add(fresh.NewEntry(rel, Read(rel)));
            added.Add(rel);
        }
        Dictionary<string, WadEntry> old = [];
        foreach (WadEntry e in Archive.Entries)
        {
            _ = old.TryAdd(e.File, e);
        }
        int changed = entries.Count(e => !e.IsAlias && (!old.TryGetValue(e.File, out WadEntry? o) || !o.Data.AsSpan().SequenceEqual(e.Data)));
        Archive.ReplaceAll(entries);
        context.Log($"Rebuilt {Name} from {dir}: {entries.Count} entries, {changed} new or changed{(added.Count > 0 ? $", added {string.Join(", ", added)}" : string.Empty)}. Use File ▸ Save to write it back.");
        context.Changed = SlotNode.Everything;
    }
}

internal sealed class ArcNode : SlotNode
{
    public ArcNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Container, Label(slot))
    {
        ChildFactory = () =>
        {
            (List<U8Node> nodes, _, _) = U8.Read(Slot.Data);
            List<ChildSlot> files = [.. nodes.Where(n => n.Type == 0).Select(n => Member(n))];
            return NodeFactory.Tree(files, s => s.Inner, Name);
        };
    }

    private static string Label(Slot slot)
    {
        try
        {
            int n = U8.Read(slot.Data).Nodes.Count(x => x.Type == 0);
            return $"U8, {n} file{(n == 1 ? "" : "s")}";
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            return Sizes.Describe(slot.Data.Length);
        }
    }

    protected override string DescribeSize()
    {
        return Label(Slot);
    }

    private ChildSlot Member(U8Node n)
    {
        string path = n.Path;
        return new ChildSlot(Slot, path, () => Bin.Slice(Slot.Data, (int)n.A, (int)(n.A + n.C)).ToArray(), data =>
        {
            byte[] b = Slot.Data;
            Dictionary<string, U8Node> files = [];
            foreach (U8Node x in U8.Read(b).Nodes.Where(x => x.Type == 0))
            {
                _ = files.TryAdd(x.Path, x);
            }
            return U8.Pack(U8.Manifest(b), p => p == path ? data : Bin.Slice(b, (int)files[p].A, (int)(files[p].A + files[p].C)).ToArray());
        });
    }

    protected override void Check(byte[] payload)
    {
        _ = U8.Read(payload);
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        (List<U8Node> nodes, uint data, uint size) = U8.Read(Slot.Data);
        yield return PropertyRow.Of("Format", "U8 archive (Wii)");
        yield return PropertyRow.Of("Files", nodes.Count(n => n.Type == 0));
        yield return PropertyRow.Of("Folders", nodes.Count(n => n.Type == 1));
        yield return PropertyRow.Of("Data offset", $"0x{data:X}");
        yield return PropertyRow.Of("Header size", $"0x{size:X}");
        yield return PropertyRow.Of("Repacking", "in place: a file keeps its size and offset");
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }

    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Extract all",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("U8 manifest (files go beside it)", "_u8.json")],
            SuggestedFileName = "_u8.json",
            Execute = context =>
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                byte[] b = Slot.Data;
                (List<U8Node> nodes, _, _) = U8.Read(b);
                foreach (U8Node n in nodes)
                {
                    if (n.Type == 1)
                    {
                        _ = Directory.CreateDirectory(NodeFactory.Inside(dir, n.Path.Length == 0 ? "." : n.Path));
                    }
                    else
                    {
                        NodeFactory.Save(dir, n.Path, Bin.Slice(b, (int)n.A, (int)(n.A + n.C)).ToArray());
                    }
                }
                AtomicSave.Write(context.Path!, NodeFactory.Utf8(PyJson.Dumps(U8.Manifest(b), 1)));
                context.Log($"Extracted {nodes.Count(n => n.Type == 0)} files from {Slot.File} to {dir}.");
            },
        };
        if (!Slot.CanWrite)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Rebuild from folder",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("U8 manifest", "_u8.json", "*.json")],
            Execute = context =>
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                JObj man = PyJson.Loads(File.ReadAllBytes(context.Path!)) as JObj ?? throw new InvalidDataException("the manifest is not a JSON object");
                byte[] rebuilt = U8.Pack(man, p => File.ReadAllBytes(NodeFactory.Inside(dir, p)));
                bool same = rebuilt.AsSpan().SequenceEqual(Slot.Data);
                if (!same)
                {
                    Store(rebuilt);
                }
                context.Log(same ? $"{dir} rebuilds {Slot.File} byte for byte; nothing changed." : $"Rebuilt {Slot.File} from {dir}. Use File ▸ Save to write it back.");
                context.Changed = same ? AssetChange.None : Everything;
            },
        };
    }
}

internal sealed class TplFileNode : SlotNode
{
    public TplFileNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Container)
    {
        ChildFactory = () => Enumerable.Range(0, Tpl.Images(Slot.Data).Count)
            .Select(k => (IAssetNode)new TplNode(() => Slot.Data, Slot.CanWrite ? ReplaceBytes : null, k, $"{Slot.Name}.{k}", $"{PathText}/{k}"));
    }

    protected override void Check(byte[] payload)
    {
        _ = Tpl.Images(payload);
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        List<TplImage> images = Tpl.Images(Slot.Data);
        yield return PropertyRow.Of("Format", "TPL texture palette (GX)");
        yield return PropertyRow.Of("Images", images.Count);
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}

internal sealed class SwfNode : SlotNode
{
    private (int Revision, byte[] Fws)? _fws;

    public SwfNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Container)
    {
        ChildFactory = Images;
    }

    private byte[] Fws
    {
        get
        {
            if (_fws is not { } c || c.Revision != Slot.Revision)
            {
                _fws = c = (Slot.Revision, Swf.Decompress(Slot.Data));
            }
            return c.Fws;
        }
    }

    private IEnumerable<IAssetNode> Images()
    {
        byte[] fws = Fws;
        foreach (SwfTag t in Swf.Tags(fws).Tags)
        {
            if (Swf.Image(t, fws) is { } image)
            {
                string label = $"{image.Id}.{image.Kind}";
                yield return new SwfImageNode(this, image.Id, label, $"{PathText}/images/{label}", Swf.TagName(t.Code));
            }
        }
    }

    private SwfTag Tag(int id)
    {
        byte[] fws = Fws;
        return Swf.Tags(fws).Tags.FirstOrDefault(t => Swf.IsImage(t) && Swf.ImageId(t, fws) == id)
            ?? throw new InvalidDataException($"{Slot.Name} no longer holds image {id}");
    }

    public ImageData DecodeImage(int id, string label)
    {
        return (Swf.Image(Tag(id), Fws) ?? throw new InvalidDataException($"image {id} is in a bitmap format this reader does not decode"))
            .Decode(b => Host.Images.Load(b, label));
    }

    public void ReplaceImage(int id, ImageData image)
    {
        byte[] fws = Swf.ReplaceImage(Fws, Tag(id), image, im => Host.Images.Encode(im, ".jpg"));
        Store(Slot.Data.AsSpan(0, 3).SequenceEqual("CWS"u8) ? Swf.Compress(fws) : fws);
    }

    protected override void Check(byte[] payload)
    {
        _ = Swf.Tags(Swf.Decompress(payload));
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        JObj info = Swf.Info(Fws);
        yield return PropertyRow.Of("Format", Slot.Data.AsSpan(0, 3).SequenceEqual("CWS"u8) ? "SWF (zlib, CWS)" : "SWF (uncompressed, FWS)");
        yield return PropertyRow.Of("Version", info["version"]);
        yield return PropertyRow.Of("Frame rate", Py.Repr(J.D(info["frame_rate"])));
        yield return PropertyRow.Of("Frames", info["frames"]);
        yield return PropertyRow.Of("Tags", J.A(info["tags"]).Count);
        yield return PropertyRow.Of("Exports", J.O(info["exports"]).Count);
        yield return PropertyRow.Of("External images", J.A(info["external_images"]).Count);
        yield return PropertyRow.Of("Fonts", string.Join(", ", J.A(info["fonts"]).Select(f => J.S(J.O(f)["name"]))));
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }

    public override IEnumerable<AssetCommand> GetCommands()
    {
        string stem = Path.GetFileNameWithoutExtension(Slot.Name);
        yield return ExportText("Export JSON", "JSON document", "*.json", stem + ".json", () => PyJson.Dumps(Swf.Info(Fws), 1));
        yield return new AssetCommand
        {
            Title = "Export uncompressed SWF",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("Uncompressed SWF", "*.swf")],
            SuggestedFileName = stem + ".fws.swf",
            Execute = context =>
            {
                AtomicSave.Write(context.Path!, Fws);
                context.Log($"Wrote {Path.GetFileName(context.Path!)} ({Sizes.Describe(Fws.Length)}).");
            },
        };
        yield return new AssetCommand
        {
            Title = "Extract all",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("SWF description (files go beside it)", "*.json")],
            SuggestedFileName = stem + ".json",
            Execute = context => Extract(context, stem),
        };
    }

    private void Extract(AssetCommandContext context, string stem)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
        byte[] fws = Fws;
        NodeFactory.Save(dir, stem + ".fws.swf", fws);
        int n = 0;
        foreach (SwfTag t in Swf.Tags(fws).Tags)
        {
            byte[] body = Bin.Slice(fws, t.Offset, t.Offset + t.Length).ToArray();
            if (t.Code is 21 or 35)
            {
                int cid = Bin.U16(body, 0, false);
                byte[] jpg = t.Code == 21 ? body[2..] : Bin.Slice(body, 6, 6 + (int)Bin.U32(body, 2, false)).ToArray();
                NodeFactory.Save(dir, $"images/{cid}.jpg", jpg);
                n++;
            }
            if ((t.Code is 20 or 35 or 36) && Swf.Image(t, fws) is { } image)
            {
                ImageData im = image.Decode(b => Host.Images.Load(b, image.Id.ToString(CultureInfo.InvariantCulture)));
                if (t.Code != 35 || im.FormatLabel == "JPEG + alpha")
                {
                    NodeFactory.Save(dir, $"images/{image.Id}.png", Host.Images.EncodePng(im));
                    n++;
                }
            }
        }
        AtomicSave.Write(context.Path!, NodeFactory.Utf8(PyJson.Dumps(Swf.Info(fws), 1)));
        context.Log($"Extracted {Slot.Name} to {dir}: {stem}.fws.swf, {Path.GetFileName(context.Path!)} and {n} image(s).");
    }
}

internal sealed class SwfImageNode(SwfNode movie, int id, string name, string path, string tag)
    : AssetNode(name, path, AssetKinds.Texture, tag), IImageAsset, IDescribable
{
    private (int Revision, ImageData Image)? _image;

    public ImageData Image
    {
        get
        {
            if (_image is not { } c || c.Revision != movie.Slot.Revision)
            {
                _image = c = (movie.Slot.Revision, movie.DecodeImage(id, Name));
            }
            return c.Image;
        }
    }

    public bool CanReplaceImage => movie.Slot.CanWrite;

    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);
        movie.ReplaceImage(id, image);
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Character id", id);
        yield return PropertyRow.Of("Tag", Detail);
        yield return PropertyRow.Of("Size", $"{Image.Width} x {Image.Height}");
        yield return PropertyRow.Of("Stored as", Image.FormatLabel);
        yield return PropertyRow.Of("Replace writes", Detail switch
        {
            "DefineBitsJPEG2" => "JPEG",
            "DefineBitsJPEG3" => "JPEG with a zlib alpha plane",
            _ => "zlib ARGB, colors kept as given",
        });
    }
}

internal sealed class BankNode : SlotNode
{
    private (int Revision, JObj Doc, Dictionary<long, byte[]> Media)? _unpacked;

    public BankNode(Slot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Container)
    {
        ChildFactory = () =>
        {
            Dictionary<long, byte[]> media = Unpacked().Media;
            return media.Keys.Select(id => (IAssetNode)new WemNode(MediaSlot(id), $"{id}.wem", $"{PathText}/media/{id}.wem"));
        };
    }

    private Dictionary<uint, string> Names()
    {
        return Bnk.EventNames(NameBook.Section("event").Concat(Group.LevelNames()));
    }

    private (JObj Doc, Dictionary<long, byte[]> Media) Unpacked()
    {
        if (_unpacked is not { } c || c.Revision != Slot.Revision)
        {
            (JObj doc, Dictionary<long, byte[]> media) = Bnk.Unpack(Slot.Data, Names());
            _unpacked = c = (Slot.Revision, doc, media);
        }
        return (c.Doc, c.Media);
    }

    private ChildSlot MediaSlot(long id)
    {
        return new ChildSlot(Slot, $"media/{id}.wem", () => Unpacked().Media[id], data =>
        {
            (JObj doc, Dictionary<long, byte[]> media) = Bnk.Unpack(Slot.Data, Names());
            media[id] = data;
            return Bnk.Pack(doc, media);
        });
    }

    protected override void Check(byte[] payload)
    {
        _ = Bnk.Unpack(payload, null);
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        (JObj doc, Dictionary<long, byte[]> media) = Unpacked();
        List<JObj> chunks = [.. J.A(doc["chunks"]).Select(J.O)];
        yield return PropertyRow.Of("Format", "Wwise sound bank");
        yield return PropertyRow.Of("Byte order", J.S(doc["endian"]));
        yield return PropertyRow.Of("Chunks", string.Join(", ", chunks.Select(c => J.S(c["id"]))));
        if (chunks.FirstOrDefault(c => J.S(c["id"]) == "BKHD") is { } bkhd)
        {
            foreach (KeyValuePair<string, object?> f in bkhd)
            {
                if (f.Key != "id" && f.Value is long or string)
                {
                    yield return PropertyRow.Of($"BKHD {f.Key.Replace('_', ' ')}", f.Value is long l ? l.ToString(CultureInfo.InvariantCulture) : f.Value);
                }
            }
        }
        yield return PropertyRow.Of("Embedded media", media.Count);
        if (chunks.FirstOrDefault(c => J.S(c["id"]) == "HIRC") is { } hirc)
        {
            List<JObj> objects = [.. J.A(hirc["objects"]).Select(J.O)];
            yield return PropertyRow.Of("HIRC objects", string.Join(", ", objects.GroupBy(o => J.S(o["kind"])).Select(g => $"{g.Count()} {g.Key}")));
            foreach (JObj e in objects.Where(o => J.L(o["type"]) == 4))
            {
                yield return PropertyRow.Of($"Event {J.L(e["id"])}", e.Get("name") as string ?? "(name not recovered)");
            }
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }

    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return ExportText("Export JSON", "JSON document", "*.json", "bank.json", () => PyJson.Dumps(Unpacked().Doc, 1));
        yield return new AssetCommand
        {
            Title = "Extract all",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("Bank description (media go beside it)", "bank.json", "*.json")],
            SuggestedFileName = "bank.json",
            Execute = context =>
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                (JObj doc, Dictionary<long, byte[]> media) = Unpacked();
                foreach ((long id, byte[] data) in media)
                {
                    NodeFactory.Save(dir, $"media/{id}.wem", data);
                }
                AtomicSave.Write(context.Path!, NodeFactory.Utf8(PyJson.Dumps(doc, 1)));
                context.Log($"Extracted {Slot.Name} to {dir}: {Path.GetFileName(context.Path!)} and {media.Count} media file(s).");
            },
        };
        if (!Slot.CanWrite)
        {
            yield break;
        }
        yield return new AssetCommand
        {
            Title = "Rebuild from folder",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("Bank description", "bank.json", "*.json")],
            Execute = context =>
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                JObj doc = PyJson.Loads(File.ReadAllBytes(context.Path!)) as JObj ?? throw new InvalidDataException("bank.json is not a JSON object");
                Dictionary<long, byte[]> media = [];
                foreach (JObj c in J.A(doc["chunks"]).Select(J.O).Where(c => J.S(c["id"]) == "DIDX"))
                {
                    foreach (JObj m in J.A(c["media"]).Select(J.O))
                    {
                        long id = J.L(m["id"]);
                        media[id] = File.ReadAllBytes(NodeFactory.Inside(dir, $"media/{id}.wem"));
                    }
                }
                byte[] rebuilt = Bnk.Pack(doc, media);
                bool same = rebuilt.AsSpan().SequenceEqual(Slot.Data);
                if (!same)
                {
                    Store(rebuilt);
                }
                context.Log(same ? $"{dir} rebuilds {Slot.File} byte for byte; nothing changed." : $"Rebuilt {Slot.File} from {dir}. Use File ▸ Save to write it back.");
                context.Changed = same ? AssetChange.None : Everything;
            },
        };
    }
}
