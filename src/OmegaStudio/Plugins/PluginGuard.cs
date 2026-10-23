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
            Report(what, ex);
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
            Report(what, ex);
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
            Report($"Identifying {Path.GetFileName(path)} as {format.DisplayName}", ex);
            return false;
        }
    }
    internal static void Report(string what, Exception ex)
    {
        string trace = ex.StackTrace is { Length: > 0 } stack ? $"  {stack.Replace("\n", "\n  ", StringComparison.Ordinal)}" : string.Empty;
        Log?.Invoke($"{what} failed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{trace}");
    }
}
