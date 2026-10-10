using Omega.Plugins;
namespace Polarbit.Formats.Nodes;

/// <summary>What every row of one open archive shares: the archive, the host, and a decoded-texture cache for the scenes.</summary>
internal sealed class ArchiveContext(VfsArchive archive, IHostServices host)
{
    private readonly Dictionary<VfsEntry, ImageData?> _images = [];
    private Dictionary<string, List<VfsEntry>>? _byStem;
    private int _imagesRevision = -1;
    public VfsArchive Archive { get; } = archive;
    public IHostServices Host { get; } = host;

    /// <summary>A texture by the name a model, track or scene asks for: its full path when that hashes to an entry, else its file name under any folder (CNK2 asks for x.png and stores x.pvr).</summary>
    public ImageData? Texture(string name)
    {
        string path = name.Replace('\\', '/');
        VfsEntry? entry = Archive.Find(path) is { Kind: PayloadKind.Pvr or PayloadKind.Png } exact ? exact : null;
        if (entry is null)
        {
            _byStem ??= Archive.Entries
                .Where(e => e.Name is not null && e.Kind is PayloadKind.Pvr or PayloadKind.Png)
                .GroupBy(e => Path.GetFileNameWithoutExtension(e.Name!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            entry = _byStem.TryGetValue(Path.GetFileNameWithoutExtension(path), out List<VfsEntry>? found) ? found[0] : null;
        }
        return entry is null ? null : Decode(entry);
    }

    public ImageData? Decode(VfsEntry entry)
    {
        if (_imagesRevision != Archive.Revision)
        {
            _images.Clear();
            _imagesRevision = Archive.Revision;
        }
        if (!_images.TryGetValue(entry, out ImageData? image))
        {
            string name = entry.Name is null ? $"{entry.Hash:x8}" : Path.GetFileNameWithoutExtension(entry.Name);
            try
            {
                image = entry.Kind == PayloadKind.Pvr ? PvrImage(entry.Payload, name) : Host.Images.Load(entry.Payload, name);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or IOException)
            {
                Host.Log($"Texture {name} could not be decoded: {ex.Message}");
                image = null;
            }
            _images[entry] = image;
        }
        return image;
    }

    public static ImageData PvrImage(byte[] payload, string name)
    {
        PvrTexture texture = PvrTexture.Parse(payload);
        return ImageData.Create(name, texture.Width, texture.Height, texture.DecodeTop(payload), texture.FormatName);
    }
}

internal static class VfsTree
{
    public static IAssetNode Build(ArchiveContext context, string name)
    {
        int named = context.Archive.Entries.Count(e => e.Name is not null);
        return PathTree.Build(
            context.Archive.Entries,
            PathOf,
            (leaf, path, entry) => Node(context, leaf, path, entry),
            name,
            $"{context.Archive.Entries.Count} entries, {named} named");
    }

    private static string PathOf(VfsEntry entry)
    {
        return entry.Name ?? $"(unnamed)/{entry.Hash:x8}{Kinds.Extension(entry.Kind)}";
    }

    /// <summary>The richest row the payload supports, or a raw row when it does not parse.</summary>
    private static IAssetNode Node(ArchiveContext context, string name, string path, VfsEntry entry)
    {
        try
        {
            return entry.Kind switch
            {
                PayloadKind.Pvr => new PvrNode(context, name, path, entry),
                PayloadKind.Pfnt => new FontNode(context, name, path, entry),
                PayloadKind.Psnd => new SoundNode(context, name, path, entry),
                PayloadKind.Pmod => new ModuleNode(context, name, path, entry),
                PayloadKind.Lang => new LangNode(context, name, path, entry),
                PayloadKind.Fcol => new FcolNode(context, name, path, entry),
                PayloadKind.Tlpb or PayloadKind.Plt => new PlateNode(context, name, path, entry),
                PayloadKind.Piff when Piff.FileType(entry.Payload) == "3DOB" => new ModelNode(context, name, path, entry),
                PayloadKind.Piff => new TrackNode(context, name, path, entry),
                PayloadKind.Pxc => new SceneNode(context, name, path, entry),
                _ => new RawNode(context, name, path, entry),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or IOException)
        {
            context.Host.Log($"{path}: {ex.Message} Shown as raw bytes.");
            return new RawNode(context, name, path, entry);
        }
    }
}

internal abstract class EntryNode : BinaryAssetNode, IDescribable
{
    protected EntryNode(ArchiveContext context, string name, string path, VfsEntry entry, string kind = AssetKinds.Auto, string? detail = null)
        : base(name, path, kind, detail ?? Sizes.Describe(entry.Payload.Length))
    {
        Context = context;
        Entry = entry;
    }
    protected ArchiveContext Context { get; }
    protected VfsEntry Entry { get; }
    public override ReadOnlySpan<byte> Data => Entry.Payload;
    public override int Size => Entry.Payload.Length;
    public override string Extension => Entry.Kind != PayloadKind.Pvr && Path.GetExtension(Name) is { Length: > 1 } own ? own : Kinds.Extension(Entry.Kind);
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);

    /// <summary>A replacement must still parse as what the entry holds, so a wrong file is refused here rather than by the game.</summary>
    protected override void Write(byte[] payload)
    {
        Check(payload);
        Entry.Replace(payload);
    }

    protected virtual void Check(byte[] payload)
    {
    }

    public virtual IEnumerable<PropertyRow> Describe()
    {
        return EntryRows();
    }

    protected IEnumerable<PropertyRow> EntryRows()
    {
        yield return PropertyRow.Of("Type", Kinds.Describe(Entry.Kind));
        yield return PropertyRow.Of("Path", Entry.Name ?? "(not recovered)");
        yield return PropertyRow.Of("Name source", Entry.Name is null ? "none" : Entry.NameRecovered ? "recovered from its hash" : "stored in the archive");
        yield return PropertyRow.Of("Name hash", $"{Entry.Hash:X8}");
        yield return PropertyRow.Of("Storage", Entry.Layers.Count == 0 ? "uncompressed" : string.Join(" over ", Entry.Layers));
        yield return PropertyRow.Of("Stored size", Sizes.Describe(Entry.RawSize));
        yield return PropertyRow.Of("Payload size", Sizes.Describe(Entry.Payload.Length));
    }
}

/// <summary>Anything without a richer row: PNG (the Generic plugin's recogniser decorates it), XML, text, shaders, Thumbs.db.</summary>
internal sealed class RawNode(ArchiveContext context, string name, string path, VfsEntry entry)
    : EntryNode(context, name, path, entry, entry.Kind is PayloadKind.Xml or PayloadKind.Text ? AssetKinds.Text : AssetKinds.Auto);
