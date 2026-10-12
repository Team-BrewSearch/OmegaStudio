using System.Globalization;
using System.IO;
using System.Text;
namespace OmegaStudio.Diagnostics;
internal static class FileLog
{
    private const long MaxBytes = 4L * 1024 * 1024;
    private const int Generations = 2;
    private static readonly Lock s_gate = new();
    private static string? _path;
    private static long _size;
    public static string? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "omega-studio.log");
            _size = File.Exists(path) ? new FileInfo(path).Length : 0;
            _path = path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null;
            return null;
        }
    }
    public static void Write(string line)
    {
        lock (s_gate)
        {
            if (_path is not { } path)
            {
                return;
            }
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string text = $"[{stamp}] {line}{Environment.NewLine}";
                File.AppendAllText(path, text, Encoding.UTF8);
                _size += text.Length;
                if (_size > MaxBytes)
                {
                    Roll(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
    public static string? Path => _path;
    private static void Roll(string path)
    {
        try
        {
            string oldest = $"{path}.{Generations}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }
            for (int generation = Generations - 1; generation >= 1; generation--)
            {
                string from = $"{path}.{generation}";
                if (File.Exists(from))
                {
                    File.Move(from, $"{path}.{generation + 1}", overwrite: true);
                }
            }
            File.Move(path, $"{path}.1", overwrite: true);
            _size = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _size = 0;
        }
    }
}
internal static class CrashLog
{
    public static void Install(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            report(Describe("UI thread", args.Exception));
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            report(Describe("app domain", args.ExceptionObject as Exception));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            report(Describe("task", args.Exception));
            args.SetObserved();
        };
    }
    private static string Describe(string where, Exception? ex)
    {
        if (ex is null)
        {
            return $"An unhandled failure on the {where} carried no exception object.";
        }
        string trace = ex.StackTrace is { Length: > 0 } stack
            ? $"  {stack.Replace("\n", "\n  ", StringComparison.Ordinal)}"
            : string.Empty;
        return string.Concat(
            "Unhandled on the ",
            where,
            ": ",
            ex.GetType().FullName,
            ": ",
            ex.Message,
            Environment.NewLine,
            trace);
    }
}
