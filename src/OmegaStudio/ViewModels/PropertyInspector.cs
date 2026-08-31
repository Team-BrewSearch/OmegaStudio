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
                $"Describing '{node.Name}'",
                () => (IReadOnlyList<PropertyRow>)[.. describable.Describe()],
                []));
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
        if (!string.IsNullOrEmpty(node.PathText))
        {
            rows.Add(new PropertyRow("Location", node.PathText));
        }
        if (node is IBinaryAsset binary)
        {
            rows.Add(new PropertyRow("Size", Sizes.Describe(PluginGuard.Try(
                $"Reading the size of '{node.Name}'", () => binary.Size, 0))));
        }
        return rows;
    }
}
