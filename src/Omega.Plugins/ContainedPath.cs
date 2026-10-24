namespace Omega.Plugins;
public static class ContainedPath
{
    public static string Resolve(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(relative);
        string top = Path.GetFullPath(root);
        string prefix = Path.EndsInDirectorySeparator(top) ? top : top + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"'{relative}' is an absolute path, not one inside {root}");
        }
        string full = Path.GetFullPath(Path.Combine(top, relative));
        if (!full.StartsWith(prefix, AtomicSave.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{relative}' points outside {root}");
        }
        string walked = top;
        foreach (string part in full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, part);
            if (IsLink(walked))
            {
                throw new InvalidDataException($"'{relative}' passes through a link at {walked}, which could lead outside {root}");
            }
        }
        return full;
    }
    public static string CreateDirectory(string root, string relative)
    {
        string full = Resolve(root, relative);
        Directory.CreateDirectory(full);
        return Resolve(root, relative);
    }
    public static void Write(string root, string relative, byte[] data)
    {
        string full = Resolve(root, relative);
        string folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        full = Resolve(root, relative);
        AtomicSave.Write(full, data);
    }
    public static void RequireDistinct(IEnumerable<string> relatives)
    {
        ArgumentNullException.ThrowIfNull(relatives);
        HashSet<string> seen = new(AtomicSave.PathComparer);
        foreach (string relative in relatives)
        {
            string normal = relative.Replace('\\', '/').Trim('/');
            if (!seen.Add(normal))
            {
                throw new InvalidDataException($"two entries both extract to '{relative}', so nothing was extracted");
            }
        }
    }
    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
