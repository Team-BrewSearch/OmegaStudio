namespace Omega.Plugins;

public static class PathTree
{
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
    private static string[] Split(string path)
    {
        return path is null
            ? []
            : [.. path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")];
    }
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
                return;
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
