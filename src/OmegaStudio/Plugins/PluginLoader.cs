using Omega.Plugins;
using System.Reflection;

namespace OmegaStudio.Plugins;

/// <summary>A plugin that loaded, and where it came from.</summary>
/// <param name="Plugin">The instance the host talks to.</param>
/// <param name="Info">Its self-description, read once.</param>
/// <param name="Directory">The folder it was loaded from, which it may read data files out of.</param>
internal sealed record LoadedPlugin(IFormatPlugin Plugin, FormatPluginInfo Info, string Directory)
{
    public override string ToString()
    {
        return $"{Info.DisplayName} {Info.Version}";
    }
}

/// <summary>A plugin that did not load, and why — shown in the Formats menu.</summary>
/// <param name="File">The file that was tried.</param>
/// <param name="Reason">A sentence the user can act on.</param>
internal sealed record PluginFailure(string File, string Reason);

/// <summary>What a scan of the formats folder found.</summary>
internal sealed record PluginCatalog(
    IReadOnlyList<LoadedPlugin> Plugins,
    IReadOnlyList<PluginFailure> Failures)
{
    public static PluginCatalog Empty { get; } = new([], []);

    /// <summary>Every archive format across every loaded plugin, with its owner.</summary>
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

/// <summary>
/// Finds and loads format plugins from the <c>formats/</c> folder beside the
/// executable.
///
/// <para>
/// One layout: <c>formats/Name.Formats.dll</c>, flat. A plugin is one file you can
/// add or remove by hand, and the folder is a list of what is installed. A plugin
/// shipping its own dependencies puts them in the same folder, where its
/// <c>.deps.json</c> still resolves them — at the cost of sharing one namespace of
/// filenames with every other plugin, which is a trade worth taking for how much
/// simpler it makes installing one.
/// </para>
///
/// <para>
/// <b>On what this does and does not protect against.</b> .NET has no in-process
/// sandbox: a DLL here runs with exactly the privileges the app has. What the loader
/// provides is narrower and worth stating plainly — the folder is opt-in and under
/// the user's control, nothing is ever downloaded or installed automatically, the
/// contract version is checked before a plugin is trusted, and a plugin that throws
/// is disabled rather than allowed to take the app down. Treat a format plugin exactly as you would any
/// other program you chose to run.
/// </para>
/// </summary>
internal static class PluginLoader
{
    /// <summary>The folder name, beside the executable, that plugins are read from.</summary>
    public const string FolderName = "formats";

    /// <summary>The filename suffix a plugin assembly must have.</summary>
    public const string Suffix = ".Formats.dll";

    /// <summary>The default location: <c>formats/</c> beside the running executable.</summary>
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>
    /// Loads every plugin in <paramref name="directory"/>. Never throws: a folder
    /// that is missing, unreadable, or full of things that are not plugins produces
    /// an empty or partial catalogue and a line in the log for each problem.
    /// </summary>
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
                // Deliberately broad. A plugin is third-party code loaded at run time,
                // and every one of the dozen ways it can fail — a bad image, a missing
                // dependency, a throwing constructor — has the same right answer: skip
                // it, say why, and keep the app running.
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

    /// <summary>
    /// Candidate plugin assemblies: every <c>*.Formats.dll</c> in the folder, in a
    /// stable order so the log reads the same twice running. The suffix is what keeps
    /// a plugin's own dependencies from being mistaken for plugins.
    /// </summary>
    private static IEnumerable<string> Discover(string directory)
    {
        return Directory.EnumerateFiles(directory, "*" + Suffix).Order(StringComparer.Ordinal);
    }

    private static LoadedPlugin LoadOne(string file)
    {
        string full = Path.GetFullPath(file);
        PluginLoadContext context = new(full);
        Assembly assembly = context.LoadFromAssemblyPath(full);

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

        return info.ContractVersion != PluginContract.Version
            ? throw new InvalidOperationException(
                $"it was built against plugin contract version {info.ContractVersion}, " +
                $"but this build of Omega Studio speaks version {PluginContract.Version}. " +
                "Rebuild the plugin against the matching Omega.Plugins.")
            : string.IsNullOrWhiteSpace(info.Id)
            ? throw new InvalidOperationException("its FormatPluginInfo has no Id")
            : new LoadedPlugin(plugin, info, Path.GetDirectoryName(full)!);
    }

    /// <summary>
    /// One readable sentence for a load failure.
    ///
    /// <para>
    /// Reflection wraps anything a plugin's own constructor throws in a
    /// <see cref="TargetInvocationException"/>, whose message says nothing useful; the
    /// inner exception is the one the plugin author wrote.
    /// </para>
    /// </summary>
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
