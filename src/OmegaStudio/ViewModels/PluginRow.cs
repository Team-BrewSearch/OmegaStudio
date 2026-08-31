using Omega.Plugins;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// One plugin as the Formats menu shows it.
///
/// <para>
/// Every field a plugin declares about itself reaches the menu, which the single
/// formatted string this replaced could not manage: the description and the id never
/// appeared at all, and the line was long enough that the flyout clipped the file
/// patterns off the end — so the one plugin whose name was longest silently lost the
/// very thing you would look it up for.
/// </para>
/// </summary>
public sealed record PluginRow(
    string Name,
    string Version,
    string Description,
    string Provenance,
    string Formats)
{
    /// <summary>Whether there is a description line to draw.</summary>
    public bool HasDescription => Description.Length > 0;

    internal static PluginRow For(LoadedPlugin plugin)
    {
        FormatPluginInfo info = plugin.Info;

        return new PluginRow(
            info.DisplayName,
            info.Version,
            info.Description,
            Provenance: string.IsNullOrWhiteSpace(info.Author) ? info.Id : $"{info.Id} · {info.Author}",
            Formats: DescribeFormats(plugin));
    }

    /// <summary>
    /// What this plugin opens, named as the Open dialog names it — or what it does
    /// instead, for one that opens nothing.
    /// </summary>
    private static string DescribeFormats(LoadedPlugin plugin)
    {
        string[] formats =
        [
            .. PluginGuard.Formats(plugin)
                .Select(format => $"{format.Filter.Label} ({string.Join(", ", format.Filter.Patterns)})"),
        ];

        if (formats.Length > 0)
        {
            return "Opens " + string.Join(", ", formats);
        }

        int recognizers = PluginGuard.Try(
            $"Listing recognisers for {plugin.Info.DisplayName}",
            () => plugin.Plugin.Recognizers.Count(),
            0);

        return recognizers > 0
            ? $"Opens nothing — contributes {recognizers} content recogniser(s)"
            : "Opens nothing";
    }
}
