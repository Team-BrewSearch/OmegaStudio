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
    public static IReadOnlyList<AssetCommand> For(IAssetNode node, AnimationClip? clip, float fps, Func<int> videoFrame)
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
        if (node is IVideoAsset)
        {
            exports.Add(ExportFrame(node, videoFrame));
            exports.Add(ExportVideo(node));
            exports.Add(ExportSoundtrack(node));
            extra.Add(ExportFrames(node));
        }
        if (node is ICommandSource source)
        {
            foreach (AssetCommand command in PluginGuard.Try(
                $"Listing commands for '{NodeAccess.Name(node)}'",
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
        string extension = string.IsNullOrEmpty(NodeAccess.Extension(binary)) ? ".bin" : NodeAccess.Extension(binary);
        return new AssetCommand
        {
            Title = "Extract…",
            Kind = AssetCommandKind.SaveFile,
            Filters = [new FileFilter("Raw data", "*" + extension)],
            SuggestedFileName = SuggestName(node, extension),
            Execute = context =>
            {
                byte[] bytes = ((IBinaryAsset)context.Node).Data.ToArray();
                AtomicSave.Write(context.Path!, bytes);
                context.Log($"Extracted {NodeAccess.PathText(node)} ({Sizes.Describe(bytes.Length)}) " +
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
                context.Log($"Replaced {NodeAccess.PathText(node)} with {Path.GetFileName(context.Path!)} " +
                    $"({Sizes.Describe(bytes.Length)}){delta}. Use File ▸ Save to write it back.");
                context.Changed = AssetChange.Bytes | AssetChange.Children | AssetChange.Scene
                    | AssetChange.Image | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    private static AssetCommand ExtractAudio(IAssetNode node, IAudioAsset audio)
    {
        string extension = PluginGuard.Try(
            $"Reading the audio of '{NodeAccess.Name(node)}'",
            () => AudioFormats.ExtensionOf(NodeAccess.EncodedAudio(audio)),
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
                AtomicSave.Write(context.Path!, bytes);
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
                context.Log($"Replaced {NodeAccess.Name(node)} from {Path.GetFileName(context.Path!)}{resized}. " +
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
                context.Log($"Exported {NodeAccess.Name(node)} to {Path.GetFileName(context.Path!)} " +
                    $"({scene.TriangleCount:N0} triangles, {scene.Textures.Count} texture(s), " +
                    $"{scene.Clips.Count} animation(s)).");
            },
        };
    }
    private static AssetCommand ExportFrame(IAssetNode node, Func<int> current)
    {
        return new AssetCommand
        {
            Title = "Export frame",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = s_imageWrite,
            SuggestedFileName = SuggestName(node, ".png"),
            Execute = context =>
            {
                IVideoAsset video = (IVideoAsset)context.Node;
                using IVideoReader reader = video.OpenVideo();
                VideoInfo info = reader.Info;
                int frame = Math.Clamp(current(), 0, Math.Max(0, info.FrameCount - 1));
                byte[] rgba = new byte[info.FrameBytes];
                reader.Seek(frame);
                if (!reader.ReadFrame(rgba))
                {
                    throw new InvalidDataException($"frame {frame + 1} could not be decoded");
                }
                context.Host.Images.Save(ImageData.Create($"frame{frame}", info.Width, info.Height, rgba), context.Path!);
                context.Log($"Exported frame {frame + 1} of {NodeAccess.Name(node)} ({info.Width}x{info.Height}) to {Path.GetFileName(context.Path!)}.");
            },
        };
    }
    private static AssetCommand ExportFrames(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Export all frames…",
            Kind = AssetCommandKind.SaveFile,
            Filters = [new FileFilter("PNG images, numbered", "*.png")],
            SuggestedFileName = SuggestName(node, ".png"),
            Execute = context =>
            {
                IVideoAsset video = (IVideoAsset)context.Node;
                using IVideoReader reader = video.OpenVideo();
                VideoInfo info = reader.Info;
                string folder = Path.GetDirectoryName(Path.GetFullPath(context.Path!))!;
                string stem = Path.GetFileNameWithoutExtension(context.Path!);
                int digits = Math.Max(5, info.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
                int batch = Math.Max(1, Environment.ProcessorCount);
                int written = 0;
                reader.Seek(0);
                while (written < info.FrameCount)
                {
                    List<byte[]> pixels = [];
                    for (int k = 0; k < batch && written + pixels.Count < info.FrameCount; k++)
                    {
                        byte[] rgba = new byte[info.FrameBytes];
                        if (!reader.ReadFrame(rgba))
                        {
                            break;
                        }
                        pixels.Add(rgba);
                    }
                    if (pixels.Count == 0)
                    {
                        break;
                    }
                    int first = written;
                    Parallel.For(0, pixels.Count, k =>
                    {
                        string name = $"{stem}_{(first + k + 1).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture)}.png";
                        AtomicSave.Write(Path.Combine(folder, name), context.Host.Images.EncodePng(ImageData.Create(name, info.Width, info.Height, pixels[k])));
                    });
                    written += pixels.Count;
                }
                context.Log($"Exported {written:N0} frames of {NodeAccess.Name(node)} to {folder} as {stem}_{new string('0', digits - 1)}1.png and on.");
            },
        };
    }
    private static AssetCommand ExportVideo(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Export video",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("Matroska video (Motion JPEG and PCM)", "*.mkv")],
            SuggestedFileName = SuggestName(node, ".mkv"),
            Execute = context =>
            {
                IVideoAsset video = (IVideoAsset)context.Node;
                using IVideoReader reader = video.OpenVideo();
                byte[] soundtrack = video.Soundtrack.ToArray();
                MatroskaWriter.Write(context.Path!, reader, soundtrack, image => context.Host.Images.Encode(image, ".jpg"), context.Log);
                context.Log($"Exported {NodeAccess.Name(node)} to {Path.GetFileName(context.Path!)} ({Sizes.Describe(new FileInfo(context.Path!).Length)}, "
                    + $"{reader.Info.FrameCount:N0} frames{(soundtrack.Length > 0 ? " with sound" : ", no sound")}).");
            },
        };
    }
    private static AssetCommand ExportSoundtrack(IAssetNode node)
    {
        return new AssetCommand
        {
            Title = "Export soundtrack",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("Soundtrack (WAV or Ogg Vorbis, as the video stores it)", "*.wav", "*.ogg")],
            SuggestedFileName = SuggestName(node, ".wav"),
            Execute = context =>
            {
                byte[] sound = ((IVideoAsset)context.Node).Soundtrack.ToArray();
                if (sound.Length == 0)
                {
                    throw new InvalidOperationException("this video has no soundtrack");
                }
                string path = Path.ChangeExtension(context.Path!, AudioFormats.ExtensionOf(sound));
                AtomicSave.Write(path, sound);
                context.Log($"Exported the soundtrack of {NodeAccess.Name(node)} to {Path.GetFileName(path)} ({Sizes.Describe(sound.Length)}).");
            },
        };
    }
    private static string FirstLabel(IAssetNode node)
    {
        string stem = NodeAccess.FileStem(node);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            return stem;
        }
        stem = NodeAccess.Name(node);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            return stem;
        }
        stem = NodeAccess.PathText(node);
        return string.IsNullOrWhiteSpace(stem) ? "asset" : stem;
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
        string stem = FirstLabel(node);
        return ImageIO.SafeName(stem.Replace('/', '_').Replace('\\', '_').Replace("..", "_")) + extension;
    }
}
