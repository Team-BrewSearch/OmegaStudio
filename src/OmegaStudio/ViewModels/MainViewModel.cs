using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
using OmegaStudio.Rendering;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel : ObservableObject, ISelectionView, ICommandRunner, IDisposable
{
    private readonly IAppStorageProvider _storage;
    private readonly ArchiveSession _session = new();
    private readonly AudioPlaybackService _audioPlayer = new();
    private readonly ConsoleLog _console = new();
    private PluginCatalog _catalog = PluginCatalog.Empty;
    private AssetRecognition _recognition = AssetRecognition.None;
    private TreeBuilder _tree = new(AssetRecognition.None);
    private IAudioAsset? _currentAudio;
    private IAssetNode? _sceneSource;
    private Task? _audioLoad;
    private AudioInfo? _audioInfo;
    AudioInfo? ISelectionView.AudioInfo
    {
        get => _audioInfo;
        set => _audioInfo = value;
    }
    IAssetNode? ISelectionView.SceneSource
    {
        get => _sceneSource;
        set => _sceneSource = value;
    }
    TreeItemViewModel? ISelectionView.Owner => SelectedItem;
    Action<string> ISelectionView.Log => Log;
    private bool _disposed;
    public MainViewModel(IAppStorageProvider storage)
    {
        _storage = storage;
        _session.Logged += Log;
        _console.PropertyChanged += OnConsoleChanged;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        Animation = new AnimationPlayer();
        Video = new VideoPlayer(_audioPlayer, LoadSoundtrackAsync, Log);
        Animation.FrameReady += (positions, normals) => Viewport?.SetAnimatedFrame(positions, normals);
        Animation.FrameCleared += () => Viewport?.ClearAnimatedFrame();
        Animation.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName is (nameof(AnimationPlayer.SelectedClip))
                or (nameof(AnimationPlayer.Fps)))
            {
                _presenter?.RefreshCommands();
            }
        };
        PluginGuard.Log = Log;
        LoadPlugins();
    }
    public GlViewport? Viewport { get; set; }
    public AnimationPlayer Animation { get; }
    public VideoPlayer Video { get; }
    [ObservableProperty]
    public partial TreeItemViewModel? SelectedItem { get; set; }
    [ObservableProperty]
    public partial SceneGeometry? ViewportScene { get; set; }
    [ObservableProperty]
    public partial int SelectedPane { get; set; }
    [ObservableProperty]
    public partial ShadingMode Shading { get; set; } = ShadingMode.Textured;
    [ObservableProperty]
    public partial string Status { get; set; } = "Open an archive to begin.";
    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];
    public ObservableCollection<PropertyItem> Properties { get; } = [];
    public ObservableCollection<AssetCommandViewModel> Commands { get; } = [];
    public string ConsoleText => _console.Text;
    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();
    public Bitmap? TexturePreview
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }
            Bitmap? previous = field;
            _ = SetProperty(ref field, value);
            previous?.Dispose();
        }
    }
    public ObservableCollection<PluginRow> LoadedFormats { get; } = [];
    public ObservableCollection<string> FailedFormats { get; } = [];
    public bool HasFailedFormats => FailedFormats.Count > 0;
    public string FormatsFolder { get; } = PluginLoader.DefaultDirectory;
    private static string? AllowList()
    {
        string path = PluginLoader.AllowListPath;
        return File.Exists(path) ? path : null;
    }
    private void LoadPlugins()
    {
        _catalog = PluginLoader.Load(FormatsFolder, Log, AllowList());
        _recognition = AssetRecognition.From(_catalog, Log);
        _tree = new TreeBuilder(_recognition, Log);
        foreach (LoadedPlugin plugin in _catalog.Plugins)
        {
            LoadedFormats.Add(PluginRow.For(plugin));
        }
        foreach (PluginFailure failure in _catalog.Failures)
        {
            FailedFormats.Add($"{failure.File}: {failure.Reason}");
        }
        OnPropertyChanged(nameof(HasFailedFormats));
        Status = _catalog.Plugins.Count == 0
            ? $"No format plugins loaded. Put one in {FormatsFolder}."
            : $"Omega Studio ready — {_catalog.Plugins.Count} format plugin(s) loaded.";
        Log(Status);
    }
    private IAssetNode? Node => SelectedItem?.Node;
    public bool HasScene => ViewportScene is not null;
    public bool HasImage => TexturePreview is not null;
    public bool HasAudio => Node is IAudioAsset;
    public bool HasVideo => Node is IVideoAsset;
    public bool HasAnimation => Animation.IsAvailable;
    public bool HasCommands => Commands.Count > 0;
    public bool IsPresenting => _presenter?.IsPresenting ?? false;
    public string TextureStatus { get; set; } = SelectionPresenter.NoTexture;
    private SelectionPresenter? _presenter;
    private SelectionPresenter Presenter => _presenter ??= new SelectionPresenter(this);
    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        Present(value?.Node);
    }
    public void Present(IAssetNode? node)
    {
        if (node is null || ReferenceEquals(node, Node))
        {
            Presenter.Present(node);
        }
    }
    public void CommitProperty(
        PropertyRow row, string value, TreeItemViewModel? owner, IAssetNode? node)
    {
        if (owner is null || node is null || row.Apply is not { } apply)
        {
            return;
        }
        bool applied = PluginGuard.Try($"Setting '{row.Name}'", () => { apply(value); return true; }, false);
        if (applied)
        {
            RecordPropertyEdit(row, owner, row.Value, value);
            Log($"Set {row.Name} to {value}.");
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (applied)
            {
                ApplyChanges(owner, node, row.Changed | AssetChange.Properties);
            }
            else
            {
                Presenter.RebuildProperties(node);
            }
        });
    }
    public void NotifyCapabilities()
    {
        OnPropertyChanged(nameof(HasScene));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(HasAnimation));
        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(TextureStatus));
        OnPropertyChanged(nameof(AudioStatus));
    }
    private void OnConsoleChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConsoleText));
    }
    public void Log(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(line));
            return;
        }
        _console.Append(line);
        Status = line;
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.Logged -= Log;
        _console.PropertyChanged -= OnConsoleChanged;
        Animation.Dispose();
        Video.Dispose();
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.Dispose();
        _session.Dispose();
        CancelSearch();
        TexturePreview = null;
        GC.SuppressFinalize(this);
    }
}
