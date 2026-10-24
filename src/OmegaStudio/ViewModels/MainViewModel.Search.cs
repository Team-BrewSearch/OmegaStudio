using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private static readonly TimeSpan s_searchDebounce = TimeSpan.FromMilliseconds(200);
    private const int HitLimit = 5000;
    private const int DepthLimit = 256;
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
            TreeItemViewModel[] roots = [.. Roots];
            SearchWalk walk = new(text, token);
            List<TreeItemViewModel> results;
            try
            {
                results = await _session.RunAsync(_session.Generation, _ =>
                {
                    List<TreeItemViewModel> found = [];
                    for (int index = 0; index < roots.Length && !walk.Full; index++)
                    {
                        if (Filter(roots[index], index, walk, 0) is { } hit && Resolve(hit, roots, walk) is { } row)
                        {
                            found.Add(row);
                        }
                    }
                    return found;
                });
            }
            catch (ArchiveClosedException)
            {
                return;
            }
            token.ThrowIfCancellationRequested();
            SearchResults.Clear();
            IsSearchActive = true;
            foreach (TreeItemViewModel row in results)
            {
                SearchResults.Add(row);
            }
            int matches = walk.Matches;
            string status = matches == 0
                ? "No matches found."
                : walk.Full
                    ? $"Found {matches}+ match{(matches == 1 ? "" : "es")} in {elapsed.ElapsedMilliseconds}ms; "
                      + $"showing the first {HitLimit}."
                    : $"Found {matches} match{(matches == 1 ? "" : "es")} in {elapsed.ElapsedMilliseconds}ms.";
            Status = walk.DepthTruncated
                ? $"{status} Folders deeper than {DepthLimit} levels were not searched."
                : status;
        }
        catch (OperationCanceledException)
        {
        }
    }
    private sealed class SearchWalk(string text, CancellationToken token)
    {
        public string Text { get; } = text;
        public CancellationToken Token { get; } = token;
        public int Matches { get; set; }
        public bool Full => Matches >= HitLimit;
        public bool DepthTruncated { get; set; }
    }
    private sealed record SearchHit(int Index, string Header, IReadOnlyList<SearchHit> Children);
    private static SearchHit? Filter(TreeItemViewModel item, int index, SearchWalk walk, int depth)
    {
        walk.Token.ThrowIfCancellationRequested();
        bool self = item.Header.Contains(walk.Text, StringComparison.OrdinalIgnoreCase);
        if (self)
        {
            walk.Matches++;
        }
        List<SearchHit>? children = null;
        IReadOnlyList<TreeItemViewModel> rows = item.BuildChildren();
        if (depth >= DepthLimit)
        {
            walk.DepthTruncated |= rows.Count > 0;
        }
        else
        {
            for (int position = 0; position < rows.Count && !walk.Full; position++)
            {
                if (Filter(rows[position], position, walk, depth + 1) is { } hit)
                {
                    (children ??= []).Add(hit);
                }
            }
        }
        return self || children is not null ? new SearchHit(index, item.Header, (IReadOnlyList<SearchHit>?)children ?? []) : null;
    }
    private static TreeItemViewModel? Resolve(SearchHit hit, IReadOnlyList<TreeItemViewModel> rows, SearchWalk walk)
    {
        walk.Token.ThrowIfCancellationRequested();
        if (hit.Index >= rows.Count || rows[hit.Index].Header != hit.Header)
        {
            return null;
        }
        TreeItemViewModel row = rows[hit.Index];
        TreeItemViewModel item = new(row.Header, row.Detail, row.Kind)
        {
            Node = row.Node,
            IsExpanded = true,
            Source = row,
            Highlight = walk.Text,
        };
        if (hit.Children.Count == 0)
        {
            return item;
        }
        IReadOnlyList<TreeItemViewModel> children = row.BuildChildren();
        List<TreeItemViewModel> found = [];
        foreach (SearchHit child in hit.Children)
        {
            if (Resolve(child, children, walk) is { } resolved)
            {
                found.Add(resolved);
            }
        }
        item.SetChildren(found);
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
            item.Highlight = null;
            foreach (TreeItemViewModel child in item.Children)
            {
                Clear(child);
            }
        }
    }
}
