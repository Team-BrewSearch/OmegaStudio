namespace Omega.Plugins;

/// <summary>
/// The version of this contract, which every plugin states and the host checks.
///
/// <para>
/// A plugin is a separately compiled assembly the host loads at run time, so
/// nothing stops an old one being dropped beside a new host. Without this check
/// the first symptom of that mismatch is a <see cref="MissingMethodException"/>
/// thrown from somewhere unrelated. With it, the loader refuses the plugin up
/// front and says which version it wanted and which it found.
/// </para>
///
/// <para>
/// Bump this when — and only when — an existing member changes meaning, changes
/// signature, or disappears. Adding a new optional capability interface does not
/// break an older plugin: it simply does not implement the new interface, and the
/// host's <c>is</c> test comes back false, which is exactly the intended answer.
/// </para>
/// </summary>
public static class PluginContract
{
    /// <summary>The current contract version. See the remarks on <see cref="PluginContract"/>.</summary>
    public const int Version = 3;
}
