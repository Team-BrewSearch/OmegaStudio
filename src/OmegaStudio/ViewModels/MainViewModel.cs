using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
using OmegaStudio.Rendering;
using OmegaStudio.Services;
using System.ComponentModel;
namespace OmegaStudio.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
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
    private bool _disposed;
    public MainViewModel(IAppStorageProvider storage)
    {
        _storage = storage;
        _session.Logged += Log;
        _console.PropertyChanged += OnConsoleChanged;
        _audioPlayer.StateChanged += OnAudioPlayerStateChanged;
        Animation = new AnimationPlayer();
        Animation.FrameReady += (positions, normals) => Viewport?.SetAnimatedFrame(positions, normals);
        Animation.FrameCleared += () => Viewport?.ClearAnimatedFrame();
        PluginGuard.Log = Log;
        LoadPlugins();
    }
    public GlViewport? Viewport { get; set; }
    public AnimationPlayer Animation { get; }
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
    public ObservableCollection<PropertyRow> Properties { get; } = [];
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
    private void LoadPlugins()
    {
        _catalog = PluginLoader.Load(FormatsFolder, Log);
        _recognition = AssetRecognition.From(_catalog, Log);
        _tree = new TreeBuilder(_recognition);
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
    public bool HasAnimation => Animation.IsAvailable;
    public bool HasCommands => Commands.Count > 0;
    public string TextureStatus => Node is not IImageAsset image
        ? "No texture selected"
        : PluginGuard.Try(
            "Reading the selected texture",
            () => $"{image.Image.Name}   {image.Image.Width} x {image.Image.Height}   {image.Image.FormatLabel}",
            "Texture could not be read");
    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        IAssetNode? node = value?.Node;
        if (node is ISceneAsset)
        {
            ShowScene(node);
        }
        if (node is IImageAsset)
        {
            ShowImage(node);
        }
        ShowAudio(node);
        RebuildCommands(node);
        RebuildProperties(node);
        NotifyCapabilities();
        ChooseVisiblePane(node);
    }
    private void ShowScene(IAssetNode? node)
    {
        if (node is not ISceneAsset source)
        {
            Animation.Unbind();
            _sceneSource = null;
            ViewportScene = null;
            return;
        }
        _sceneSource = node;
        Viewport?.ClearAnimatedFrame();
        SceneGeometry? scene = PluginGuard.Try($"Building the scene for '{node.Name}'", source.GetScene, null);
        if (scene is not null)
        {
            if (!PluginGuard.Try($"Validating the scene for '{node.Name}'", () => { scene.Validate(); return true; }, false))
            {
                scene = null;
            }
        }
        ViewportScene = scene;
        if (scene is not null && node is IAnimatedAsset animated)
        {
            Animation.Bind(animated);
        }
        else
        {
            Animation.Unbind();
        }
    }
    private void ShowImage(IAssetNode? node)
    {
        TexturePreview = node is not IImageAsset image
            ? null
            : PluginGuard.Try(
                $"Rendering '{node.Name}'", () => ImageIO.ToBitmap(image.Image), null);
    }
    private void ShowAudio(IAssetNode? node)
    {
        if (node is not IAudioAsset audio)
        {
            ResetAudioState();
            _audioInfo = null;
            return;
        }
        _audioInfo = PluginGuard.Try(
            $"Reading the audio of '{node.Name}'",
            () => AudioInfoHelper.TryGetInfo(audio.EncodedAudio, out AudioInfo info) ? info : (AudioInfo?)null,
            null);
        ResetAudioState();
        _ = LoadAudioAsync(audio);
    }
    private void ChooseVisiblePane(IAssetNode? node)
    {
        int preferred = node switch
        {
            ISceneAsset => 0,
            IImageAsset => 1,
            IAudioAsset => 2,
            _ => -1,
        };
        if (preferred >= 0)
        {
            SelectedPane = preferred;
            return;
        }
        bool[] available = [HasScene, HasImage, HasAudio];
        if (SelectedPane >= 0 && SelectedPane < available.Length && available[SelectedPane])
        {
            return;
        }
        int fallback = Array.IndexOf(available, true);
        if (fallback >= 0)
        {
            SelectedPane = fallback;
        }
    }
    private void RebuildProperties(IAssetNode? node)
    {
        Properties.Clear();
        foreach (PropertyRow row in PropertyInspector.Describe(node, _audioInfo))
        {
            Properties.Add(row);
        }
    }
    private void RebuildCommands(IAssetNode? node)
    {
        Commands.Clear();
        if (node is null)
        {
            return;
        }
        foreach (AssetCommand command in NodeCommands.For(node))
        {
            Commands.Add(new AssetCommandViewModel(command, this));
        }
    }
    private void NotifyCapabilities()
    {
        OnPropertyChanged(nameof(HasScene));
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasAudio));
        OnPropertyChanged(nameof(HasAnimation));
        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(TextureStatus));
        OnPropertyChanged(nameof(AudioStatus));
    }
    private void OnConsoleChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConsoleText));
    }
    private void Log(string line)
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
        _audioPlayer.StateChanged -= OnAudioPlayerStateChanged;
        _audioPlayer.Dispose();
        _session.Dispose();
        CancelSearch();
        TexturePreview = null;
        GC.SuppressFinalize(this);
    }
}
