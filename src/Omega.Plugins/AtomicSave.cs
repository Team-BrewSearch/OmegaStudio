using System.Buffers;
namespace Omega.Plugins;
public static class AtomicSave
{
    public const string TempSuffix = ".omega-save.tmp";
    public const string RollbackSuffix = ".omega-save.orig";
    private const int Chunk = 1 << 20;
    public static void Write(string path, byte[] rebuilt)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(rebuilt);
        WriteToStream(path, file => file.Write(rebuilt));
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
        List<string> temporaries = [];
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ArgumentException.ThrowIfNullOrEmpty(paths[i]);
                byte[] bytes = rebuilt[i];
                temporaries.Add(WriteToStream(paths[i], file => file.Write(bytes)));
            }
            OverwriteAll(paths);
        }
        finally
        {
            temporaries.ForEach(TryDelete);
        }
    }
    public static string WriteToStream(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);
        string temporary = TempPath(path);
        try
        {
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            return temporary;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }
    [Obsolete("Use WriteToStream, which flushes to the disk itself. This will be removed at the next contract version.")]
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
    internal static void OverwriteAll(IReadOnlyList<string> paths)
    {
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
            rollbacks.ForEach(TryDelete);
        }
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
        return Compare(path, file => file.Length == rebuilt.Length && Matches(file, rebuilt));
    }
    internal static bool SameFiles(string path, string other)
    {
        return Compare(path, file => Compare(other, copy => file.Length == copy.Length && Matches(file, copy)));
    }
    private static bool Compare(string path, Func<FileStream, bool> test)
    {
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            return test(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static bool Matches(Stream file, ReadOnlySpan<byte> expected)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            while (!expected.IsEmpty)
            {
                int want = Math.Min(chunk.Length, expected.Length);
                if (file.ReadAtLeast(chunk.AsSpan(0, want), want, throwOnEndOfStream: false) != want
                    || !chunk.AsSpan(0, want).SequenceEqual(expected[..want]))
                {
                    return false;
                }
                expected = expected[want..];
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
    private static bool Matches(Stream file, Stream copy)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            int read;
            while ((read = copy.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false)) > 0)
            {
                if (!Matches(file, chunk.AsSpan(0, read)))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
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
            rollbacks.ForEach(TryDelete);
            throw;
        }
        return rollbacks;
    }
    private static void Restore(IReadOnlyList<string> paths, List<string> rollbacks)
    {
        for (int i = 0; i < paths.Count && i < rollbacks.Count; i++)
        {
            if (rollbacks[i].Length == 0 || !File.Exists(rollbacks[i]))
            {
                continue;
            }
            try
            {
                File.Copy(rollbacks[i], paths[i], overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
