using Virtuos.Formats.Scene;
using Omega.Plugins;
using System.Diagnostics.CodeAnalysis;
namespace Virtuos.Formats.Nodes;

internal sealed class CarNode : EntryNode, ISceneAsset, IDescribable, ICommandSource
{
    private SceneGeometry? _scene;
    private int _revision = -1;
    public CarNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry, AssetKinds.Model)
    {
    }
    [AllowNull]
    private CarModel Model { get => field ??= CarModel.Parse(Entry.Span); set; }
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
        CarModel parsed = CarModel.Parse(payload);
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
        (byte[] bytes, int triangles) = CarWriter.Convert(source, Entry.Span, Geometry.Textures, context.Log);
        Entry.Replace(bytes);
        Invalidate();
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Converted {Path.GetFileName(context.Path!)} into {Entry.Name} "
            + $"({triangles} triangles). Use File ▸ Save to write it back.");
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
        yield return PropertyRow.Of("Vertices", geometry.Positions.Length / 3);
        yield return PropertyRow.Of("Triangles", geometry.Indices.Length / 3);
        yield return PropertyRow.Of("Parts", Model.ChunkOffsets.Count);
        yield return PropertyRow.Of("Textures", geometry.Textures.Length);
        for (int wheel = 0; wheel < CarModel.WheelCount; wheel++)
        {
            yield return PropertyRow.Of(
                $"Wheel {wheel + 1}",
                $"{Model.Wheel(wheel, 0):0.###}, {Model.Wheel(wheel, 1):0.###}, {Model.Wheel(wheel, 2):0.###}");
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class SkinnedModelNode : EntryNode, ISceneAsset, IAnimatedAsset, IDescribable, ICommandSource
{
    private readonly Dictionary<string, AniModel> _animations = [];
    private SceneGeometry? _scene;
    private int _revision = -1;
    public SkinnedModelNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry, AssetKinds.Model)
    {
    }
    [AllowNull]
    private AmdModel Model { get => field ??= AmdModel.Parse(Entry.Span); set; }
    [AllowNull]
    private SskTrack Geometry { get => field ??= Context.ParseModel(Entry, Model.Build([])); set; }
    private SkeModel? Skeleton
    {
        get
        {
            if (field is not null)
            {
                return field;
            }
            DwkEntry? bones = Context.Sibling(Entry, ".ske");
            return field = bones is not null && SkeModel.IsSkeleton(bones.Span)
                ? SkeModel.Parse(bones.Span)
                : null;
        }
        set;
    }
    public SceneGeometry? GetScene()
    {
        DropStaleCache();
        if (_scene is null)
        {
            IReadOnlyList<AnimationClip> clips = Clips;
            SceneGeometry built = TrackScene.Build(Geometry, Skeleton);
            _scene = built.Joints.Count > 0
                ? new SceneGeometry
                {
                    Meshes = built.Meshes,
                    Textures = built.Textures,
                    Lighting = built.Lighting,
                    Filtering = built.Filtering,
                    Joints = built.Joints,
                    Clips = clips,
                }
                : built;
        }
        return _scene.TriangleCount > 0 ? _scene : null;
    }
    [AllowNull]
    public IReadOnlyList<AnimationClip> Clips
    {
        get
        {
            DropStaleCache();
            return field ??= FindClips();
        }
        private set;
    }
    private List<AnimationClip> FindClips()
    {
        List<AnimationClip> clips = [];
        if (Skeleton is not { } skeleton)
        {
            return clips;
        }
        int cut = Entry.NormalizedPath.LastIndexOf('/');
        string folder = cut < 0 ? string.Empty : Entry.NormalizedPath[..(cut + 1)];
        foreach (DwkEntry candidate in Context.Archive.Entries)
        {
            if (!candidate.NormalizedPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                || !candidate.Name.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)
                || !AniModel.IsAnimation(candidate.Span))
            {
                continue;
            }
            AniModel animation = AniModel.Parse(candidate.Span);
            if (animation.BoneCount != skeleton.Bones.Count)
            {
                continue;
            }
            _animations[candidate.Name] = animation;
            clips.Add(RigPoser.ToClip(
                Path.GetFileNameWithoutExtension(candidate.Name), skeleton, animation, AniModel.Fps));
        }
        return clips;
    }
    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        ArgumentNullException.ThrowIfNull(clip);
        positions = [];
        normals = [];
        if (GetScene() is not { } scene || Skeleton is not { } skeleton
            || !_animations.TryGetValue(clip.Name + ".ani", out AniModel? animation))
        {
            return false;
        }
        double frame = seconds * AniModel.Fps;
        frame = animation.FrameCount > 0
            ? ((frame % animation.FrameCount) + animation.FrameCount) % animation.FrameCount
            : 0;
        return RigPoser.TryPose(scene, skeleton, animation, frame, out positions, out normals);
    }
    protected override void Write(byte[] payload)
    {
        AmdModel parsed = AmdModel.Parse(payload);
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
        if (Skeleton is not { } skeleton)
        {
            throw new InvalidDataException(
                "no skeleton sits beside this model, so its vertices cannot be placed back on their bones");
        }
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
        (byte[] bytes, int triangles) = AmdWriter.Convert(
            source, Entry.Span, skeleton, Geometry.Textures, context.Log);
        Entry.Replace(bytes);
        Invalidate();
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Converted {Path.GetFileName(context.Path!)} into {Entry.Name} "
            + $"({triangles} triangles over {skeleton.Bones.Count} bones). Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Scene
            | AssetChange.Properties | AssetChange.Dirty;
    }
    private void Invalidate()
    {
        Model = null;
        Geometry = null;
        _scene = null;
        Skeleton = null;
        Clips = null;
        _animations.Clear();
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
        SkeModel? skeleton = Skeleton;
        yield return PropertyRow.Of("Vertices", geometry.Positions.Length / 3);
        yield return PropertyRow.Of("Triangles", geometry.Indices.Length / 3);
        yield return PropertyRow.Of("Bones used", Model.Bones.Count);
        yield return PropertyRow.Of("Bone indices", string.Join(", ", Model.Bones));
        yield return PropertyRow.Of(
            "Skeleton", skeleton is null ? "none beside this model" : $"{skeleton.Bones.Count} bones");
        yield return PropertyRow.Of("Textures", geometry.Textures.Length);
        yield return PropertyRow.Of("Animations", Clips.Count);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class SkeletonNode : EntryNode, IDescribable, ICommandSource
{
    private int _revision = -1;
    public SkeletonNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }
    [AllowNull]
    private SkeModel Model
    {
        get
        {
            if (_revision != Context.Archive.Revision)
            {
                field = null;
                _revision = Context.Archive.Revision;
            }
            return field ??= SkeModel.Parse(Entry.Span);
        }
        set;
    }
    protected override void Write(byte[] payload)
    {
        SkeModel parsed = SkeModel.Parse(payload);
        base.Write(payload);
        Model = parsed;
        _revision = Context.Archive.Revision;
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace skeleton from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
        byte[] bytes = RigGltfReader.ReadSkeleton(source, Entry.Span, context.Log);
        Entry.Replace(bytes);
        Model = null;
        _revision = Context.Archive.Revision;
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Read the skeleton of {Path.GetFileName(context.Path!)} into {Entry.Name}. "
            + "Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Properties | AssetChange.Dirty;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        SkeModel model = Model;
        int depth = 0;
        for (int bone = 0; bone < model.Bones.Count; bone++)
        {
            int steps = 0;
            for (int parent = model.Bones[bone].Parent; parent >= 0; parent = model.Bones[parent].Parent)
            {
                steps++;
            }
            depth = Math.Max(depth, steps);
        }
        yield return PropertyRow.Of("Bones", model.Bones.Count);
        yield return PropertyRow.Of("Depth", depth + 1);
        for (int bone = 0; bone < model.Bones.Count; bone++)
        {
            SkeBone entry = model.Bones[bone];
            yield return PropertyRow.Of(
                $"Bone {bone}",
                $"parent {entry.Parent}, at {entry.Translation(0):0.###}, "
                    + $"{entry.Translation(1):0.###}, {entry.Translation(2):0.###}");
        }
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
internal sealed class AnimationNode : EntryNode, IDescribable, ICommandSource
{
    private int _revision = -1;
    public AnimationNode(TrackContext context, string name, string path, DwkEntry entry)
        : base(context, name, path, entry)
    {
    }
    [AllowNull]
    private AniModel Model
    {
        get
        {
            if (_revision != Context.Archive.Revision)
            {
                field = null;
                _revision = Context.Archive.Revision;
            }
            return field ??= AniModel.Parse(Entry.Span);
        }
        set;
    }
    protected override void Write(byte[] payload)
    {
        AniModel parsed = AniModel.Parse(payload);
        base.Write(payload);
        Model = parsed;
        _revision = Context.Archive.Revision;
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title = "Replace animation from glTF",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("glTF model", "*.glb", "*.gltf")],
            Execute = Import,
        };
    }
    private void Import(AssetCommandContext context)
    {
        using Omega.Plugins.Gltf.IGltfSource source = context.Host.Gltf.Open(context.Path!);
        byte[] bytes = RigGltfReader.ReadAnimation(
            source, Entry.Span, Path.GetFileNameWithoutExtension(Entry.Name), context.Log);
        Entry.Replace(bytes);
        Model = null;
        _revision = Context.Archive.Revision;
        Detail = Sizes.Describe(Entry.Size);
        context.Log(
            $"Read an animation of {Path.GetFileName(context.Path!)} into {Entry.Name}. "
            + "Use File ▸ Save to write it back.");
        context.Changed = AssetChange.Bytes | AssetChange.Properties | AssetChange.Dirty;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        AniModel model = Model;
        yield return PropertyRow.Of("Bones", model.BoneCount);
        yield return PropertyRow.Of("Frames", model.FrameCount);
        yield return PropertyRow.Of("Animated bones", model.Tracks.Count);
        yield return PropertyRow.Of(
            "Still bones", model.BoneCount - model.Tracks.Count);
        foreach (PropertyRow row in EntryRows())
        {
            yield return row;
        }
    }
}
