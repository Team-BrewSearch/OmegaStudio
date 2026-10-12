using Omega.Plugins;
namespace Virtuos.Formats.Nodes;
internal readonly record struct DwkParts(IAssetNode Gda, IAssetNode Gdf, IAssetNode Gdt);
public sealed class DwkContainerRecognizer : IContainerRecognizer
{
    public string Id => "virtuos.gda.container";
    public IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(host);
        Dictionary<string, DwkParts> triples = Gather(children);
        if (triples.Count == 0)
        {
            return null;
        }
        List<byte[]> databases = [.. children
            .Where(child => Extension(child) == ".rdf" && child is IBinaryAsset)
            .Select(child => ((IBinaryAsset)child).Data.ToArray())];
        List<IAssetNode> rows = [];
        foreach (IAssetNode child in children)
        {
            if (!triples.TryGetValue(Stem(child), out DwkParts parts) || !IsPart(parts, child))
            {
                rows.Add(child);
                continue;
            }
            if (ReferenceEquals(child, parts.Gda))
            {
                rows.Add(Open(parts, databases, host) ?? child);
            }
        }
        return rows;
    }
    private static IAssetNode? Open(DwkParts parts, IReadOnlyList<byte[]> databases, IHostServices host)
    {
        try
        {
            DwkArchive archive = DwkArchive.Load(Bytes(parts.Gda), Bytes(parts.Gdf), Bytes(parts.Gdt));
            VirtuosResourceIndex? resources = VirtuosResourceIndex.Load(databases, archive.Entries.Count);
            bool writable = Writable(parts.Gda) && Writable(parts.Gdt);
            if (writable)
            {
                _ = new NestedDwk(archive, parts);
            }
            host.Log($"{parts.Gda.Name}: {archive.Entries.Count} entries, " +
                     (resources is null
                         ? "no resource database found."
                         : $"{resources.DatabaseCount} resource database(s).") +
                     (writable ? string.Empty : " Read-only: the rows it is made of cannot be written back."));
            return DwkTree.Build(new TrackContext(archive, resources), parts.Gda.Name);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
        {
            host.Log($"{parts.Gda.Name} looks like a Virtuos archive but did not open: {ex.Message}");
            return null;
        }
    }
    private static Dictionary<string, DwkParts> Gather(IReadOnlyList<IAssetNode> children)
    {
        Dictionary<string, IAssetNode> data = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IAssetNode> names = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IAssetNode> tables = new(StringComparer.OrdinalIgnoreCase);
        foreach (IAssetNode child in children)
        {
            if (child is not IBinaryAsset)
            {
                continue;
            }
            Dictionary<string, IAssetNode>? bucket = Extension(child) switch
            {
                ".gda" => data,
                ".gdf" => names,
                ".gdt" => tables,
                _ => null,
            };
            _ = bucket?.TryAdd(Stem(child), child);
        }
        Dictionary<string, DwkParts> triples = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string stem, IAssetNode gda) in data)
        {
            if (names.TryGetValue(stem, out IAssetNode? gdf) && tables.TryGetValue(stem, out IAssetNode? gdt))
            {
                triples[stem] = new DwkParts(gda, gdf, gdt);
            }
        }
        return triples;
    }
    private static bool IsPart(DwkParts parts, IAssetNode child)
    {
        return ReferenceEquals(child, parts.Gda)
            || ReferenceEquals(child, parts.Gdf)
            || ReferenceEquals(child, parts.Gdt);
    }
    private static bool Writable(IAssetNode node)
    {
        return node is IBinaryAsset { CanReplaceBytes: true };
    }
    private static byte[] Bytes(IAssetNode node)
    {
        return node is EntryNode entry && DataOwned(entry.Entry)
            ? entry.Entry.AsArray()
            : ((IBinaryAsset)node).Data.ToArray();
    }
    private static bool DataOwned(DwkEntry entry)
    {
        try
        {
            byte[] array = entry.AsArray();
            return array.Length == entry.Size;
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            return false;
        }
    }
    private static string Extension(IAssetNode node)
    {
        return Path.GetExtension(node.Name);
    }
    private static string Stem(IAssetNode node)
    {
        return Path.GetFileNameWithoutExtension(node.Name);
    }
}
internal sealed class NestedDwk
{
    private readonly DwkArchive _archive;
    private readonly IBinaryAsset _gda;
    private readonly IBinaryAsset _gdt;
    public NestedDwk(DwkArchive archive, DwkParts parts)
    {
        _archive = archive;
        _gda = (IBinaryAsset)parts.Gda;
        _gdt = (IBinaryAsset)parts.Gdt;
        _archive.Changed += Flush;
    }
    private void Flush()
    {
        (byte[] gda, byte[] gdt) = _archive.Pack();
        _gda.ReplaceBytes(gda);
        _gdt.ReplaceBytes(gdt);
    }
}
