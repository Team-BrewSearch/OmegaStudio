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
        if (files.Count == 0)
        {
            return;
        }
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
        }
        catch
        {
            foreach (string temporary in temporaries)
            {
                AtomicSave.TryDelete(temporary);
            }
            throw;
        }
        bool parsed = false;
        try
        {
            parsed = readsBack(temporaries);
        }
        finally
        {
            if (!parsed)
            {
                foreach (string temporary in temporaries)
                {
                    AtomicSave.TryDelete(temporary);
                }
            }
        }
        if (!parsed)
        {
            throw new InvalidDataException(
                $"what was built for {what} does not read back, so nothing was written. This is a bug in the "
                + "writer, not in the file: report it with the file that was being saved.");
        }
        List<int> changed = [];
        for (int i = 0; i < files.Count; i++)
        {
            if (!AtomicSave.Same(files[i].Path, files[i].Rebuilt))
            {
                changed.Add(i);
            }
        }
        if (changed.Count == 0)
        {
            foreach (string temporary in temporaries)
            {
                AtomicSave.TryDelete(temporary);
            }
            return;
        }
        string[] paths = [.. changed.Select(index => files[index].Path)];
        byte[][] bytes = [.. changed.Select(index => files[index].Rebuilt)];
        AtomicSave.WriteAll(paths, bytes);
        foreach (string temporary in temporaries)
        {
            AtomicSave.TryDelete(temporary);
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
                throw new InvalidDataException(
                    $"what was built for {what} does not read back, so nothing was written. This is a bug in the "
                    + "writer, not in the file: report it with the file that was being saved.");
            }
            if (AtomicSave.Same(path, ReadEverything(temporary)))
            {
                AtomicSave.TryDelete(temporary);
                return;
            }
            AtomicSave.Overwrite(path);
        }
        finally
        {
            AtomicSave.TryDelete(temporary);
        }
    }
    private static byte[] ReadEverything(string path)
    {
        using FileStream file = System.IO.File.OpenRead(path);
        byte[] bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }
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
