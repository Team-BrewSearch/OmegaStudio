using Kartz.Formats.Gltf;
using Kartz.Formats.Scene;
using Omega.Plugins;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Kartz.Formats.Nodes;

/// <summary>
/// A node backed by one entry in the archive.
///
/// <para>
/// <see cref="BinaryAssetNode"/> supplies the whole of <see cref="IBinaryAsset"/>, so
/// every kind of entry — track, texture, or something nobody has decoded — gets raw
/// extract and replace from this handful of lines. For an archive of 1,400 files that
/// is most of what a session actually does.
/// </para>
/// </summary>
internal abstract class EntryNode : BinaryAssetNode
{
    protected EntryNode(
        TrackContext context, string name, string path, DwkEntry entry,
        string kind = AssetKinds.Auto, string? detail = null)
        : base(name, path, kind, detail ?? Sizes.Describe(entry.Size))
    {
        Context = context;
        Entry = entry;
    }

    protected TrackContext Context { get; }

    protected DwkEntry Entry { get; }

    public override ReadOnlySpan<byte> Data => Entry.Span;

    public override int Size => Entry.Size;

    public override string Extension =>
        Path.GetExtension(Entry.Name) is { Length: > 1 } found ? found : ".bin";

    protected override void Write(byte[] payload)
    {
        Entry.Replace(payload);
    }

    /// <summary>Location rows every entry-backed node shares.</summary>
    protected IEnumerable<PropertyRow> EntryRows()
    {
        yield return PropertyRow.Of("File", Entry.Name);
        yield return PropertyRow.Of("GDF path", Entry.GdfPath);
        yield return PropertyRow.Of("Index", Entry.Index);
    }
}

/// <summary>An entry nothing recognised. Still extractable and replaceable.</summary>
internal sealed class RawNode : EntryNode, IDescribable
{
    public RawNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }

    public IEnumerable<PropertyRow> Describe()
    {
        return EntryRows();
    }
}

/// <summary>A <c>5TX</c> texture.</summary>
internal sealed class TextureNode : EntryNode, IImageAsset, IDescribable
{
    private SskTexture _texture;

    private TextureNode(TrackContext context, string name, string path, DwkEntry entry, SskTexture texture)
        : base(context, name, path, entry, detail: $"{texture.Width}x{texture.Height}")
    {
        _texture = texture;
    }

