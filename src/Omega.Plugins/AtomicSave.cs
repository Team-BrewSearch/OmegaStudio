using System.Buffers;
using System.Collections.Concurrent;
namespace Omega.Plugins;
public static class AtomicSave
{
    public const string TempSuffix = ".omega-save.tmp";
    public const string RollbackSuffix = ".omega-save.orig";
    private const int Chunk = 1 << 20;
    private static readonly ConcurrentDictionary<string, string> s_pending = new(PathComparer);
    private static readonly Lock s_commit = new();
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static void Write(string path, byte[] rebuilt)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(rebuilt);
        string temporary = WriteToStream(path, file => file.Write(rebuilt));
        try
        {
            Overwrite(path, temporary);
        }
        finally
        {
            TryDelete(temporary);
        }
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
        List<(string Path, string Temporary)> prepared = [];
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                ArgumentException.ThrowIfNullOrEmpty(paths[i]);
                byte[] bytes = rebuilt[i];
                prepared.Add((paths[i], WriteToStream(paths[i], file => file.Write(bytes))));
            }
            OverwriteAll(prepared);
        }
        finally
        {
            prepared.ForEach(entry => TryDelete(entry.Temporary));
        }
    }
    public static string WriteToStream(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);
        string temporary = TempPath(path);
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            s_pending[System.IO.Path.GetFullPath(path)] = temporary;
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
        if (!s_pending.TryGetValue(System.IO.Path.GetFullPath(path), out string? temporary) || !File.Exists(temporary))
        {
            throw new InvalidOperationException(
                $"nothing has been written for {System.IO.Path.GetFileName(path)} yet");
        }
        Overwrite(path, temporary);
    }
    public static void Overwrite(string path, string temporary)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(temporary);
        if (!File.Exists(temporary))
        {
            throw new InvalidOperationException(
                $"nothing has been written for {System.IO.Path.GetFileName(path)} yet");
        }
        lock (s_commit)
        {
            File.Move(temporary, path, overwrite: true);
        }
        s_pending.TryRemove(new KeyValuePair<string, string>(System.IO.Path.GetFullPath(path), temporary));
    }
    internal static void OverwriteAll(IReadOnlyList<(string Path, string Temporary)> targets)
    {
        if (targets.Select(target => System.IO.Path.GetFullPath(target.Path)).Distinct(PathComparer).Count() != targets.Count)
        {
            throw new ArgumentException("one save names the same file twice", nameof(targets));
        }
        if (targets.Count == 1)
        {
            Overwrite(targets[0].Path, targets[0].Temporary);
            return;
        }
        lock (s_commit)
        {
            List<Rollback> rollbacks = TakeRollbackCopies(targets);
            int applied = 0;
            try
            {
                foreach ((string path, string temporary) in targets)
                {
                    if (!File.Exists(temporary))
                    {
                        throw new InvalidOperationException(
                            $"nothing has been written for {System.IO.Path.GetFileName(path)} yet");
                    }
                    File.Move(temporary, path, overwrite: true);
                    applied++;
                }
            }
            catch (Exception failure)
            {
                Restore(rollbacks, applied, failure);
                throw;
            }
            rollbacks.ForEach(rollback => TryDelete(rollback.Copy ?? string.Empty));
        }
    }
    public static string TempPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return UniqueSibling(path, TempSuffix);
    }
    private static string UniqueSibling(string path, string suffix)
    {
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        return System.IO.Path.Combine(
            folder ?? ".",
            System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..12] + suffix);
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
    private sealed record Rollback(string Path, string? Copy);
    private static List<Rollback> TakeRollbackCopies(IReadOnlyList<(string Path, string Temporary)> targets)
    {
        List<Rollback> rollbacks = [];
        try
        {
            foreach ((string path, _) in targets)
            {
                if (!File.Exists(path))
                {
                    rollbacks.Add(new Rollback(path, null));
                    continue;
                }
                string copy = UniqueSibling(path, RollbackSuffix);
                File.Copy(path, copy, overwrite: false);
                using (FileStream flush = File.Open(copy, FileMode.Open, FileAccess.ReadWrite))
                {
                    flush.Flush(flushToDisk: true);
                }
                rollbacks.Add(new Rollback(path, copy));
            }
        }
        catch
        {
            rollbacks.ForEach(rollback => TryDelete(rollback.Copy ?? string.Empty));
            throw;
        }
        return rollbacks;
    }
    private static void Restore(List<Rollback> rollbacks, int applied, Exception failure)
    {
        List<string> problems = [];
        for (int i = applied - 1; i >= 0; i--)
        {
            Rollback rollback = rollbacks[i];
            try
            {
                if (rollback.Copy is null)
                {
                    File.Delete(rollback.Path);
                }
                else
                {
                    File.Copy(rollback.Copy, rollback.Path, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(rollback.Copy is null
                    ? $"{rollback.Path} was written and could not be removed again ({ex.Message})"
                    : $"{rollback.Path} could not be put back; its original is kept at {rollback.Copy} ({ex.Message})");
            }
        }
        for (int i = 0; i < rollbacks.Count; i++)
        {
            if (rollbacks[i].Copy is { } copy && !problems.Any(problem => problem.Contains(copy, StringComparison.Ordinal)))
            {
                TryDelete(copy);
            }
        }
        if (problems.Count > 0)
        {
            throw new IOException(
                $"saving failed ({failure.Message}), and undoing the part already written also failed: "
                + string.Join("; ", problems),
                failure);
        }
    }
}
