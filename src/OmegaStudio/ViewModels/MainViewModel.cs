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

/// <summary>
/// The window's view model: what is selected, what each pane shows, and the commands
/// the menus and context menus invoke.
///
/// <para>
/// It knows no format. Every pane is driven by asking the selected
/// <see cref="IAssetNode"/> which capability interfaces it implements — a node that
/// is an <see cref="IImageAsset"/> gets the Texture tab, one that is not does not
/// have one — so a new plugin lights up the window without a line changing here.
/// </para>
///
/// <para>
/// The work itself lives in the collaborators it owns: <see cref="ArchiveSession"/>
/// for files, <see cref="AnimationPlayer"/> for playback, <see cref="ConsoleLog"/>
/// for the transcript, <see cref="PropertyInspector"/> for the Properties pane,
/// <see cref="NodeCommands"/> for the context menu. Search stays here in
/// <c>MainViewModel.Search.cs</c> because it is genuinely view state.
/// </para>
/// </summary>
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

    /// <summary>The decode in flight for <see cref="_currentAudio"/>, if any.</summary>
    private Task? _audioLoad;

    /// <summary>
    /// Format details for the selected clip, read once on selection. Reading them
    /// walks the whole blob, and the status bar re-evaluates on every poll tick.
    /// </summary>
    private AudioInfo? _audioInfo;

    private bool _disposed;

    /// <param name="storage">File pickers, so the view model needs no visual tree.</param>
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

    /// <summary>Set by the view once the control tree exists.</summary>
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

    /// <summary>The selected node's context menu, rebuilt on every selection.</summary>
    public ObservableCollection<AssetCommandViewModel> Commands { get; } = [];

    public string ConsoleText => _console.Text;

    public Array ShadingModes { get; } = Enum.GetValues<ShadingMode>();

    /// <summary>
    /// The decoded texture the Texture pane draws.
    ///
    /// <para>
    /// The previous bitmap is released only after the change has been announced. Disposing it
    /// first left the compositor — which draws on a thread of its own — free to sample pixels
    /// that had already been freed.
    /// </para>
    /// </summary>
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

    // ---- plugins ----------------------------------------------------------

    /// <summary>Every loaded plugin, as the Formats menu shows it.</summary>
    public ObservableCollection<PluginRow> LoadedFormats { get; } = [];

    /// <summary>One line per plugin that failed, with the reason.</summary>
    public ObservableCollection<string> FailedFormats { get; } = [];

    public bool HasFailedFormats => FailedFormats.Count > 0;

    /// <summary>Where plugins are read from, shown in the Formats menu so it can be found.</summary>
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

    // ---- capability flags -------------------------------------------------

    private IAssetNode? Node => SelectedItem?.Node;

    // What each pane can show. Deliberately asked of what is *on screen* rather than
    // of the selection: clicking a bone under a model must not take the model out of
    // the viewport, so a node with no pane of its own leaves the panes as they were.
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

    // ---- selection --------------------------------------------------------

    partial void OnSelectedItemChanged(TreeItemViewModel? value)
    {
        IAssetNode? node = value?.Node;

        // Each pane updates only for a node it is actually about. A bone, a material
        // or a folder changes the Properties pane and the context menu and nothing
        // else — which is what makes clicking through a model's parts usable.
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
            ViewportScene = null;
            return;
        }

        Viewport?.ClearAnimatedFrame();

        SceneGeometry? scene = PluginGuard.Try($"Building the scene for '{node.Name}'", source.GetScene, null);
        if (scene is not null)
        {
            // Validate before it reaches the GPU: a submesh running off the end of its
            // arrays is a plugin bug that would otherwise surface as a driver crash
            // with nothing pointing at the plugin that caused it.
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
            // Leaving an audio node stops the clip: playback that outlives the
            // selection has nothing on screen explaining where the sound is from.
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

    /// <summary>
    /// Switches to the pane the selection is about, and leaves it alone when the
    /// selection has no pane of its own.
    ///
    /// <para>
    /// The fallback matters because panes hide themselves when empty: leaving the
    /// selection on a tab that has just disappeared shows a blank content area with
    /// no header to explain it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Raises every selection-dependent flag together. They all read
    /// <see cref="SelectedItem"/>, so they always change at the same moment.
    /// </summary>
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

    /// <summary>
    /// Appends a line to the console and the status bar.
    ///
    /// <para>
    /// Marshalled to the UI thread, because <see cref="IHostServices.Log"/> promises a plugin
    /// it may write from any thread and plugins take that up: opening an archive, running a
    /// command and searching a folder all happen on the pool. Both things this touches are
    /// UI-affine — <see cref="ConsoleLog"/> holds a plain StringBuilder, and
    /// <see cref="Status"/> raises straight into live bindings.
    /// </para>
    /// </summary>
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

    // ---- lifetime ---------------------------------------------------------

    /// <summary>
    /// Releases the audio device, the animation timer, the open archive and any
    /// in-flight search. The app shuts these down explicitly rather than leaving an
    /// OpenAL context to a finalizer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Unsubscribed as well as disposed: the console and the session outlive nothing here,
        // but leaving a handler pointing at a disposed view model is how a late log line from
        // a background save reaches a window that is already gone.
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
