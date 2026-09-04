using Kartz.Formats.Gltf;
using Kartz.Formats.Scene;
using Omega.Plugins;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
namespace Kartz.Formats.Nodes;

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
    public override string FileStem => Path.GetFileNameWithoutExtension(Entry.Name);
    protected override void Write(byte[] payload)
    {
        Entry.Replace(payload);
    }
    protected IEnumerable<PropertyRow> EntryRows()
    {
        yield return PropertyRow.Of("File", Entry.Name);
        yield return PropertyRow.Of("GDF path", Entry.GdfPath);
        yield return PropertyRow.Of("Index", Entry.Index);
    }
}
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
internal sealed class TextureNode : EntryNode, IImageAsset, IDescribable
{
    private SskTexture _texture;
    private TextureNode(TrackContext context, string name, string path, DwkEntry entry, SskTexture texture)
        : base(context, name, path, entry, detail: $"{texture.Width}x{texture.Height}")
    {
        _texture = texture;
    }
    public static TextureNode? TryCreate(TrackContext context, string name, string path, DwkEntry entry)
    {
        try
        {
            SskTexture texture = SskTexture.Parse(entry.Span, entry.Name);
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
internal sealed class TrackNode : EntryNode, ISceneAsset, IDescribable, ICommandSource
{
    private SceneGeometry? _scene;
    private int _revision = -1;
    public TrackNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }
    [AllowNull]
    private SskTrack Track { get => field ??= Context.ParseTrack(Entry); set; }
    public SceneGeometry? GetScene()
    {
        DropStaleCache();
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
        _revision = Context.Archive.Revision;
    }
    private void DropStaleCache()
    {
        if (_revision != Context.Archive.Revision)
        {
            Invalidate();
        }
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace track from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
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
    private bool ReplacePairedBpvs()
    {
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
        DropStaleCache();
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
internal sealed class ZoneNode : EntryNode, ISceneAsset, IDescribable, ICommandSource
{
    private SceneGeometry? _scene;
    private int _revision = -1;
    public ZoneNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry, AssetKinds.Model)
    {
    }
    [AllowNull]
    private ZonModel Model { get => field ??= ZonModel.Parse(Entry.Span); set; }
    [AllowNull]
    private SskTrack Geometry { get => field ??= Context.ParseModel(Entry, Model.Build([])); set; }
    public SceneGeometry? GetScene()
    {
        DropStaleCache();
        _scene ??= TrackScene.Build(Geometry);
        return _scene.TriangleCount > 0 ? _scene : null;
    }
    protected override void Write(byte[] payload)
    {
        ZonModel parsed = ZonModel.Parse(payload);
        base.Write(payload);
        Invalidate();
        Model = parsed;
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace model from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
        (byte[] bytes, int triangles) = ZonWriter.Convert(source, Entry.Span, Geometry.Textures, context.Log);
        Entry.Replace(bytes);
        Invalidate();
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Converted {Path.GetFileName(context.Path!)} into {Entry.Name} " +
            $"({triangles} triangles). Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Scene
            | AssetChange.Properties | AssetChange.Dirty;
    }
    private void Invalidate()
    {
        Model = null;
        Geometry = null;
        _scene = null;
        _revision = Context.Archive.Revision;
    }
    private void DropStaleCache()
    {
        if (_revision != Context.Archive.Revision)
        {
            Invalidate();
        }
    }
    public IEnumerable<PropertyRow> Describe()
    {
        DropStaleCache();
        SskTrack geometry = Geometry;
        yield return PropertyRow.Of("Models", Model.ChunkOffsets.Count);
        yield return PropertyRow.Of("Vertices", geometry.Positions.Length / 3);
        yield return PropertyRow.Of("Triangles", geometry.Indices.Length / 3);
        yield return PropertyRow.Of("Draw sections", geometry.Sections.Length);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class CollisionNode : EntryNode, ISceneAsset, IDescribable, ICommandSource
{
    private SceneGeometry? _scene;
    private int _revision = -1;
    public CollisionNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry, AssetKinds.Mesh)
    {
    }
    [AllowNull]
    private ColModel Model { get => field ??= ColModel.Parse(Entry.Span); set; }
    public SceneGeometry? GetScene()
    {
        DropStaleCache();
        _scene ??= Model.ToScene(Entry.Name);
        return _scene.TriangleCount > 0 ? _scene : null;
    }
    protected override void Write(byte[] payload)
    {
        ColModel parsed = ColModel.Parse(payload);
        base.Write(payload);
        Invalidate();
        Model = parsed;
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace collision from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
        List<TrkImportPrimitive> primitives = TrkGltfReader.Read(source, [], context.Log);
        List<float> positions = [];
        List<float> normals = [];
        List<int> indices = [];
        foreach (TrkImportPrimitive primitive in primitives)
        {
            int first = positions.Count / 3;
            positions.AddRange(primitive.Positions);
            normals.AddRange(primitive.Normals.Length == primitive.Positions.Length
                ? primitive.Normals
                : new float[primitive.Positions.Length]);
            foreach (int index in primitive.Indices)
            {
                indices.Add(first + index);
            }
        }
        ColModel rebuilt = ColModel.Parse(Entry.Span);
        rebuilt.ApplyMesh(positions, normals, indices, context.Log);
        byte[] bytes = rebuilt.Serialize();
        Entry.Replace(bytes);
        Invalidate();
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Rebuilt {Entry.Name} from {Path.GetFileName(context.Path!)} " +
            $"({rebuilt.Sectors.Count} sectors, {positions.Count / 3} vertices). " +
            "Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Scene
            | AssetChange.Properties | AssetChange.Dirty;
    }
    private void Invalidate()
    {
        Model = null;
        _scene = null;
        _revision = Context.Archive.Revision;
    }
    private void DropStaleCache()
    {
        if (_revision != Context.Archive.Revision)
        {
            Invalidate();
        }
    }
    public IEnumerable<PropertyRow> Describe()
    {
        DropStaleCache();
        ColModel model = Model;
        int vertices = 0;
        foreach (ColSection section in model.Sections)
        {
            vertices += section.VertexCount;
        }
        yield return PropertyRow.Of("Sectors", model.Sectors.Count);
        yield return PropertyRow.Of("Cross sections", model.Sections.Count);
        yield return PropertyRow.Of("Section vertices", vertices);
        yield return PropertyRow.Of(
            "Surface triangles",
            model.SceneVertexCount == 0 ? 0 : model.ToScene("collision").TriangleCount);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class VisibilityNode : EntryNode, IDescribable
{
    private BpvsModel? _model;
    private int _revision = -1;
    public VisibilityNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }
    protected override void Write(byte[] payload)
    {
        base.Write(payload);
        _model = null;
        _revision = Context.Archive.Revision;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        if (_revision != Context.Archive.Revision)
        {
            _model = null;
            _revision = Context.Archive.Revision;
        }
        int chunks = Context.ChunkCountFor(Entry);
        if (chunks == 0)
        {
            yield return PropertyRow.Of("Track", "not found in this archive");
            foreach (PropertyRow row in EntryRows())
            {
                yield return row;
            }
            yield break;
        }
        _model ??= BpvsModel.Parse(Entry.Span, chunks);
        BpvsModel model = _model;
        int visible = 0;
        int least = int.MaxValue;
        int most = 0;
        foreach (BpvsSector sector in model.Sectors)
        {
            int drawn = sector.VisibleChunks;
            visible += drawn;
            least = Math.Min(least, drawn);
            most = Math.Max(most, drawn);
        }
        yield return PropertyRow.Of("Sectors", model.Sectors.Count);
        yield return PropertyRow.Of("Track chunks", chunks);
        yield return PropertyRow.Of(
            "Chunks drawn", model.Sectors.Count == 0
                ? "0"
                : $"{visible / model.Sectors.Count} on average, {least} to {most}");
        yield return PropertyRow.Of("Shared lists", model.SharedLists);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
