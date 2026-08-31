using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;

public sealed record PluginRow(
    string Name,
    string Version,
    string Description,
    string Provenance,
    string Formats)
{
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
