using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
namespace OmegaStudio.ViewModels;
public partial class TreeItemViewModel : ObservableObject
{
    private static readonly PropertyChangedEventArgs s_hasChildrenChanged = new(nameof(HasChildren));
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
    [ObservableProperty]
    public partial string? Highlight { get; set; }
    private int _generation;
    private bool _published;
    private Lazy<List<TreeItemViewModel>>? _rows;
    public void EnsureLoaded()
    {
        if (_published || _rows is not { } rows)
        {
            return;
        }
        _published = true;
        SetChildren([new TreeItemViewModel("…", string.Empty, "loading")]);
        _ = BuildAsync(rows, _generation);
    }
    internal Action<string, int, long>? OnBuilt { get; set; }
    public delegate Task<List<TreeItemViewModel>> Runner(Func<List<TreeItemViewModel>> work);
    internal Runner? Run { get; init; }
    public TreeItemViewModel(string header, string detail = "", string kind = "")
    {
        Header = header;
        Detail = detail;
        Kind = kind;
    }
    public string Header { get; }
    [ObservableProperty]
    public partial string Kind { get; set; }
    [ObservableProperty]
    public partial string Detail { get; set; }
    public IAssetNode? Node { get; set; }
    public TreeItemViewModel? Source { get; init; }
    public TreeItemViewModel? Parent { get; private set; }
    public int Depth { get; private set; }
    private void Attach(TreeItemViewModel parent)
    {
        Parent = parent;
        int depth = parent.Depth + 1;
        if (depth == Depth)
        {
            return;
        }
        Depth = depth;
        foreach (TreeItemViewModel child in Children)
        {
            child.Attach(this);
        }
    }
    public IReadOnlyList<TreeItemViewModel> Children { get; private set; } = [];
    public bool HasChildren => Children.Count > 0 || (_rows is not null && !_published);
    internal OutlinerRows? Outline { get; set; }
    public Func<IEnumerable<TreeItemViewModel>>? Loader
    {
        get;
        set
        {
            field = value;
            _generation++;
            _published = false;
            _rows = value is null ? null : new Lazy<List<TreeItemViewModel>>(() => Build(value));
            SetChildren([]);
        }
    }
    public IReadOnlyList<TreeItemViewModel> BuildChildren()
    {
        return _rows?.Value ?? [];
    }
    public void SetChildren(IReadOnlyList<TreeItemViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (TreeItemViewModel row in rows)
        {
            row.Attach(this);
        }
        Children = rows;
        OnPropertyChanged(s_hasChildrenChanged);
        Changed();
    }
    public void Reload(Func<IEnumerable<TreeItemViewModel>> loader)
    {
        bool wasExpanded = IsExpanded;
        Loader = loader;
        if (wasExpanded)
        {
            EnsureLoaded();
        }
    }
    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureLoaded();
        }
        Changed();
    }
    private void Changed()
    {
        TreeItemViewModel row = this;
        while (row.Parent is { } parent)
        {
            if (!parent.IsExpanded)
            {
                return;
            }
            row = parent;
        }
        row.Outline?.Refresh(this);
    }
    private async Task BuildAsync(Lazy<List<TreeItemViewModel>> rows, int generation)
    {
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        List<TreeItemViewModel> items;
        try
        {
            items = Run is { } run ? await run(() => rows.Value) : await Task.Run(() => rows.Value);
        }
        catch (ArchiveClosedException)
        {
            return;
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation)
            {
                return;
            }
            OnBuilt?.Invoke(Header, items.Count, watch.ElapsedMilliseconds);
            SetChildren(items);
        });
    }
    private List<TreeItemViewModel> Build(Func<IEnumerable<TreeItemViewModel>> loader)
    {
        List<TreeItemViewModel> rows;
        try
        {
            rows = [.. loader()];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ArchiveClosedException)
        {
            rows = [new TreeItemViewModel("Error", ex.Message)];
        }
        foreach (TreeItemViewModel row in rows)
        {
            row.Attach(this);
        }
        return rows;
    }
    public override string ToString()
    {
        return Header;
    }
}
