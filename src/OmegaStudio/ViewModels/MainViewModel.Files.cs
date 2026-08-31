using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
using OmegaStudio.Services;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Opening, saving, and running the commands a node offers.
///
/// <para>
/// There is one command runner here rather than one method per operation. Extract,
/// replace, export and every format-specific import a plugin adds all share the same
/// three steps — pick a file, do the work off the UI thread, refresh what changed —
/// and the only thing that differs between them is the middle one, which is the part
/// the <see cref="AssetCommand"/> carries.
/// </para>
/// </summary>
public partial class MainViewModel
{
    public event Action? ResetViewRequested;
    public event Action? RequestClose;

    // ---- open and save ----------------------------------------------------

    [RelayCommand]
    private async Task OpenAsync()
    {
        (string label, string[] patterns) = OpenFilter();

        string? path = await FilePickers.OpenAsync(_storage, "Open archive", label, patterns);
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }

    /// <summary>
    /// The Open dialog's filter, pooled across every loaded format.
    ///
    /// <para>
    /// Built from the plugins rather than hard-coded, which is why dropping a new
    /// plugin into <c>formats/</c> makes its files selectable without anything else
    /// changing.
    /// </para>
    /// </summary>
    private (string Label, string[] Patterns) OpenFilter()
    {
        string[] patterns = [.. _catalog.Formats
            .SelectMany(entry => entry.Format.Filter.Patterns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

        return patterns.Length == 0
            ? ("All files", ["*.*"])
            : ("Supported archives", patterns);
    }

    /// <summary>Opens an archive by path; also used for command-line arguments.</summary>
    public async Task OpenPathAsync(string path)
    {
        ResetWorkspace();
        Status = $"Opening {Path.GetFileName(path)}…";

        IArchive? archive = await _session.OpenAsync(path, _catalog, _recognition);
        if (archive is null)
        {
            return;
        }

        TreeItemViewModel root = _tree.ForArchive(archive);
        Roots.Add(root);
        root.IsExpanded = true;
    }

    [RelayCommand]
    private void CloseAll()
    {
        ResetWorkspace();
        Log("Closed archive.");
    }

    /// <summary>
    /// Returns the window to its empty state. Opening and closing both need this, and
    /// each used to carry its own copy of the list.
    /// </summary>
    private void ResetWorkspace()
    {
        ResetAudioState();
        Animation.Unbind();
        _session.Close();

        // The history describes the archive that is going away, and its entries hold
        // that archive's payloads.
        ClearHistory();

        Roots.Clear();
        Properties.Clear();
        Commands.Clear();
        SelectedItem = null;
        ViewportScene = null;
        TexturePreview = null;
        SearchFilter = "";
        _audioInfo = null;
    }

    [RelayCommand]
    private Task SaveAsync()
    {
        Status = "Saving…";
        return _session.SaveAsync();
    }

    // ---- running a node's commands ----------------------------------------

    /// <summary>
    /// Runs one command: picker, then the work off the UI thread, then a refresh of
    /// exactly what it says it changed.
    /// </summary>
    internal async Task RunCommandAsync(AssetCommand command)
    {
        if (SelectedItem is not { Node: { } node } row)
        {
            return;
        }

        string? path = await PickForAsync(command);
        if (path is null && command.Kind != AssetCommandKind.Immediate)
        {
            return;   // cancelled
        }

        HostServices services = new(
            _session.Owner ?? throw new InvalidOperationException("no archive is open"),
            Log,
            _recognition);

        AssetCommandContext context = new(node, services, path);

        // What the node holds now, so the edit can be taken back. Skipped for an export,
        // which writes a file and leaves the asset alone: copying a 133 MB payload to
        // record a change that cannot happen is pure cost.
        AssetSnapshot? before = command.Kind == AssetCommandKind.SaveFile
            ? null
            : AssetSnapshot.Capture(node);

        try
        {
            await Task.Run(() => command.Execute(context));
        }
        catch (Exception ex)
        {
            // Commands are plugin code doing file I/O on user-chosen paths; both halves
            // fail routinely and neither should cost more than the command itself.
            Log($"{command.Title.TrimEnd('…')} failed: {ex.Message}");
            return;
        }

        ApplyChanges(row, node, context.Changed);
        RecordEdit(command.Title, row, before, context.Changed);
    }

    private Task<string?> PickForAsync(AssetCommand command)
    {
        FileFilter filter = command.Filter ?? new FileFilter("All files", "*.*");
        string title = command.Title.TrimEnd('…', ' ');

        return command.Kind switch
        {
            AssetCommandKind.SaveFile => FilePickers.SaveAsync(
                _storage, title, command.SuggestedFileName, filter.PrimaryExtension, filter.Label),

            AssetCommandKind.OpenFile => FilePickers.OpenAsync(
                _storage, title, filter.Label, [.. filter.Patterns]),

            _ => Task.FromResult<string?>(null),
        };
    }

    /// <summary>
    /// Refreshes the views a command invalidated, and only those.
    ///
    /// <para>
    /// Driven by what the command reported rather than rebuilding everything: only
    /// the plugin knows whether rewriting a blob invalidated the rows beneath it, and
    /// re-reading the whole tree after every edit would throw away the lazy loading
    /// that keeps a 133 MB archive usable.
    /// </para>
    /// </summary>
    private void ApplyChanges(TreeItemViewModel row, IAssetNode node, AssetChange changed)
    {
        if (changed == AssetChange.None)
        {
            return;
        }

        // A search result is a stand-in for a row in the real tree, so the edit belongs to the
        // row behind it — that is the one the Outliner shows again once the filter is cleared,
        // and the only one that has a child loader to rebuild.
        TreeItemViewModel target = row.Source ?? row;

        if (changed.HasFlag(AssetChange.Bytes))
        {
            // Re-read what the bytes now are. Replacing a raw blob with a WAV should
            // give the row a transport, not leave it raw until the archive is reopened.
            node = _tree.Recognize(node);
            target.Node = node;
            target.Kind = AssetKinds.Resolve(node);
            target.Detail = node.Detail;

            if (!ReferenceEquals(target, row))
            {
                row.Node = target.Node;
                row.Kind = target.Kind;
                row.Detail = target.Detail;
            }
        }

        if (changed.HasFlag(AssetChange.Children) && node.HasChildren)
        {
            target.Reload(() => node.GetChildren().Select(_tree.ForNode));
        }

        if (changed.HasFlag(AssetChange.Scene))
        {
            ShowScene(node);
        }

        if (changed.HasFlag(AssetChange.Image))
        {
            ShowImage(node);
            OnPropertyChanged(nameof(TextureStatus));
        }

        if (changed.HasFlag(AssetChange.Properties))
        {
            RebuildProperties(node);
        }

        if (changed.HasFlag(AssetChange.Dirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }

        NotifyCapabilities();
    }

    /// <summary>Whether the open archive has edits waiting for a save.</summary>
    public bool HasUnsavedChanges => _session.HasChanges;

    // ---- view ------------------------------------------------------------

    [RelayCommand]
    private void ResetView()
    {
        ResetViewRequested?.Invoke();
    }

    [RelayCommand]
    private void Exit()
    {
        RequestClose?.Invoke();
    }
}
