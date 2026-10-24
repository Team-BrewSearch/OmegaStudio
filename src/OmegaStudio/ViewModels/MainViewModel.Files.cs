using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    public event Action? ResetViewRequested;
    public event Action? RequestClose;
    public Func<Task<bool>>? DiscardConfirmation { get; set; }
    private bool _asking;
    [RelayCommand]
    private async Task OpenAsync()
    {
        string? path = await FilePickers.OpenAsync(_storage, "Open archive", [OpenFilter()]);
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }
    private int _opening;
    private FileFilter OpenFilter()
    {
        string[] patterns = [.. _catalog.Formats
            .SelectMany(entry => entry.Format.Filter.Patterns)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        return patterns.Length == 0
            ? new FileFilter("All files", "*.*")
            : new FileFilter("Supported archives", patterns);
    }
    public async Task OpenPathAsync(string path)
    {
        if (Interlocked.CompareExchange(ref _opening, 1, 0) != 0)
        {
            Status = "Another archive is already opening.";
            return;
        }
        try
        {
            await OpenNowAsync(path);
        }
        finally
        {
            Interlocked.Exchange(ref _opening, 0);
        }
    }
    private async Task OpenNowAsync(string path)
    {
        if (!File.Exists(path))
        {
            Log($"{path} does not exist, so nothing was opened.");
            Status = "Open an archive to begin.";
            return;
        }
        if (!await CanDiscardAsync())
        {
            return;
        }
        await ResetWorkspaceAsync();
        Status = $"Opening {Path.GetFileName(path)}…";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        IArchive? archive = await _session.OpenAsync(path, _catalog, _recognition);
        if (archive is null)
        {
            return;
        }
        watch.Stop();
        Log($"Opened {Path.GetFileName(path)} as {archive.DisplayName} in {watch.ElapsedMilliseconds} ms.");
        int generation = _session.Generation;
        TreeItemViewModel root = _tree.ForArchive(archive, work => _session.RunAsync(generation, _ => work()));
        Roots.Add(root);
        root.IsExpanded = true;
    }
    public async Task<bool> CanDiscardAsync()
    {
        if (!_session.HasChanges || DiscardConfirmation is not { } ask)
        {
            return true;
        }
        if (_asking)
        {
            return false;
        }
        _asking = true;
        try
        {
            return await ask();
        }
        finally
        {
            _asking = false;
        }
    }
    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (!await CanDiscardAsync())
        {
            return;
        }
        await ResetWorkspaceAsync();
        Log("Closed archive.");
    }
    private async Task ResetWorkspaceAsync()
    {
        CancelSearch();
        ResetAudioState();
        Animation.Unbind();
        Task video = Video.UnbindAsync();
        Roots.Clear();
        SelectedItem = null;
        await video;
        await _session.CloseAsync();
        ClearHistory();
        Roots.Clear();
        Properties.Clear();
        Commands.Clear();
        SelectedItem = null;
        _sceneSource = null;
        ViewportScene = null;
        TexturePreview = null;
        SearchFilter = "";
        _audioInfo = null;
    }
    [RelayCommand]
    public async Task SaveAsync()
    {
        Status = "Saving…";
        await _session.SaveAsync();
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
    public async Task RunCommandAsync(AssetCommand command)
    {
        if (SelectedItem is not { Node: { } node } row || _session.Owner is not { } owner)
        {
            return;
        }
        int generation = _session.Generation;
        string title = command.Title.TrimEnd('…');
        string? path = await PickForAsync(command);
        if (path is null && command.Kind != AssetCommandKind.Immediate)
        {
            return;
        }
        if (generation != _session.Generation)
        {
            Log($"{title} was not run because its archive was closed while the file picker was open.");
            return;
        }
        PendingFlushes? pending = _session.Pending;
        HostServices services = new(owner, Log, _recognition, pending);
        AssetCommandContext context = new(node, services, path);
        bool readOnly = command.Kind == AssetCommandKind.SaveFile || command.Role == AssetCommandRole.Export;
        TreeItemViewModel target = row.Source ?? row;
        EditOutcome outcome;
        try
        {
            outcome = await _session.RunAsync(generation, _ => EditOutcome.Run(command, context, pending, readOnly));
        }
        catch (ArchiveClosedException)
        {
            Log($"{title} finished after its archive was closed, so its result was not applied.");
            return;
        }
        PublishEditOutcome(title, command.Title, row, target, node, context.Changed, outcome);
    }
    private Task<string?> PickForAsync(AssetCommand command)
    {
        IReadOnlyList<FileFilter> filters = command.Filters.Count > 0
            ? command.Filters
            : [new FileFilter("All files", "*.*")];
        string title = command.Title.TrimEnd('…', ' ');
        return command.Kind switch
        {
            AssetCommandKind.SaveFile => FilePickers.SaveAsync(
                _storage, title, command.SuggestedFileName, filters),
            AssetCommandKind.OpenFile => FilePickers.OpenAsync(_storage, title, filters),
            _ => Task.FromResult<string?>(null),
        };
    }
    private void ApplyChanges(TreeItemViewModel row, IAssetNode node, AssetChange changed)
    {
        if (changed == AssetChange.None)
        {
            return;
        }
        TreeItemViewModel target = row.Source ?? row;
        if (changed.HasFlag(AssetChange.Bytes))
        {
            node = _tree.Recognize(node);
            target.Node = node;
            target.Kind = AssetKinds.Resolve(node);
            target.Detail = NodeAccess.Detail(node);
            if (!ReferenceEquals(target, row))
            {
                row.Node = target.Node;
                row.Kind = target.Kind;
                row.Detail = target.Detail;
            }
        }
        if (changed.HasFlag(AssetChange.Children) && NodeAccess.HasChildren(node))
        {
            target.Reload(() => _tree.Children(node, target.Run));
        }
        if (changed.HasFlag(AssetChange.Scene))
        {
            if (node is ISceneAsset)
            {
                Present(node);
            }
            else
            {
                Presenter.ShowScene(_sceneSource);
            }
        }
        if (changed.HasFlag(AssetChange.Image))
        {
            Present(node);
            OnPropertyChanged(nameof(TextureStatus));
        }
        if (changed.HasFlag(AssetChange.Properties))
        {
            Presenter.RebuildProperties(node);
        }
        if (changed.HasFlag(AssetChange.Dirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        NotifyCapabilities();
    }
    public bool HasUnsavedChanges => _session.HasChanges;
    public string OpenArchiveName => _session.DisplayName ?? "The archive";
    [RelayCommand]
    private void ResetView()
    {
        ResetViewRequested?.Invoke();
    }
    [RelayCommand]
    private Task ExitAsync()
    {
        RequestClose?.Invoke();
        return Task.CompletedTask;
    }
}
