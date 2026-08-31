using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using System.Diagnostics;
namespace OmegaStudio.ViewModels;

public partial class MainViewModel
{
    private static readonly TimeSpan s_searchDebounce = TimeSpan.FromMilliseconds(200);
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
        _searchCts?.Dispose();
        _searchCts = null;
    }
    private async Task SearchAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(s_searchDebounce, token);
            Status = "Searching...";
            Stopwatch elapsed = Stopwatch.StartNew();
            (List<SearchHit> hits, int matches) = await Task.Run(() =>
            {
                List<SearchHit> found = [];
                int total = 0;
                foreach (TreeItemViewModel root in Roots)
                {
                    token.ThrowIfCancellationRequested();
                    int inRoot = 0;
                    if (Filter(root, ref inRoot, text, token) is { } hit)
                    {
                        found.Add(hit);
                        total += inRoot;
                    }
                }
                return (found, total);
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
    private static SearchHit? Filter(
        TreeItemViewModel item, ref int matches, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool self = item.Header.Contains(text, StringComparison.OrdinalIgnoreCase);
        List<SearchHit> children = [];
        foreach (TreeItemViewModel child in item.Build())
        {
            token.ThrowIfCancellationRequested();
            int inChild = 0;
            if (Filter(child, ref inChild, text, token) is { } hit)
            {
                children.Add(hit);
                matches += inChild;
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
