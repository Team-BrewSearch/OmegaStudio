using Omega.Plugins;
using System.Reflection;
namespace OmegaStudio.Plugins;

internal sealed record LoadedPlugin(IFormatPlugin Plugin, FormatPluginInfo Info, string Directory)
{
    public override string ToString()
    {
        return $"{Info.DisplayName} {Info.Version}";
    }
}
internal sealed record PluginFailure(string File, string Reason);
internal sealed record PluginCatalog(
    IReadOnlyList<LoadedPlugin> Plugins,
    IReadOnlyList<PluginFailure> Failures)
{
    public static PluginCatalog Empty { get; } = new([], []);
    public IEnumerable<(LoadedPlugin Owner, IArchiveFormat Format)> Formats
    {
        get
        {
            foreach (LoadedPlugin plugin in Plugins)
            {
                foreach (IArchiveFormat format in PluginGuard.Formats(plugin))
                {
                    yield return (plugin, format);
                }
            }
        }
    }
}
internal static class PluginLoader
{
    public const string FolderName = "formats";
    public const string Suffix = ".Formats.dll";
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, FolderName);
    public static PluginCatalog Load(string directory, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Directory.Exists(directory))
        {
            log($"No formats folder at {directory} — nothing to load.");
            return PluginCatalog.Empty;
        }
        List<LoadedPlugin> loaded = [];
        List<PluginFailure> failures = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Discover(directory))
        {
            string name = Path.GetFileName(file);
            try
            {
                LoadedPlugin plugin = LoadOne(file);
                if (!ids.Add(plugin.Info.Id))
                {
                    failures.Add(new PluginFailure(
                        name, $"another plugin already claims the id '{plugin.Info.Id}'"));
                    continue;
                }
                loaded.Add(plugin);
                log($"Loaded {plugin.Info.DisplayName} {plugin.Info.Version} from {name}.");
            }
            catch (Exception ex)
            {
                failures.Add(new PluginFailure(name, Describe(ex)));
                log($"Could not load {name}: {Describe(ex)}");
            }
        }
        if (loaded.Count == 0 && failures.Count == 0)
        {
            log($"No format plugins found in {directory}.");
        }
        return new PluginCatalog(loaded, failures);
    }
    private static IEnumerable<string> Discover(string directory)
    {
        return Directory.EnumerateFiles(directory, "*" + Suffix).Order(StringComparer.Ordinal);
    }
    private static LoadedPlugin LoadOne(string file)
    {
        string full = Path.GetFullPath(file);
        PluginLoadContext context = new(full);
        Assembly assembly = context.LoadPlugin();
        OmegaPluginAttribute? attribute = assembly.GetCustomAttribute<OmegaPluginAttribute>();
        if (attribute is null)
        {
            throw new InvalidOperationException(
                "it has no [assembly: OmegaPlugin(typeof(...))] attribute naming its plugin type");
        }
        if (!typeof(IFormatPlugin).IsAssignableFrom(attribute.PluginType))
        {
            throw new InvalidOperationException(
                $"'{attribute.PluginType.FullName}' does not implement IFormatPlugin");
        }
        if (Activator.CreateInstance(attribute.PluginType) is not IFormatPlugin plugin)
        {
            throw new InvalidOperationException(
                $"'{attribute.PluginType.FullName}' could not be constructed");
        }
        FormatPluginInfo info = plugin.Info;
        string? directory = Path.GetDirectoryName(full);
        return info.ContractVersion != PluginContract.Version
            ? throw new InvalidOperationException(
                $"it was built against plugin contract version {info.ContractVersion}, " +
                $"but this build of Omega Studio speaks version {PluginContract.Version}. " +
                "Rebuild the plugin against the matching Omega.Plugins.")
            : string.IsNullOrWhiteSpace(info.Id)
            ? throw new InvalidOperationException("its FormatPluginInfo has no Id")
            : string.IsNullOrEmpty(directory)
            ? throw new InvalidOperationException($"'{full}' has no containing directory")
            : new LoadedPlugin(plugin, info, directory);
    }
    private static string Describe(Exception ex)
    {
        return ex switch
        {
            TargetInvocationException { InnerException: { } inner } => Describe(inner),
            BadImageFormatException => "it is not a valid .NET assembly for this platform",
            FileNotFoundException missing => $"a dependency is missing ({missing.FileName})",
            _ => ex.Message,
        };
    }
}
