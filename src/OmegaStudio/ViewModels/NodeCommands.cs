using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;

internal static class NodeCommands
{
    private static readonly FileFilter s_anyFile = new("All files", "*.*");
    private static readonly FileFilter s_png = new("PNG image", "*.png");
    private static readonly FileFilter s_gltf = new("glTF 2.0", "*.glb", "*.gltf");
    public static IReadOnlyList<AssetCommand> For(IAssetNode node)
    {
        List<AssetCommand> commands = [];
        if (node is IBinaryAsset binary)
        {
            AddBinary(commands, node, binary);
        }
        if (node is IAudioAsset audio)
        {
            AddAudio(commands, node, audio);
        }
        if (node is IImageAsset image)
        {
            AddImage(commands, node, image);
        }
        if (node is ISceneAsset)
        {
            AddScene(commands, node);
        }
        if (node is ICommandSource source)
        {
            commands.AddRange(PluginGuard.Try(
                $"Listing commands for '{node.Name}'",
                () => (IReadOnlyList<AssetCommand>)[.. source.GetCommands()],
                []));
        }
        return commands;
    }
    private static void AddBinary(List<AssetCommand> commands, IAssetNode node, IBinaryAsset binary)
    {
        string extension = string.IsNullOrEmpty(binary.Extension) ? ".bin" : binary.Extension;
        commands.Add(new AssetCommand
        {
            Title = "Extract Raw…",
            Kind = AssetCommandKind.SaveFile,
            Filter = new FileFilter("Raw data", "*" + extension),
            SuggestedFileName = SuggestName(node, extension),
            Execute = context =>
            {
                byte[] bytes = ((IBinaryAsset)context.Node).Data.ToArray();
                File.WriteAllBytes(context.Path!, bytes);
                context.Log($"Extracted {node.PathText} ({Sizes.Describe(bytes.Length)}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        });
        if (!binary.CanReplaceBytes)
        {
            return;
        }
        commands.Add(new AssetCommand
        {
            Title = "Replace Raw…",
            Kind = AssetCommandKind.OpenFile,
            Filter = s_anyFile,
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
        });
    }
    private static void AddAudio(List<AssetCommand> commands, IAssetNode node, IAudioAsset audio)
    {
        string extension = PluginGuard.Try(
            $"Reading the audio of '{node.Name}'",
            () => AudioFormats.ExtensionOf(audio.EncodedAudio),
            ".wav");
        string label = extension == ".ogg" ? "OGG Vorbis" : "WAV audio";
        commands.Add(new AssetCommand
        {
            Title = "Extract Audio…",
            Kind = AssetCommandKind.SaveFile,
            Filter = new FileFilter(label, "*" + extension),
            SuggestedFileName = SuggestName(node, extension),
            Execute = context =>
            {
                byte[] bytes = ((IAudioAsset)context.Node).EncodedAudio.ToArray();
                File.WriteAllBytes(context.Path!, bytes);
                context.Log($"Extracted {label} ({Sizes.Describe(bytes.Length)}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        });
    }
    private static void AddImage(List<AssetCommand> commands, IAssetNode node, IImageAsset image)
    {
        commands.Add(new AssetCommand
        {
            Title = "Export PNG…",
            Kind = AssetCommandKind.SaveFile,
            Filter = s_png,
            SuggestedFileName = SuggestName(node, ".png"),
            Execute = context =>
            {
                ImageData data = ((IImageAsset)context.Node).Image;
                context.Host.Images.SavePng(data, context.Path!);
                context.Log($"Exported {data.Name} ({data.Width}x{data.Height}) " +
                    $"to {Path.GetFileName(context.Path!)}.");
            },
        });
        if (!image.CanReplaceImage)
        {
            return;
        }
        ImageData? current = PluginGuard.Try<ImageData?>($"Reading '{node.Name}'", () => image.Image, null);
        string size = current is null ? "any size" : $"{current.Width}x{current.Height}, any size accepted";
        commands.Add(new AssetCommand
        {
            Title = $"Replace from PNG… ({size})",
            Kind = AssetCommandKind.OpenFile,
            Filter = s_png,
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
        });
    }
    private static void AddScene(List<AssetCommand> commands, IAssetNode node)
    {
        commands.Add(new AssetCommand
        {
            Title = "Export glTF…",
            Kind = AssetCommandKind.SaveFile,
            Filter = s_gltf,
            SuggestedFileName = SuggestName(node, ".glb"),
            Execute = context =>
            {
                SceneGeometry scene = ((ISceneAsset)context.Node).GetScene()
                    ?? throw new InvalidOperationException("this asset has no geometry to export");
                context.Host.Gltf.Write(scene, context.Path!);
                context.Log($"Exported {node.Name} to {Path.GetFileName(context.Path!)} " +
                    $"({scene.TriangleCount:N0} triangles, {scene.Textures.Count} texture(s)).");
            },
        });
    }
    private static string SuggestName(IAssetNode node, string extension)
    {
        string stem = !string.IsNullOrWhiteSpace(node.Name) ? node.Name
            : !string.IsNullOrWhiteSpace(node.PathText) ? node.PathText
            : "asset";
        return ImageIO.SafeName(
            stem.Replace('/', '_').Replace('\\', '_').Replace("..", "_")) + extension;
    }
}
