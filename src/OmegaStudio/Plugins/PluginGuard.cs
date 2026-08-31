using Omega.Plugins;
namespace OmegaStudio.Plugins;

internal static class PluginGuard
{
    public static Action<string>? Log { get; set; }
    public static T Try<T>(string what, Func<T> work, T fallback)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"{what} failed: {ex.Message}");
            return fallback;
        }
    }
    public static void Try(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"{what} failed: {ex.Message}");
        }
    }
    public static IReadOnlyList<IArchiveFormat> Formats(LoadedPlugin plugin)
    {
        return Try(
            $"Listing formats for {plugin.Info.DisplayName}",
            () => (IReadOnlyList<IArchiveFormat>)[.. plugin.Plugin.Archives],
            []);
    }
    public static bool CanOpen(IArchiveFormat format, string path, ReadOnlySpan<byte> head)
    {
        try
        {
            return format.CanOpen(path, head);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"{format.DisplayName} failed while identifying {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }
}
