using System.Reflection;
using System.Security.Cryptography;
using Omega.Plugins;
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
public sealed record PluginProvenance(string File, string? Sha256, string Signature);
internal static class PluginLoader
{
    public const string FolderName = "formats";
    public const string Suffix = ".Formats.dll";
    public const string AllowListName = "trusted-plugins.txt";
    public const string AllowListPrefix = "sha256:";
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, FolderName);
    public static string AllowListPath => Path.Combine(AppContext.BaseDirectory, AllowListName);
    public static PluginCatalog Load(string directory, Action<string> log)
    {
        return Load(directory, log, allowList: null);
    }
    public static PluginCatalog Load(string directory, Action<string> log, string? allowList)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!Directory.Exists(directory))
        {
            log($"No formats folder at {directory} — nothing to load.");
            return PluginCatalog.Empty;
        }
        Dictionary<string, string> trusted = ReadAllowList(allowList, log);
        List<LoadedPlugin> loaded = [];
        List<PluginFailure> failures = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Discover(directory))
        {
            string name = Path.GetFileName(file);
            try
            {
                if (trusted.Count > 0 && !Allowed(file, trusted))
                {
                    throw new InvalidOperationException(
                        $"its SHA-256 is not on the trusted list at {Path.GetFileName(allowList)}; Omega Studio "
                        + "loads only the plugins its build vouched for. See docs/PLUGINS.md.");
                }
                LoadedPlugin plugin = LoadOne(file);
                if (!ids.Add(plugin.Info.Id))
                {
                    failures.Add(new PluginFailure(
                        name, $"another plugin already claims the id '{plugin.Info.Id}'"));
                    continue;
                }
                loaded.Add(plugin);
                log($"Loaded {plugin.Info.DisplayName} {plugin.Info.Version} from {name}{Fingerprint(file, log)}.");
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
    private static Dictionary<string, string> ReadAllowList(string? allowList, Action<string> log)
    {
        if (allowList is null || !File.Exists(allowList))
        {
            if (allowList is not null)
            {
                log($"No trusted plugin list at {Path.GetFileName(allowList)}; loading what it finds.");
            }
            return [];
        }
        Dictionary<string, string> trusted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(allowList))
        {
            string text = line.Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }
            int at = text.IndexOf(AllowListPrefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                log($"Ignoring a line in {Path.GetFileName(allowList)} that names no digest.");
                continue;
            }
            string digest = text[(at + AllowListPrefix.Length)..].Trim();
            if (digest.Length == 64)
            {
                trusted[digest.ToLowerInvariant()] = text[..at].Trim();
            }
        }
        return trusted;
    }
    private static bool Allowed(string file, Dictionary<string, string> trusted)
    {
        string digest = Digest(file);
        return !trusted.TryGetValue(digest, out string? name)
            || string.IsNullOrEmpty(name)
            || name.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase);
    }
    private static string Fingerprint(string file, Action<string> log)
    {
        return $" (sha256:{Digest(file)[..16]}…)";
    }
    public static string Digest(string file)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
