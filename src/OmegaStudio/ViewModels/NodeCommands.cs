using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;

internal static class NodeCommands
{
    private static readonly FileFilter s_anyFile = new("All files", "*.*");
    private static readonly FileFilter s_gltf = new("glTF 2.0", "*.glb", "*.gltf");
    private static readonly FileFilter[] s_imageWrite =
    [
        new FileFilter("PNG image", "*.png"),
        new FileFilter("JPEG image", "*.jpg", "*.jpeg"),
        new FileFilter("BMP image", "*.bmp"),
    ];
    private static readonly FileFilter s_imageRead = new("Image", "*.png", "*.jpg", "*.jpeg", "*.bmp");
    public static IReadOnlyList<AssetCommand> For(IAssetNode node, AnimationClip? clip, float fps)
    {
        List<AssetCommand> own = [], exports = [], replacements = [], extra = [];
        if (node is IBinaryAsset binary)
        {
            own.Add(Extract(node, binary));
            if (binary.CanReplaceBytes)
            {
                replacements.Add(ReplaceBytes(node));
            }
        }
        if (node is IAudioAsset audio)
        {
            own.Add(ExtractAudio(node, audio));
        }
        if (node is IImageAsset image)
        {
            exports.Add(ExportImage(node));
            if (image.CanReplaceImage)
            {
                replacements.Add(ReplaceImage(node));
            }
        }
        if (node is ISceneAsset)
        {
            exports.Add(ExportScene(node, clip, fps));
        }
        if (node is ICommandSource source)
        {
            foreach (AssetCommand command in PluginGuard.Try(
                $"Listing commands for '{node.Name}'",
                () => (IReadOnlyList<AssetCommand>)[.. source.GetCommands()],
                []))
            {
                Bucket(command.Role, exports, replacements, extra).Add(command);
            }
        }
        List<AssetCommand> menu = [.. own];
        if (exports.Count > 0)
        {
            menu.Add(Merge("Export…", AssetCommandKind.SaveFile, exports));
        }
        if (replacements.Count > 0)
        {
            menu.Add(Merge("Replace…", AssetCommandKind.OpenFile, replacements));
        }
        menu.AddRange(extra);
        return menu;
    }
    private static List<AssetCommand> Bucket(
        AssetCommandRole role,
        List<AssetCommand> exports,
        List<AssetCommand> replacements,
        List<AssetCommand> extra)
    {
        return role switch
        {
            AssetCommandRole.Export => exports,
            AssetCommandRole.Replace => replacements,
            _ => extra,
        };
    }
    private static AssetCommand Extract(IAssetNode node, IBinaryAsset binary)
    {
        string extension = string.IsNullOrEmpty(binary.Extension) ? ".bin" : binary.Extension;
        return new AssetCommand
        {
            Title = "Extract…",
            Kind = AssetCommandKind.SaveFile,
            Filters = [new FileFilter("Raw data", "*" + extension)],
            SuggestedFileName = SuggestName(node, extension),
            Execute = context =>
            {
                byte[] bytes = ((IBinaryAsset)context.Node).Data.ToArray();
                File.WriteAllBytes(context.Path!, bytes);
                context.Log($"Extracted {node.PathText} ({Sizes.Describe(bytes.Length)}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        };
    }
    private static AssetCommand ReplaceBytes(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Replace raw",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [s_anyFile],
            Execute = context =>
            {
                IBinaryAsset target = (IBinaryAsset)context.Node;
                byte[] bytes = File.ReadAllBytes(context.Path!);
                if (bytes.Length == 0)
                {
                    throw new InvalidDataException("the file is empty");
                }
                int was = target.Size;
                target.ReplaceBytes(bytes);
                string delta = bytes.Length == was
                    ? string.Empty
                    : $" — slot {(bytes.Length > was ? "grew" : "shrank")} from {Sizes.Describe(was)}";
                context.Log($"Replaced {node.PathText} with {Path.GetFileName(context.Path!)} " +
                    $"({Sizes.Describe(bytes.Length)}){delta}. Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Bytes | AssetChange.Children | AssetChange.Scene
                    | AssetChange.Image | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    private static AssetCommand ExtractAudio(IAssetNode node, IAudioAsset audio)
    {
        string extension = PluginGuard.Try(
            $"Reading the audio of '{node.Name}'",
            () => AudioFormats.ExtensionOf(audio.EncodedAudio),
            ".wav");
        string label = extension == ".ogg" ? "OGG Vorbis" : "WAV audio";
        return new AssetCommand
        {
            Title = "Extract Audio…",
            Kind = AssetCommandKind.SaveFile,
            Filters = [new FileFilter(label, "*" + extension)],
            SuggestedFileName = SuggestName(node, extension),
            Execute = context =>
            {
                byte[] bytes = ((IAudioAsset)context.Node).EncodedAudio.ToArray();
                File.WriteAllBytes(context.Path!, bytes);
                context.Log($"Extracted {label} ({Sizes.Describe(bytes.Length)}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        };
    }
    private static AssetCommand ExportImage(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Export image",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = s_imageWrite,
            SuggestedFileName = SuggestName(node, ".png"),
            Execute = context =>
            {
                ImageData data = ((IImageAsset)context.Node).Image;
                context.Host.Images.Save(data, context.Path!);
                context.Log($"Exported {data.Name} ({data.Width}x{data.Height}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        };
    }
    private static AssetCommand ReplaceImage(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Replace image",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [s_imageRead],
            Execute = context =>
            {
                IImageAsset target = (IImageAsset)context.Node;
                int wasWidth = target.Image.Width, wasHeight = target.Image.Height;
                ImageData replacement = context.Host.Images.Load(context.Path!);
                target.ReplaceImage(replacement);
                string resized = replacement.Width == wasWidth && replacement.Height == wasHeight
                    ? string.Empty
                    : $" — resized from {wasWidth}x{wasHeight} to {replacement.Width}x{replacement.Height}";
                context.Log($"Replaced {node.Name} from {Path.GetFileName(context.Path!)}{resized}. " +
                    "Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Image | AssetChange.Bytes | AssetChange.Scene
                    | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    private static AssetCommand ExportScene(IAssetNode node, AnimationClip? clip, float fps)
    {
        return new AssetCommand
        {
            Title = "Export glTF",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [s_gltf],
            SuggestedFileName = SuggestName(node, ".glb"),
            Execute = context =>
            {
                SceneGeometry scene = ((ISceneAsset)context.Node).GetScene()
                    ?? throw new InvalidOperationException("this asset has no geometry to export");
                scene = WithClip(scene, clip, fps);
                context.Host.Gltf.Write(scene, context.Path!);
                context.Log($"Exported {node.Name} to {Path.GetFileName(context.Path!)} " +
                    $"({scene.TriangleCount:N0} triangles, {scene.Textures.Count} texture(s), " +
                    $"{scene.Clips.Count} animation(s)).");
            },
        };
    }
    private static SceneGeometry WithClip(SceneGeometry scene, AnimationClip? clip, float fps)
    {
        if (scene.Clips.Count == 0)
        {
            return scene;
        }
        List<AnimationClip> wanted = clip is null
            ? []
            : [.. scene.Clips
                .Where(candidate => ReferenceEquals(candidate, clip)
                    || string.Equals(candidate.Name, clip.Name, StringComparison.Ordinal))
                .Select(candidate => AtRate(candidate, fps))];
        return new SceneGeometry
        {
            Meshes = scene.Meshes,
            Textures = scene.Textures,
            Lighting = scene.Lighting,
            Filtering = scene.Filtering,
            Joints = scene.Joints,
            Clips = wanted,
        };
    }
    private static AnimationClip AtRate(AnimationClip clip, float fps)
    {
        return fps <= 0f || Math.Abs(clip.Fps - fps) < 0.001f
            ? clip
            : new AnimationClip
            {
                Name = clip.Name,
                Frames = clip.Frames,
                Fps = fps,
                Tracks = clip.Tracks,
            };
    }
    private static AssetCommand Merge(
        string title, AssetCommandKind kind, List<AssetCommand> alternatives)
    {
        List<FileFilter> filters = [.. alternatives.SelectMany(alternative => alternative.Filters)];
        if (kind == AssetCommandKind.OpenFile && filters.Count > 1)
        {
            FileFilter combined = Combined(filters);
            _ = filters.RemoveAll(filter => Same(filter, combined));
            filters.Insert(0, combined);
        }
        return new AssetCommand
        {
            Title = title,
            Kind = kind,
            Filters = filters,
            SuggestedFileName = alternatives[0].SuggestedFileName,
            Execute = context => Choose(alternatives, context.Path).Execute(context),
        };
    }
    private static FileFilter Combined(IEnumerable<FileFilter> filters)
    {
        string[] patterns = [.. filters
            .SelectMany(filter => filter.Patterns)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return Array.IndexOf(patterns, "*.*") >= 0
            ? s_anyFile
            : new FileFilter("Supported files", patterns);
    }
    private static bool Same(FileFilter filter, FileFilter other)
    {
        return filter.Label == other.Label && filter.Patterns.SequenceEqual(other.Patterns);
    }
    private static AssetCommand Choose(List<AssetCommand> alternatives, string? path)
    {
        string extension = Path.GetExtension(path ?? string.Empty);
        foreach (AssetCommand alternative in alternatives)
        {
            if (Names(alternative, extension))
            {
                return alternative;
            }
        }
        foreach (AssetCommand alternative in alternatives)
        {
            if (alternative.Filters.SelectMany(filter => filter.Patterns).Contains("*.*"))
            {
                return alternative;
            }
        }
        return alternatives[0];
    }
    private static bool Names(AssetCommand command, string extension)
    {
        return extension.Length > 1 && command.Filters
            .SelectMany(filter => filter.Patterns)
            .Any(pattern => pattern.Length > 2
                && pattern.StartsWith("*.", StringComparison.Ordinal)
                && pattern.AsSpan(1).Equals(extension, StringComparison.OrdinalIgnoreCase));
    }
    private static string SuggestName(IAssetNode node, string extension)
    {
        string stem = !string.IsNullOrWhiteSpace(node.FileStem) ? node.FileStem
            : !string.IsNullOrWhiteSpace(node.Name) ? node.Name
            : !string.IsNullOrWhiteSpace(node.PathText) ? node.PathText
            : "asset";
        return ImageIO.SafeName(
            stem.Replace('/', '_').Replace('\\', '_').Replace("..", "_")) + extension;
    }
}
