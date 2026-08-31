using Omega.Plugins;

namespace OmegaStudio.Plugins;

/// <summary>
/// Every call from the host into a plugin, wrapped.
///
/// <para>
/// A plugin is third-party code parsing files it has reverse engineered, so it will
/// eventually throw on something. The rule is that this can cost the user the one
/// thing they were doing and never the session: an unreadable node shows an error
/// row, a broken sniff means that format simply does not claim the file, and a
/// throwing property leaves the pane empty. None of them close the window.
/// </para>
///
/// <para>
/// The catches are deliberately broad. Narrowing them by exception type would mean
/// guessing which types a plugin author will use — and getting that guess wrong
/// turns a contained failure back into a crash, which is the exact outcome this
/// exists to prevent.
/// </para>
/// </summary>
internal static class PluginGuard
{
    /// <summary>Where a contained failure is reported. Set once, at startup.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Runs <paramref name="work"/>, returning <paramref name="fallback"/> and logging
    /// if it throws.
    /// </summary>
    public static T Try<T>(string what, Func<T> work, T fallback)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{what} failed: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>Runs <paramref name="work"/>, logging and swallowing anything it throws.</summary>
    public static void Try(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{what} failed: {ex.Message}");
        }
    }

    /// <summary>A plugin's archive formats, or none if asking throws.</summary>
    public static IReadOnlyList<IArchiveFormat> Formats(LoadedPlugin plugin)
    {
        return Try(
            $"Listing formats for {plugin.Info.DisplayName}",
            () => (IReadOnlyList<IArchiveFormat>)[.. plugin.Plugin.Archives],
            []);
    }

    /// <summary>
    /// Whether a format claims a file. A format that throws while sniffing is treated
    /// as saying no — the file may well belong to a different plugin that works.
    /// </summary>
    public static bool CanOpen(IArchiveFormat format, string path, ReadOnlySpan<byte> head)
    {
        try
        {
            return format.CanOpen(path, head);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"{format.DisplayName} failed while identifying {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
