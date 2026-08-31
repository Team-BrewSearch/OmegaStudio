using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Builds the Properties pane for the selected node.
///
/// <para>
/// Two sources, in order. The node describes itself through
/// <see cref="IDescribable"/> — a model's triangle count, a texture's format, a
/// track's draw sections — because only the plugin knows what is worth saying about
/// it. Then the host appends what it can see for itself: the slot path, the payload
/// size, and the decoded audio format, none of which a plugin should have to repeat.
/// </para>
/// </summary>
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
