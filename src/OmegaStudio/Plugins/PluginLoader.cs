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
        Dictionary<string, string>? trusted = ReadAllowList(allowList, log);
        List<LoadedPlugin> loaded = [];
        List<PluginFailure> failures = [];
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Discover(directory))
        {
            string name = Path.GetFileName(file);
            try
            {
                byte[] image = File.ReadAllBytes(file);
                string digest = Digest(image);
                if (trusted is not null && !Allowed(name, digest, trusted))
                {
                    throw new InvalidOperationException(
                        $"its SHA-256 is not on the trusted list at {Path.GetFileName(allowList)}; Omega Studio "
                        + "loads only the plugins its build vouched for. See docs/PLUGINS.md.");
                }
                LoadedPlugin plugin = LoadOne(file, image);
                if (!ids.Add(plugin.Info.Id))
                {
                    failures.Add(new PluginFailure(
                        name, $"another plugin already claims the id '{plugin.Info.Id}'"));
                    continue;
                }
                loaded.Add(plugin);
                log($"Loaded {plugin.Info.DisplayName} {plugin.Info.Version} from {name} (sha256:{digest[..16]}…).");
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
    private static Dictionary<string, string>? ReadAllowList(string? allowList, Action<string> log)
    {
        if (allowList is null || !File.Exists(allowList))
        {
            if (allowList is not null)
            {
                log($"No trusted plugin list at {Path.GetFileName(allowList)}; loading what it finds.");
            }
            return null;
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
            string digest = at < 0 ? string.Empty : text[(at + AllowListPrefix.Length)..].Trim();
            if (digest.Length != 64)
            {
                log($"Ignoring a line in {Path.GetFileName(allowList)} that names no SHA-256 digest.");
                continue;
            }
            trusted[digest.ToLowerInvariant()] = text[..at].Trim();
        }
        if (trusted.Count == 0)
        {
            log($"{Path.GetFileName(allowList)} vouches for no plugin, so none will load.");
        }
        return trusted;
    }
    private static bool Allowed(string fileName, string digest, Dictionary<string, string> trusted)
    {
        return trusted.TryGetValue(digest, out string? name)
            && (name.Length == 0 || name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }
    public static string Digest(ReadOnlySpan<byte> image)
    {
        return Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
    }
    private static IEnumerable<string> Discover(string directory)
    {
        return Directory.EnumerateFiles(directory, "*" + Suffix).Order(StringComparer.Ordinal);
    }
    private static LoadedPlugin LoadOne(string file, byte[] image)
    {
        string full = Path.GetFullPath(file);
        PluginLoadContext context = new(full);
        Assembly assembly = context.LoadPlugin(image);
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
