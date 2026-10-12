namespace Omega.Plugins;
public static class AtomicSave
{
    public const string TempSuffix = ".omega-save.tmp";
    public const string RollbackSuffix = ".omega-save.orig";
    public static void Write(string path, byte[] rebuilt)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(rebuilt);
        WriteToTemp(path, rebuilt);
        Overwrite(path);
    }
    public static void WriteAll(IReadOnlyList<string> paths, IReadOnlyList<byte[]> rebuilt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(rebuilt);
        if (paths.Count != rebuilt.Count)
        {
            throw new ArgumentException(
                $"{paths.Count} path(s) were named for {rebuilt.Count} replacement(s)", nameof(rebuilt));
        }
        if (paths.Count == 0)
        {
            return;
        }
        for (int i = 0; i < paths.Count; i++)
        {
            ArgumentException.ThrowIfNullOrEmpty(paths[i]);
            WriteToTemp(paths[i], rebuilt[i]);
        }
        if (paths.Count == 1)
        {
            Overwrite(paths[0]);
            return;
        }
        List<string> rollbacks = TakeRollbackCopies(paths);
        try
        {
            foreach (string path in paths)
            {
                Overwrite(path);
            }
        }
        catch
        {
            Restore(paths, rollbacks);
            throw;
        }
        finally
        {
            foreach (string rollback in rollbacks)
            {
                TryDelete(rollback);
            }
        }
    }
    public static string WriteToStream(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);
        string temporary = TempPath(path);
        try
        {
            using (FileStream file = OpenTemporary(temporary))
            {
                write(file);
            }
            return temporary;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }
    public static void FlushToDisk(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Flush();
        if (stream is FileStream file && file.SafeFileHandle is { } handle && handle.IsClosed is false)
        {
            file.Flush(flushToDisk: true);
        }
    }
    public static void Overwrite(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string temporary = TempPath(path);
        if (!File.Exists(temporary))
        {
            throw new InvalidOperationException(
                $"nothing has been written for {System.IO.Path.GetFileName(path)} yet");
        }
        File.Move(temporary, path, overwrite: true);
    }
    public static string TempPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        return System.IO.Path.Combine(folder ?? ".", System.IO.Path.GetFileName(path) + TempSuffix);
    }
    public static bool Same(string path, byte[] rebuilt)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(rebuilt);
        try
        {
            return new FileInfo(path) is { Length: long length } info
                && length == rebuilt.Length
                && ReadAll(path, rebuilt.Length).AsSpan().SequenceEqual(rebuilt);
            static byte[] ReadAll(string at, long length)
            {
                byte[] bytes = new byte[length];
                using FileStream file = System.IO.File.OpenRead(at);
                file.ReadExactly(bytes);
                return bytes;
            }
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
    private static void WriteToTemp(string path, byte[] rebuilt)
    {
        using FileStream file = OpenTemporary(TempPath(path));
        file.Write(rebuilt);
        file.Flush(flushToDisk: true);
    }
    private static FileStream OpenTemporary(string path)
    {
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    }
    private static List<string> TakeRollbackCopies(IReadOnlyList<string> paths)
    {
        List<string> rollbacks = [];
        try
        {
            foreach (string path in paths)
            {
                if (!File.Exists(path))
                {
                    rollbacks.Add(string.Empty);
                    continue;
                }
                string rollback = path + RollbackSuffix;
                File.Copy(path, rollback, overwrite: true);
                using FileStream copy = System.IO.File.Open(rollback, FileMode.Open, FileAccess.ReadWrite);
                copy.Flush(flushToDisk: true);
                rollbacks.Add(rollback);
            }
        }
        catch
        {
            foreach (string rollback in rollbacks)
            {
                TryDelete(rollback);
            }
            throw;
        }
        return rollbacks;
    }
    private static void Restore(IReadOnlyList<string> paths, List<string> rollbacks)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            string rollback = i < rollbacks.Count ? rollbacks[i] : string.Empty;
            if (string.IsNullOrEmpty(rollback) || !File.Exists(rollback))
            {
                continue;
            }
            try
            {
                File.Copy(rollback, paths[i], overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