    /// <summary>
    /// Builds the node, or returns null if the blob only looked like a texture.
    ///
    /// <para>
    /// A factory rather than a throwing constructor because the caller's answer to a
    /// failed parse is to fall back to a raw node, not to abandon the row — and one
    /// undecodable entry in an archive of 1400 should cost exactly itself.
    /// </para>
    /// </summary>
    public static TextureNode? TryCreate(TrackContext context, string name, string path, DwkEntry entry)
    {
        try
        {
            SskTexture texture = SskTexture.Parse(entry.Span, entry.Name);

            // A header that parses but yields no pixels is not something to show in the
            // Texture pane; as its own entry it is better presented as the raw blob it
            // is. Inside a track it still has to occupy its slot — see TrackScene.ToImage.
            return TrackScene.IsUsable(texture)
                ? new TextureNode(context, name, path, entry, texture)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public ImageData Image => TrackScene.ToImage(_texture);

    public bool CanReplaceImage => true;

    /// <summary>
    /// Re-encodes the image back to <c>5TX</c> in whatever format the original used,
    /// and mutates the live texture so every pane holding it sees the new image.
    /// </summary>
    public void ReplaceImage(ImageData image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] encoded = SskTextureEncoder.Encode(_texture, image.Rgba, image.Width, image.Height);
        Entry.Replace(encoded);
        _texture.SetPixels(image.Width, image.Height, image.Rgba);
        Detail = $"{_texture.Width}x{_texture.Height}";
    }

    protected override void Write(byte[] payload)
    {
        // Re-parsed rather than kept: the replacement may be a different size or a different
        // 5TX pixel format, and a stale decode would keep showing the image that is no longer
        // there. Parsed *before* the commit, because it may also not be a texture at all —
        // parsing afterwards left the entry replaced and the archive dirty while this node
        // still described the old image.
        SskTexture parsed = SskTexture.Parse(payload, Entry.Name);
        base.Write(payload);
        _texture = parsed;
    }

    protected override string DescribeSize()
    {
        return $"{_texture.Width}x{_texture.Height}";
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Size", $"{_texture.Width} x {_texture.Height}");
        yield return PropertyRow.Of("Format", $"5TX fmt{_texture.Format}");
        yield return PropertyRow.Of("Transparency", _texture.HasTransparency ? "yes" : "no");

        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}

/// <summary>
/// A <c>TRK</c> track: the DS display list that draws a whole course.
///
/// <para>
/// Decoded on first use rather than when the row is built. The archive holds
/// hundreds of entries and expanding a folder builds a node for every one of them;
/// decoding a display list eagerly there would stall the Outliner on tracks nobody
/// clicked.
/// </para>
/// </summary>
internal sealed class TrackNode : EntryNode, ISceneAsset, IDescribable, ICommandSource
{
    private SceneGeometry? _scene;

    // No kind of its own: it is an ISceneAsset, so the host derives Model and the
    // dot is the same colour as a model from any other game.
    public TrackNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }

    [AllowNull]
    private SskTrack Track { get => field ??= Context.ParseTrack(Entry); set; }

    /// <summary>
    /// The track's geometry, or null when there is nothing to draw.
    ///
    /// <para>
    /// Null is a real answer here, not a failure. The display-list signature that
    /// identifies a track also matches a few blobs that are not one — the skeleton
    /// files beside the character models decode cleanly and produce no triangles at
    /// all — and returning an empty scene for those would give them a Viewport tab
    /// showing nothing, which reads as a broken viewer rather than as a file with no
    /// geometry in it.
    /// </para>
    /// </summary>
    public SceneGeometry? GetScene()
    {
        _scene ??= TrackScene.Build(Track);
        return _scene.TriangleCount > 0 ? _scene : null;
    }

    protected override void Write(byte[] payload)
    {
        base.Write(payload);
        Invalidate();
    }

    private void Invalidate()
    {
        Track = null;
        _scene = null;
    }

    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace Track from glTF…",
            Kind = AssetCommandKind.OpenFile,
            Filter = new FileFilter("glTF model", "*.glb", "*.gltf"),
            Execute = Import,
        };
    }

    /// <summary>
    /// Rebuilds the track's display list from a glTF file.
    ///
    /// <para>
    /// The existing track is the template: its chunk count, chunk positions, texture
    /// slot table and tail sections are all kept, and only the geometry is replaced.
    /// That is not conservatism for its own sake — the tail sections the writer copies
    /// still describe the original chunk count internally, so a conversion that
    /// re-derived its own layout produced a structurally inconsistent file that
    /// crashed every emulator it was tested on.
    /// </para>
    /// </summary>
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);

        // The triangle count comes back from the conversion rather than being recovered by
        // re-decoding the finished track: the log line used to force a second full display-list
        // parse of a track that had already been committed, purely to print a number.
        (byte[] bytes, bool matchedLayout, int triangles) = TrkImportRunner.ConvertToTrk(
            source, Entry.Span, Track.Textures, context.Log);

        ushort chunks = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x02, 2));

        Entry.Replace(bytes);
        Invalidate();
        Detail = Sizes.Describe(Entry.Size);

        string layout = matchedLayout
            ? "chunk count and positions were matched to the original track's layout, " +
              "so its paired .bpvs and tail data were left untouched"
            : ReplacePairedBpvs()
            ? "the original layout could not be read, so this fell back to a single chunk " +
              "and its paired .bpvs was replaced with a minimal matching version"
            : "the original layout could not be read, so this fell back to a single chunk, " +
              "with no paired .bpvs entry found to replace";

        context.Log(
            $"Converted {Path.GetFileName(context.Path!)} into {Entry.Name} " +
            $"({triangles} triangles across {chunks} chunks — {layout}). " +
            "Note: the paired .col was not regenerated and may be stale. " +
            "Use File ▸ Save to write it back.");

        context.Changed = AssetChange.Bytes | AssetChange.Scene
            | AssetChange.Properties | AssetChange.Dirty;
    }

    /// <summary>
    /// Replaces this track's sibling <c>.bpvs</c> with a minimal single-section one,
    /// to match a single-chunk conversion. Returns whether a sibling was found.
    /// </summary>
    private bool ReplacePairedBpvs()
    {
        // String surgery rather than Path.ChangeExtension: GDF paths are raw
        // backslash-separated strings, and running them through Path.* re-introduces
        // mixed separators on Windows.
        int dot = Entry.GdfPath.LastIndexOf('.');
        string bpvsPath = (dot >= 0 ? Entry.GdfPath[..dot] : Entry.GdfPath) + ".bpvs";

        DwkEntry? bpvs = Context.Archive.Entries.FirstOrDefault(
            e => string.Equals(e.GdfPath, bpvsPath, StringComparison.OrdinalIgnoreCase));

        if (bpvs is null)
        {
            return false;
        }

        bpvs.Replace(BpvsWriter.BuildMinimalSingleSection());
        return true;
    }

    public IEnumerable<PropertyRow> Describe()
    {
        SskTrack track = Track;

        yield return PropertyRow.Of("Vertices", track.Positions.Length / 3);
        yield return PropertyRow.Of("Triangles", track.Indices.Length / 3);
        yield return PropertyRow.Of("Draw sections", track.Sections.Length);
        yield return PropertyRow.Of("Chunks", track.ChunkCount);
        yield return PropertyRow.Of("Textures", track.Textures.Length);

        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
