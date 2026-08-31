using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// One row in the Outliner. Children are built on first expand, which keeps a
/// 133 MB archive instant to open.
/// </summary>
public partial class TreeItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    private readonly Lock _loadLock = new();

    /// <summary>The child rows, built once. Null until <see cref="Build"/> has run.</summary>
    private volatile List<TreeItemViewModel>? _built;

    /// <summary>Whether <see cref="_built"/> has been copied into <see cref="Children"/>.</summary>
    private bool _published;

    public TreeItemViewModel(string header, string detail = "", string kind = "")
    {
        Header = header;
        Detail = detail;
        Kind = kind;
        HeaderSegments.Add(new TextSegment(header, false));
    }

    public string Header { get; }

    /// <summary>
    /// Drives the row's colour dot. Settable because replacing a node's bytes can
    /// change what it holds — a raw blob swapped for a WAV becomes audio.
    /// </summary>
    [ObservableProperty]
    public partial string Kind { get; set; }

    /// <summary>
    /// The grey text after the header: a size, or a texture's dimensions. Settable
    /// because replacing a payload can change it without rebuilding the row.
    /// </summary>
    [ObservableProperty]
    public partial string Detail { get; set; }

    /// <summary>
    /// The plugin node this row stands for — or the recognised view wrapped around it.
    ///
    /// <para>
    /// Everything the panes and the context menu do is decided from which capability
    /// interfaces this implements, so it is the only thing a row needs to carry.
    /// Settable because replacing the bytes can change what they are, and the row then
    /// has to stand for a different view of the same node.
    /// </para>
    /// </summary>
    public IAssetNode? Node { get; set; }

    /// <summary>
    /// The real Outliner row this one stands in for, or null when this <i>is</i> the real tree.
    ///
    /// <para>
    /// Search results are separate rows rather than the tree's own: they carry only the
    /// matching branches, and only the matched runs of their labels. Without this link an edit
    /// made from a search result refreshed the stand-in and left the real row behind it holding
    /// a stale node, which reappeared the moment the filter was cleared.
    /// </para>
    /// </summary>
    public TreeItemViewModel? Source { get; init; }

    public ObservableCollection<TreeItemViewModel> Children { get; } = [];
    public ObservableCollection<TextSegment> HeaderSegments { get; } = [];

    /// <summary>
    /// Populates <see cref="Children"/> once, on first expand. Setting a loader
    /// also parks a placeholder child so the expander arrow is visible before load.
    ///
    /// <para>
    /// Assigning again discards whatever was built from the previous one, which is
    /// how a row recovers after the bytes underneath it are replaced.
    /// </para>
    /// </summary>
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

    /// <summary>Swaps the child loader and rebuilds the rows if they were showing.</summary>
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

    /// <summary>
    /// Copies this row's children into <see cref="Children"/>, building them first if they have
    /// not been built yet.
    ///
    /// <para>
    /// <b>UI thread only.</b> It mutates a collection the Outliner is bound to, and Avalonia
    /// raises <c>CollectionChanged</c> on whichever thread does the mutating. Anything walking
    /// the tree in the background wants <see cref="Build"/> instead.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// This row's children, built at most once and <i>without</i> touching
    /// <see cref="Children"/>.
    ///
    /// <para>
    /// Safe from any thread, which is the whole reason it exists apart from
    /// <see cref="EnsureLoaded"/>. The Outliner search walks the tree off the UI thread and has
    /// to force lazy rows to load on the way; doing that through EnsureLoaded mutated a bound
    /// collection from the pool, which Avalonia answers with a torn visual tree or an exception
    /// depending on where the renderer happened to be.
    /// </para>
    ///
    /// <para>
    /// A loader that throws is contained rather than propagated: the failure becomes one row
    /// saying why, and the rest of the tree stays usable.
    /// </para>
    /// </summary>
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
