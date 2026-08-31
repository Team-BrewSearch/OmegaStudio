using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
namespace OmegaStudio.ViewModels;

public partial class TreeItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
    private readonly Lock _loadLock = new();
    private volatile List<TreeItemViewModel>? _built;
    private bool _published;
    public TreeItemViewModel(string header, string detail = "", string kind = "")
    {
        Header = header;
        Detail = detail;
        Kind = kind;
        HeaderSegments.Add(new TextSegment(header, false));
    }
    public string Header { get; }
    [ObservableProperty]
    public partial string Kind { get; set; }
    [ObservableProperty]
    public partial string Detail { get; set; }
    public IAssetNode? Node { get; set; }
    public TreeItemViewModel? Source { get; init; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];
    public ObservableCollection<TextSegment> HeaderSegments { get; } = [];
    public Func<IEnumerable<TreeItemViewModel>>? Loader
    {
        get;
        set
        {
            field = value;
            _built = null;
            _published = false;
            Children.Clear();
            if (value is not null)
            {
                Children.Add(new TreeItemViewModel("…"));
            }
        }
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
    }
    public void EnsureLoaded()
    {
        if (_published || Loader is null)
        {
            return;
        }
        IReadOnlyList<TreeItemViewModel> items = Build();
        _published = true;
        Children.Clear();
        foreach (TreeItemViewModel child in items)
        {
            Children.Add(child);
        }
    }
    public IReadOnlyList<TreeItemViewModel> Build()
    {
        if (_built is { } already)
        {
            return already;
        }
        lock (_loadLock)
        {
            if (_built is { } raced)
            {
                return raced;
            }
            if (Loader is not { } loader)
            {
                return _built = [];
            }
            try
            {
                return _built = [.. loader()];
            }
            catch (Exception ex)
            {
                return _built = [new TreeItemViewModel("Error", ex.Message)];
            }
        }
    }
    public void UpdateSearchHighlight(string? searchText)
    {
        HeaderSegments.Clear();
        if (string.IsNullOrEmpty(Header))
        {
            HeaderSegments.Add(new TextSegment("", false));
            return;
        }
        if (string.IsNullOrEmpty(searchText))
        {
            HeaderSegments.Add(new TextSegment(Header, false));
            return;
        }
        int startIndex = 0;
        int matchIndex;
        while ((matchIndex = Header.IndexOf(searchText, startIndex, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (matchIndex > startIndex)
            {
                HeaderSegments.Add(new TextSegment(Header[startIndex..matchIndex], false));
            }
            HeaderSegments.Add(new TextSegment(Header.Substring(matchIndex, searchText.Length), true));
            startIndex = matchIndex + searchText.Length;
        }
        if (startIndex < Header.Length)
        {
            HeaderSegments.Add(new TextSegment(Header[startIndex..], false));
        }
    }
    public override string ToString()
    {
        return Header;
    }
}
