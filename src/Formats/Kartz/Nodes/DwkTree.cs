using Omega.Plugins;
namespace Kartz.Formats.Nodes;

internal sealed class TrackContext(DwkArchive archive, KartzResourceIndex? resources)
{
    private TrackTextureTable? _textures;
    public DwkArchive Archive { get; } = archive;
    public SskTrack ParseTrack(DwkEntry entry)
    {
        SskTrack track = SskTrack.Parse(entry.Span, Array.Empty<SskTexture>());
        _textures ??= new TrackTextureTable(Archive, resources);
        track.AttachTextureSlots(_textures.Resolve(entry, track));
        return track;
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
            _ => new RawNode(context, name, path, entry),
        };
    }
}
