using System.Collections.Specialized;
using Avalonia.Collections;
namespace OmegaStudio.ViewModels;
public sealed class OutlinerRows : AvaloniaList<TreeItemViewModel>
{
    private IReadOnlyList<TreeItemViewModel> _roots = [];
    public event Action? Refreshed;
    public void Show(IReadOnlyList<TreeItemViewModel>? roots)
    {
        if (_roots is INotifyCollectionChanged old)
        {
            old.CollectionChanged -= OnRootsChanged;
        }
        foreach (TreeItemViewModel root in _roots)
        {
            root.Outline = null;
        }
        _roots = roots ?? [];
        if (_roots is INotifyCollectionChanged now)
        {
            now.CollectionChanged += OnRootsChanged;
        }
        Rebuild();
    }
    public int Reveal(TreeItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        TreeItemViewModel root = row;
        while (root.Parent is { } parent)
        {
            root = parent;
        }
        if (!_roots.Contains(root))
        {
            return -1;
        }
        for (TreeItemViewModel? up = row.Parent; up is not null; up = up.Parent)
        {
            up.IsExpanded = true;
        }
        return IndexOf(row);
    }
    internal void Refresh(TreeItemViewModel row)
    {
        int index = IndexOf(row);
        if (index < 0)
        {
            return;
        }
        int depth = row.Depth;
        int end = index + 1;
        while (end < Count && this[end].Depth > depth)
        {
            end++;
        }
        List<TreeItemViewModel> rows = [];
        AddVisible(row, rows);
        int old = end - index - 1;
        bool same = old == rows.Count;
        for (int i = 0; same && i < old; i++)
        {
            same = ReferenceEquals(this[index + 1 + i], rows[i]);
        }
        if (same)
        {
            return;
        }
        RemoveRange(index + 1, old);
        InsertRange(index + 1, rows);
        Refreshed?.Invoke();
    }
    private void OnRootsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rebuild();
    }
    private void Rebuild()
    {
        List<TreeItemViewModel> rows = [];
        foreach (TreeItemViewModel root in _roots)
        {
            root.Outline = this;
            rows.Add(root);
            AddVisible(root, rows);
        }
        Clear();
        AddRange(rows);
        Refreshed?.Invoke();
    }
    private static void AddVisible(TreeItemViewModel row, List<TreeItemViewModel> rows)
    {
        if (!row.IsExpanded)
        {
            return;
        }
        foreach (TreeItemViewModel child in row.Children)
        {
            rows.Add(child);
            AddVisible(child, rows);
        }
    }
}
