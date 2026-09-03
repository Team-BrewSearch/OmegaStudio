using Omega.Plugins;
namespace Kartz.Formats.Nodes;

internal sealed class TrackContext(DwkArchive archive, KartzResourceIndex? resources)
{
    private TrackTextureTable? _textures;
    public DwkArchive Archive { get; } = archive;
    public int ChunkCountFor(DwkEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int dot = entry.GdfPath.LastIndexOf('.');
        string trackPath = (dot >= 0 ? entry.GdfPath[..dot] : entry.GdfPath) + ".trk";
        foreach (DwkEntry candidate in Archive.Entries)
        {
            if (string.Equals(candidate.GdfPath, trackPath, StringComparison.OrdinalIgnoreCase)
                && SskTrack.IsTrack(candidate.Span))
            {
                return Binary.BinaryRead.U16(candidate.Span, 0x02);
            }
        }
        return 0;
    }
    public SskTrack ParseTrack(DwkEntry entry)
    {
        SskTrack track = SskTrack.Parse(entry.Span, Array.Empty<SskTexture>());
        AttachTextures(entry, track);
        return track;
    }
    public SskTrack ParseZone(DwkEntry entry, ZonModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        SskTrack zone = model.Build([]);
        AttachTextures(entry, zone);
        return zone;
    }
    private void AttachTextures(DwkEntry entry, SskTrack geometry)
    {
        _textures ??= new TrackTextureTable(Archive, resources);
        geometry.AttachTextureSlots(_textures.Resolve(entry, geometry));
    }
}
internal static class DwkTree
{
    public static IAssetNode Build(TrackContext context, string name)
    {
        return PathTree.Build(
            context.Archive.Entries,
            entry => entry.NormalizedPath,
            (leafName, path, entry) => Node(context, leafName, path, entry),
            name);
    }
    private static IAssetNode Node(TrackContext context, string name, string path, DwkEntry entry)
    {
        return AssetTypes.Detect(entry.Span) switch
        {
            AssetKind.Texture => TextureNode.TryCreate(context, name, path, entry)
                ?? (IAssetNode)new RawNode(context, name, path, entry),
            AssetKind.Track => new TrackNode(context, name, path, entry),
            AssetKind.Zone => new ZoneNode(context, name, path, entry),
            AssetKind.Collision => new CollisionNode(context, name, path, entry),
            AssetKind.Visibility => new VisibilityNode(context, name, path, entry),
            _ => new RawNode(context, name, path, entry),
        };
    }
}
