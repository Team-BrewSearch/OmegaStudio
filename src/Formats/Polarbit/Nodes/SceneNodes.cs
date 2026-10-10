using Omega.Plugins;
namespace Polarbit.Formats.Nodes;

/// <summary>Base for rows that draw: the scene is rebuilt whenever anything in the archive changed, since textures live in other entries.</summary>
internal abstract class GeometryNode(ArchiveContext context, string name, string path, VfsEntry entry, string detail)
    : EntryNode(context, name, path, entry, AssetKinds.Model, detail), ISceneAsset
{
    private (int Revision, SceneGeometry Scene)? _scene;
    public SceneGeometry? GetScene()
    {
        if (_scene is not { } cached || cached.Revision != Context.Archive.Revision)
        {
            _scene = cached = (Context.Archive.Revision, Build(new TextureSet(Context.Texture)));
        }
        return cached.Scene.TriangleCount > 0 ? cached.Scene : null;
    }
    protected abstract SceneGeometry Build(TextureSet textures);

    /// <summary>Replace-from-glTF, the same shape for models and scenes: rewrite the payload, log what the import had to decide.</summary>
    protected AssetCommand ImportCommand(Func<byte[], Omega.Plugins.Gltf.GltfDocument, (byte[] Payload, List<string> Notes)> replace)
    {
        return new AssetCommand
        {
            Title = "Replace geometry from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.gltf", "*.glb")],
            Execute = context =>
            {
                (byte[] payload, List<string> notes) = replace(Entry.Payload, context.Host.Gltf.Read(context.Path!));
                Entry.Replace(payload);
                Detail = DescribeSize();
                SceneGeometry? scene = GetScene();
                context.Log($"Imported {Path.GetFileName(context.Path!)} into {Name}: {scene?.VertexCount ?? 0:N0} vertices, {scene?.TriangleCount ?? 0:N0} triangles.");
                foreach (string note in notes)
                {
                    context.Log($"  note: {note}");
                }
                context.Log($"  {Name} is now {Sizes.Describe(payload.Length)}. Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Bytes | AssetChange.Scene | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
}

/// <summary>A PIFF 3DOB model: karts, characters, pick-ups and props, with their skins and animation clips.</summary>
internal sealed class ModelNode : GeometryNode, IAnimatedAsset, ICommandSource
{
    private (int Revision, PiffModel Model, PiffAnimation Animation)? _parsed;
    public ModelNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, Label(PiffModel.Parse(entry.Payload)))
    {
    }
    private static string Label(PiffModel model)
    {
        return $"{model.Polys.Sum(p => PiffModel.Triangles(p.Mode, p.Indices).Count() / 3)} triangles{(model.IsSkinned ? $", {model.Bones.Count} bones" : string.Empty)}";
    }
    private (PiffModel Model, PiffAnimation Animation) Parsed
    {
        get
        {
            if (_parsed is not { } cached || cached.Revision != Context.Archive.Revision)
            {
                PiffModel model = PiffModel.Parse(Entry.Payload);
                _parsed = cached = (Context.Archive.Revision, model, new PiffAnimation(model));
            }
            return (cached.Model, cached.Animation);
        }
    }
    public override string Extension => ".pof";
    protected override SceneGeometry Build(TextureSet textures)
    {
        return Parsed.Model.BuildScene(textures, FileStem);
    }
    public IReadOnlyList<AnimationClip> Clips => Parsed.Animation.Clips;
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return ImportCommand(PiffWriter.Replace);
    }
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        return Parsed.Animation.TryPose(clip, seconds, out positions, out normals);
    }
    protected override void Check(byte[] payload)
    {
        _ = PiffModel.Parse(payload);
    }
    protected override string DescribeSize()
    {
        return Label(PiffModel.Parse(Entry.Payload));
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PiffModel model = Parsed.Model;
        yield return PropertyRow.Of("Vertices", model.VertexCount);
        yield return PropertyRow.Of("Primitive groups", model.Polys.Count);
        yield return PropertyRow.Of("Textures", string.Join(", ", model.Textures));
        yield return PropertyRow.Of("Vertex data", string.Join(", ", new[] { "positions", model.Normals.Length > 0 ? "normals" : null, model.Uvs.Length > 0 ? "UVs" : null, model.Colors.Length > 0 ? $"{model.ColorBytes}-byte colours" : null }.OfType<string>()));
        if (model.IsSkinned)
        {
            yield return PropertyRow.Of("Bones", string.Join(", ", model.Bones.Select(b => b.Name)));
        }
        if (model.Clips.Count > 0)
        {
            yield return PropertyRow.Of("Clips", string.Join(", ", model.Clips.Select(c => c.Name)));
        }
        if (model.LodLevels > 0)
        {
            yield return PropertyRow.Of("Levels of detail", model.LodLevels + 1);
        }
        if (model.Id.Length > 0)
        {
            yield return PropertyRow.Of("ID", model.Id);
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}

/// <summary>A PIFF SCEN track: the render geometry, with the gameplay chunk counts listed.</summary>
internal sealed class TrackNode : GeometryNode
{
    public TrackNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, Label(PiffTrack.Parse(entry.Payload)))
    {
    }
    private static string Label(PiffTrack track)
    {
        return $"{track.Chunks.Count} chunks, {track.Textures.Count} textures";
    }
    public override string Extension => ".trk";
    protected override SceneGeometry Build(TextureSet textures)
    {
        return PiffTrack.Parse(Entry.Payload).BuildScene(textures);
    }
    protected override void Check(byte[] payload)
    {
        _ = PiffTrack.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PiffTrack track = PiffTrack.Parse(Entry.Payload);
        yield return PropertyRow.Of("Layout", track.Cnk2Layout ? "Nitro Kart 2 (iOS)" : "Nitro Kart 3D (Zeebo) / Krazy Kart Racing");
        yield return PropertyRow.Of("Vertices", track.Vertices);
        yield return PropertyRow.Of("Draw chunks", track.Chunks.Count);
        yield return PropertyRow.Of("Materials", track.Materials.Count);
        yield return PropertyRow.Of("Textures", string.Join(", ", track.Textures.Where(t => t.Length > 0)));
        foreach ((string id, int count) in track.Counts)
        {
            yield return PropertyRow.Of(id switch
            {
                "POSI" => "Named positions",
                "CAMS" => "Cameras",
                "PICK" => "Pick-ups",
                "EMIT" => "Particle emitters",
                "LINE" => "Lines (laps, checkpoints, AI)",
                _ => "Collision regions",
            }, count);
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}

/// <summary>A PXC scene graph: a car, a track or a set of props, every mesh placed through its parents.</summary>
internal sealed class SceneNode : GeometryNode, ICommandSource
{
    public SceneNode(ArchiveContext context, string name, string path, VfsEntry entry)
        : base(context, name, path, entry, Label(PxcScene.Parse(entry.Payload)))
    {
    }
    private static string Label(PxcScene scene)
    {
        return $"{scene.Objects.Count(o => o.Mesh is not null)} meshes";
    }
    public override string Extension => ".pxc";
    public IEnumerable<AssetCommand> GetCommands()
    {
        if (PxcScene.Parse(Entry.Payload).Objects.Any(o => o.Mesh is { Chunk: "MESH" }))
        {
            yield return ImportCommand(PxcWriter.Replace);
        }
    }
    protected override SceneGeometry Build(TextureSet textures)
    {
        return PxcScene.Parse(Entry.Payload).BuildScene(Entry.Payload, textures);
    }
    protected override void Check(byte[] payload)
    {
        _ = PxcScene.Parse(payload);
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        PxcScene scene = PxcScene.Parse(Entry.Payload);
        yield return PropertyRow.Of("Version", $"{scene.Version} ({(scene.Getaway ? "Reckless Getaway" : "Raging Thunder 2")})");
        yield return PropertyRow.Of("Objects", scene.Objects.Count);
        foreach (IGrouping<string, PxcObject> group in scene.Objects.GroupBy(o => o.Tag).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            yield return PropertyRow.Of(group.Key switch
            {
                "SGGR" => "Groups",
                "SGNO" => "Nodes",
                "SGPS" => "Meshes",
                "SGSP" => "Locators",
                "SGCA" => "Cameras",
                "SGME" => "Metadata objects",
                "SGMR" => "References",
                "SGPR" => "Projectors",
                "SGLI" => "Lights",
                "SGAN" => "Animation sets",
                "SGCU" => "Curves",
                "SGCO" => "Collision",
                "SGPC" => "Portal cells",
                "SGG2" => "Grid cells",
                _ => group.Key,
            }, group.Count());
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
