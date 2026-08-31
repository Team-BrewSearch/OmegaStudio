using System.Reflection;

namespace OmegaStudio;

/// <summary>
/// What the app calls itself, and which build this is.
///
/// <para>
/// The version is read from the assembly rather than written here, so there is exactly
/// one place to change it: <c>&lt;Version&gt;</c> in <c>OmegaStudio.csproj</c>. The
/// build stamps that onto the executable and the About box reads it back, which means
/// a release cannot ship a number that disagrees with the file's own properties.
/// </para>
/// </summary>
internal static class AppInfo
{
    /// <summary>The product name, as the About box and the title bar show it.</summary>
    public const string Name = "Omega Studio";

    /// <summary>One line about what the app is, for the About box.</summary>
    public const string Summary =
        "A game asset browser and editor. Every format it opens comes from a plugin " +
        "in the formats folder beside it.";

    /// <summary>The version this build was stamped with, e.g. <c>1.0.0</c>.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Assembly assembly = typeof(AppInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // No informational version means no <Version> was set; the assembly version
            // is then the SDK's own 1.0.0, which is still the truth about this build.
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        // "1.0.0+9a1f2c3" — a source-revision suffix belongs in a build log, not in a
        // dialog. Only present when the build knows the commit; trimmed either way.
        int build = informational.IndexOf('+', StringComparison.Ordinal);
        return build < 0 ? informational : informational[..build];
    }
}
