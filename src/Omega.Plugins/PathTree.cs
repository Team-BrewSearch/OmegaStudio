namespace Omega.Plugins;

/// <summary>
/// Builds a folder tree out of a flat list of paths.
///
/// <para>
/// Plenty of archives store every entry as a full path with no directory records —
/// tar does, and so does the Kartz GDF — so the folders have to be invented. Doing it
/// here rather than per plugin means a format that stores a path list writes no tree
/// code at all.
/// </para>
/// </summary>
public static class PathTree
{
    /// <summary>
    /// Builds the tree. Folders are created from the path segments and sorted
    /// case-insensitively; leaves come from <paramref name="leaf"/>, which is called
    /// lazily, when its folder is first expanded.
    /// </summary>
    /// <param name="items">The archive's entries, in any order.</param>
    /// <param name="pathOf">
    /// An entry's full path. Separators may be <c>/</c> or <c>\</c>; empty segments
    /// and a leading <c>./</c> are ignored, which is what a real tar writes.
    /// </param>
    /// <param name="leaf">Builds a node, given its display name, its full path, and the entry.</param>
    /// <param name="rootName">The root row's label, usually the archive's file name.</param>
    /// <param name="rootDetail">The root row's grey text. Defaults to the entry count.</param>
    public static IAssetNode Build<T>(
        IEnumerable<T> items,
        Func<T, string> pathOf,
        Func<string, string, T, IAssetNode> leaf,
        string rootName,
        string? rootDetail = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pathOf);
        ArgumentNullException.ThrowIfNull(leaf);

        Folder<T> root = new();
        foreach (T item in items)
        {
            root.Insert(Split(pathOf(item)), 0, item);
        }

        return new GroupNode(
            rootName,
            string.Empty,
            rootDetail ?? $"{root.Count} entries",
            () => root.Children(string.Empty, leaf));
    }

    /// <summary>
    /// A path as its segments, with the noise a real archive carries stripped out:
    /// mixed separators, a leading <c>./</c>, a trailing slash on a directory, and
    /// empty segments from a doubled separator.
    /// </summary>
    private static string[] Split(string path)
    {
        return path is null
            ? []
            : [.. path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")];
    }

    /// <summary>One level of the invented tree: sub-folders, and an entry if a file sits here.</summary>
    private sealed class Folder<T>
    {
        private readonly Dictionary<string, Folder<T>> _children =
            new(StringComparer.OrdinalIgnoreCase);

        private T? _entry;
        private bool _hasEntry;

        public int Count { get; private set; }

        public void Insert(string[] segments, int depth, T item)
        {
            if (depth >= segments.Length)
            {
                return;   // a path that was nothing but separators
            }

            Count++;

            string key = segments[depth];
            if (!_children.TryGetValue(key, out Folder<T>? child))
            {
                _children[key] = child = new Folder<T>();
            }

            if (depth == segments.Length - 1)
            {
                child._entry = item;
                child._hasEntry = true;
                child.Count++;
            }
            else
            {
                child.Insert(segments, depth + 1, item);
            }
        }

        public IEnumerable<IAssetNode> Children(string path, Func<string, string, T, IAssetNode> leaf)
        {
            foreach ((string name, Folder<T> child) in
                     _children.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                string childPath = path.Length == 0 ? name : $"{path}/{name}";

                // A name can be both, in an archive that stores "a" and "a/b". Rare and
                // usually malformed, but emitting only one of them would silently lose
                // whichever lost — so both get a row.
                if (child._hasEntry)
                {
                    yield return leaf(name, childPath, child._entry!);
                }

                if (child._children.Count > 0)
                {
                    yield return new GroupNode(
                        name,
                        childPath,
                        $"{child.Count} entries",
                        () => child.Children(childPath, leaf));
                }
            }
        }
    }
}
