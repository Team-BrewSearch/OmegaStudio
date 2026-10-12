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
    public const string Unnamed = "(unnamed)";
    private static string Tag(string stem, int suffix) =>
        string.Concat(stem, suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static string[] Split(string? path)
    {
        string[] segments = path is null
            ? []
            : [.. path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != ".")];
        return segments.Length == 0 ? [Unnamed] : segments;
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
            if (depth != segments.Length - 1)
            {
                child.Insert(segments, depth + 1, item);
            }
            else if (!child._hasEntry)
            {
                child._entry = item;
                child._hasEntry = true;
                child.Count++;
            }
            else
            {
                string stem = $"{key}_";
                int suffix = 1;
                while (_children.TryGetValue(Tag(stem, suffix), out Folder<T>? taken) && taken._hasEntry)
                {
                    suffix++;
                }
                if (!_children.TryGetValue(Tag(stem, suffix), out Folder<T>? spare))
                {
                    spare = new Folder<T>();
                    _children[Tag(stem, suffix)] = spare;
                }
                spare._entry = item;
                spare._hasEntry = true;
                spare.Count++;
            }
        }
        public IEnumerable<IAssetNode> Children(string path, Func<string, string, T, IAssetNode> leaf)
        {
            foreach ((string name, Folder<T> child) in
                     _children.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                string childPath = path.Length == 0 ? name : $"{path}/{name}";
                if (child._children.Count == 0)
                {
                    if (child._hasEntry)
                    {
                        yield return leaf(name, childPath, child._entry!);
                    }
                    continue;
                }
                yield return new GroupNode(
                    name,
                    childPath,
                    $"{child.Count} entries",
                    () => child.Rows(childPath, leaf));
            }
        }
        private IEnumerable<IAssetNode> Rows(string path, Func<string, string, T, IAssetNode> leaf)
        {
            if (_hasEntry)
            {
                yield return leaf(NestedLeafName, path, _entry!);
            }
            foreach (IAssetNode row in Children(path, leaf))
            {
                yield return row;
            }
        }
        private const string NestedLeafName = "(this folder)";
    }
}
