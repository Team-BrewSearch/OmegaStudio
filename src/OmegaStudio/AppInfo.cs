using System.Reflection;
namespace OmegaStudio;

internal static class AppInfo
{
    public const string Name = "Omega Studio";
    public const string Summary =
        "A game asset browser and editor. Every format it opens comes from a plugin " +
        "in the formats folder beside it.";
    public static string Version { get; } = ReadVersion();
    private static string ReadVersion()
    {
        Assembly assembly = typeof(AppInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
        int build = informational.IndexOf('+', StringComparison.Ordinal);
        return build < 0 ? informational : informational[..build];
    }
}
