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
    private static StreamWriter? _writer;
    private static long _limit = MaxBytes;
    public static string? Open(string directory)
    {
        lock (s_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, "omega-studio.log");
                _writer?.Dispose();
                _writer = Writer(path);
                _path = path;
                _limit = MaxBytes;
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null;
                _path = null;
                return null;
            }
        }
    }
    public static void Write(string line)
    {
        lock (s_gate)
        {
            if (_writer is not { } writer || _path is not { } path)
            {
                return;
            }
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _ = writer.BaseStream.Seek(0, SeekOrigin.End);
                writer.WriteLine($"[{stamp}] {line}");
                if (writer.BaseStream.Position > _limit)
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
    private static StreamWriter Writer(string path)
    {
        FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _ = stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    }
    private static void Roll(string path)
    {
        _writer?.Dispose();
        _writer = null;
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _writer = Writer(path);
        _limit = _writer.BaseStream.Position + MaxBytes;
    }
}
internal static class CrashLog
{
    public static void Install(Action<string> report, Func<string> context)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(context);
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            report(Describe("UI thread", args.Exception, context));
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            FileLog.Write(Describe("app domain", args.ExceptionObject as Exception, context));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            report(Describe("task", args.Exception, context));
            args.SetObserved();
        };
    }
    private static string Describe(string where, Exception? ex, Func<string> context)
    {
        if (ex is null)
        {
            return $"An unhandled failure on the {where} carried no exception object. {Context(context)}";
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
            "  ",
            Context(context),
            Environment.NewLine,
            trace);
    }
    private static string Context(Func<string> context)
    {
        try
        {
            return context();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"The app state could not be read ({ex.GetType().Name}).";
        }
    }
}
