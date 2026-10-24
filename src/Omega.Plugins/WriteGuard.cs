namespace Omega.Plugins;
public static class WriteGuard
{
    public static void Commit(
        string what,
        IReadOnlyList<(string Path, byte[] Rebuilt)> files,
        Func<IReadOnlyList<string>, bool> readsBack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readsBack);
        foreach ((string path, byte[] rebuilt) in files)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException($"a {what} save named no path for one of its files", nameof(files));
            }
            if (rebuilt is null)
            {
                throw new ArgumentException($"a {what} save built no bytes for {path}", nameof(files));
            }
        }
        List<string> temporaries = [];
        try
        {
            foreach ((string path, byte[] rebuilt) in files)
            {
                temporaries.Add(AtomicSave.WriteToStream(path, stream => stream.Write(rebuilt)));
            }
            if (files.Count > 0 && !readsBack(temporaries))
            {
                throw DoesNotReadBack(what);
            }
            (string Path, string Temporary)[] changed =
            [
                .. files.Select((file, i) => (file.Path, temporaries[i], file.Rebuilt))
                    .Where(file => !AtomicSave.Same(file.Path, file.Rebuilt))
                    .Select(file => (file.Path, file.Item2)),
            ];
            if (changed.Length > 0)
            {
                AtomicSave.OverwriteAll(changed);
            }
        }
        finally
        {
            temporaries.ForEach(AtomicSave.TryDelete);
        }
    }
    public static void CommitStreaming(
        string what,
        string path,
        Action<Stream> build,
        Func<string, bool> readsBack)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(readsBack);
        string temporary = AtomicSave.WriteToStream(path, build);
        try
        {
            if (!readsBack(temporary))
            {
                throw DoesNotReadBack(what);
            }
            if (!AtomicSave.SameFiles(path, temporary))
            {
                AtomicSave.Overwrite(path, temporary);
            }
        }
        finally
        {
            AtomicSave.TryDelete(temporary);
        }
    }
    private static InvalidDataException DoesNotReadBack(string what)
    {
        return new InvalidDataException(
            $"what was built for {what} does not read back, so nothing was written. This is a bug in the "
            + "writer, not in the file: report it with the file that was being saved.");
    }
    [Obsolete("Unused. This will be removed at the next contract version.")]
    public static string Skipped(string what, IReadOnlyList<(string Path, byte[] Rebuilt)> files)
    {
        return files.Count == 1
            ? $"{what} is unchanged, so nothing was written."
            : $"{what} was rebuilt the same, so nothing was written.";
    }
    public static string Wrote(string what, IReadOnlyList<(string Path, byte[] Rebuilt)> files)
    {
        long bytes = files.Sum(file => file.Rebuilt.LongLength);
        string names = string.Join(", ", files.Select(file => System.IO.Path.GetFileName(file.Path)));
        return $"{what} saved ({Sizes.Describe(bytes)}): {names}";
    }
}
