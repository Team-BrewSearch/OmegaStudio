using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private static readonly TimeSpan s_searchDebounce = TimeSpan.FromMilliseconds(200);
    private const int HitLimit = 5000;
    private CancellationTokenSource? _searchCts;
    [ObservableProperty]
    public partial string SearchFilter { get; set; } = "";
    [ObservableProperty]
    public partial bool IsSearchActive { get; set; }
    public ObservableCollection<TreeItemViewModel> SearchResults { get; } = [];
    public ObservableCollection<TreeItemViewModel> CurrentItems => IsSearchActive ? SearchResults : Roots;
    partial void OnIsSearchActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentItems));
    }
    partial void OnSearchFilterChanged(string value)
    {
        CancelSearch();
        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearchActive = false;
            SearchResults.Clear();
            ClearHighlights();
            Status = "Ready.";
            return;
        }
        _searchCts = new CancellationTokenSource();
        _ = SearchAsync(value, _searchCts.Token);
    }
    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }
    private async Task SearchAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(s_searchDebounce, token);
            Status = "Searching...";
            Stopwatch elapsed = Stopwatch.StartNew();
            (List<SearchHit> hits, int matches, bool truncated) = await Task.Run(() =>
            {
                List<SearchHit> found = [];
                bool capped = false;
                int total = 0;
                foreach (TreeItemViewModel root in Roots)
                {
                    token.ThrowIfCancellationRequested();
                    if (total >= HitLimit)
                    {
                        capped = true;
                        break;
                    }
                    int inRoot = 0;
                    if (Filter(root, ref inRoot, text, token) is { } hit)
                    {
                        found.Add(hit);
                        total += inRoot;
                    }
                }
                return (found, total, capped);
            }, token);
            token.ThrowIfCancellationRequested();
            SearchResults.Clear();
            IsSearchActive = true;
            foreach (SearchHit hit in hits)
            {
                SearchResults.Add(ToViewModel(hit, text));
            }
            Status = matches == 0
                ? "No matches found."
                : truncated
                    ? $"Found {matches}+ match{(matches == 1 ? "" : "es")} in {elapsed.ElapsedMilliseconds}ms; "
                      + $"showing the first {HitLimit}."
                    : $"Found {matches} match{(matches == 1 ? "" : "es")} in {elapsed.ElapsedMilliseconds}ms.";
        }
        catch (OperationCanceledException)
        {
        }
    }
    private sealed record SearchHit(
        TreeItemViewModel Source,
        string Header,
        string Detail,
        string Kind,
        IAssetNode? Node,
        List<SearchHit> Children);
    private SearchHit? Filter(
        TreeItemViewModel item, ref int matches, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool self = item.Header.Contains(text, StringComparison.OrdinalIgnoreCase);
        List<SearchHit> children = [];
        if (item.Node is { } node)
        {
            foreach (IAssetNode child in ChildrenOf(node, token))
            {
                token.ThrowIfCancellationRequested();
                int inChild = 0;
                TreeItemViewModel row = TreeRow(child);
                if (Filter(row, ref inChild, text, token) is { } hit)
                {
                    children.Add(hit);
                    matches += inChild;
                }
            }
        }
        if (self)
        {
            matches++;
        }
        return self || children.Count > 0
            ? new SearchHit(item, item.Header, item.Detail, item.Kind, item.Node, children)
            : null;
    }
    private static IReadOnlyList<IAssetNode> ChildrenOf(IAssetNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return PluginGuard.Try(
            $"Reading the contents of '{NodeAccess.Name(node)}'",
            () => (IReadOnlyList<IAssetNode>)[.. node.GetChildren()],
            []);
    }
    private TreeItemViewModel TreeRow(IAssetNode node)
    {
        IAssetNode shown = _recognition.Enrich(node);
        return new TreeItemViewModel(NodeAccess.Name(shown), NodeAccess.Detail(shown), NodeAccess.Resolve(shown))
        {
            Node = shown,
        };
    }
    private static TreeItemViewModel ToViewModel(SearchHit hit, string text)
    {
        TreeItemViewModel item = new(hit.Header, hit.Detail, hit.Kind)
        {
            Node = hit.Node,
            IsExpanded = true,
            Source = hit.Source,
        };
        item.UpdateSearchHighlight(text);
        foreach (SearchHit child in hit.Children)
        {
            item.Children.Add(ToViewModel(child, text));
        }
        return item;
    }
    private void ClearHighlights()
    {
        foreach (TreeItemViewModel root in Roots)
        {
            Clear(root);
        }
        static void Clear(TreeItemViewModel item)
        {
            item.UpdateSearchHighlight(null);
            foreach (TreeItemViewModel child in item.Children)
            {
                Clear(child);
            }
        }
    }
}
