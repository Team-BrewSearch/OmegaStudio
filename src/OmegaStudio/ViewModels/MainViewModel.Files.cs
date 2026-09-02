using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;

public partial class MainViewModel
{
    public event Action? ResetViewRequested;
    public event Action? RequestClose;
    [RelayCommand]
    private async Task OpenAsync()
    {
        string? path = await FilePickers.OpenAsync(_storage, "Open archive", [OpenFilter()]);
        if (path is not null)
        {
            await OpenPathAsync(path);
        }
    }
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
    private void ResetWorkspace()
    {
        ResetAudioState();
        Animation.Unbind();
        _session.Close();
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
    private Task SaveAsync()
    {
        Status = "Saving…";
        return _session.SaveAsync();
    }
    internal async Task RunCommandAsync(AssetCommand command)
    {
        if (SelectedItem is not { Node: { } node } row)
        {
            return;
        }
        string? path = await PickForAsync(command);
        if (path is null && command.Kind != AssetCommandKind.Immediate)
        {
            return;
        }
        HostServices services = new(
            _session.Owner ?? throw new InvalidOperationException("no archive is open"),
            Log,
            _recognition);
        AssetCommandContext context = new(node, services, path);
        AssetSnapshot? before = command.Kind == AssetCommandKind.SaveFile
            ? null
            : AssetSnapshot.Capture(node);
        try
        {
            await Task.Run(() => command.Execute(context));
        }
        catch (Exception ex)
        {
            Log($"{command.Title.TrimEnd('…')} failed: {ex.Message}");
            return;
        }
        ApplyChanges(row, node, context.Changed);
        RecordEdit(command.Title, row, before, context.Changed);
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
            target.Reload(() => _tree.Children(node));
        }
        if (changed.HasFlag(AssetChange.Scene))
        {
            ShowScene(node is ISceneAsset ? node : _sceneSource);
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
    public bool HasUnsavedChanges => _session.HasChanges;
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
