using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal static class PropertyInspector
{
    public static IEnumerable<PropertyRow> Describe(IAssetNode? node, AudioInfo? audio = null)
    {
        if (node is null)
        {
            return [];
        }
        List<PropertyRow> rows = [];
        if (node is IDescribable describable)
        {
            rows.AddRange(PluginGuard.Try(
                $"Describing '{NodeAccess.Name(node)}'",
                () => (IReadOnlyList<PropertyRow>)[.. describable.Describe()],
                []));
        }
        if (node is IVideoAsset video && PluginGuard.Try("Reading the video header", () => video.Video, null) is { } info)
        {
            rows.Add(new PropertyRow("Video", $"{info.Width} x {info.Height}, {info.FramesPerSecond:0.###} fps"));
            rows.Add(new PropertyRow("Frames", $"{info.FrameCount:N0}"));
            rows.Add(new PropertyRow("Duration", $"{info.Duration:mm\\:ss\\.fff}"));
        }
        if (audio is { } clip)
        {
            rows.Add(new PropertyRow("Format", clip.Format));
            rows.Add(new PropertyRow("Sample rate", $"{clip.SampleRate:N0} Hz"));
            rows.Add(new PropertyRow("Channels", clip.Channels switch
            {
                1 => "Mono",
                2 => "Stereo",
                _ => $"{clip.Channels}",
            }));
            rows.Add(new PropertyRow("Bit depth", $"{clip.BitsPerSample}-bit"));
            rows.Add(new PropertyRow("Duration", $"{clip.Duration:mm\\:ss\\.fff}"));
        }
        string where = NodeAccess.PathText(node);
        if (!string.IsNullOrEmpty(where))
        {
            rows.Add(new PropertyRow("Location", where));
        }
        if (node is IBinaryAsset binary)
        {
            rows.Add(new PropertyRow("Size", Sizes.Describe(NodeAccess.Size(binary))));
        }
        return rows;
    }
}
