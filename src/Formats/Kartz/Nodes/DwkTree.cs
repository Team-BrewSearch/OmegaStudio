using Omega.Plugins;

namespace Kartz.Formats.Nodes;

/// <summary>
/// What every node in this archive shares: the archive itself, and the resource
/// databases that resolve a track's texture slots.
///
/// <para>
/// The texture table is built on first use rather than at open, because resolving it
/// reads every <c>.rdf</c> beside the archive and most sessions never open a track.
/// </para>
/// </summary>
internal sealed class TrackContext(DwkArchive archive, KartzResourceIndex? resources)
{
    private TrackTextureTable? _textures;

    public DwkArchive Archive { get; } = archive;

    /// <summary>
    /// Decodes a track and attaches the textures its chunk headers refer to.
    ///
    /// <para>
    /// Two passes, and not by accident: the slot order cannot be read off the TRK
    /// itself, so the track is parsed once untextured, the table is resolved against
    /// it, and the textures are attached afterwards — which also avoids decoding the
    /// display list twice.
    /// </para>
    /// </summary>
    public SskTrack ParseTrack(DwkEntry entry)
    {
        SskTrack track = SskTrack.Parse(entry.Span, Array.Empty<SskTexture>());
        _textures ??= new TrackTextureTable(Archive, resources);
        track.AttachTextureSlots(_textures.Resolve(entry, track));
        return track;
    }
}

/// <summary>
/// Builds the Outliner tree for an archive triple.
///
/// <para>
/// The GDF stores every entry as a full backslash-separated path with no directory
/// records, so the folders have to be invented — which <see cref="PathTree"/> does for
/// any archive shaped this way. What is left here is classifying one entry, and that
/// goes by content rather than by name: the GDF's extensions are not a reliable guide,
/// since several tracks and textures share one.
/// </para>
/// </summary>
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
