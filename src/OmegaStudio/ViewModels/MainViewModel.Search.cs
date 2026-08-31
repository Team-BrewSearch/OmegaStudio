using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using System.Diagnostics;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Outliner search. Typing filters the tree to matching rows and their ancestors,
/// with the matched substrings highlighted.
/// </summary>
public partial class MainViewModel
{
    /// <summary>How long typing must pause before a search runs.</summary>
    private static readonly TimeSpan s_searchDebounce = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    public partial string SearchFilter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearchActive { get; set; }

    public ObservableCollection<TreeItemViewModel> SearchResults { get; } = [];

    /// <summary>What the Outliner binds to: the filtered tree, or the whole one.</summary>
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

    /// <summary>
    /// Debounces, then walks the tree off the UI thread. The walk forces lazy
    /// children to load, which is why it produces plain records rather than view
    /// models — those have to be created back on the UI thread.
    /// </summary>
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
            // Superseded by a newer keystroke.
        }
    }

    /// <summary>
    /// A matched row, flattened off the UI thread. It carries the plugin node so a hit drives
    /// the panes exactly as the row it mirrors would, and the row itself so an edit made from a
    /// search result reaches the tree rather than only the stand-in.
    /// </summary>
    private sealed record SearchHit(
        TreeItemViewModel Source,
        string Header,
        string Detail,
        string Kind,
        IAssetNode? Node,
        List<SearchHit> Children);

    /// <summary>Keeps a row if it matches, or if anything below it does.</summary>
    private static SearchHit? Filter(
        TreeItemViewModel item, ref int matches, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Build, not EnsureLoaded: this runs on the thread pool, and EnsureLoaded publishes into
        // the row's bound Children collection, which only the UI thread may touch.
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
