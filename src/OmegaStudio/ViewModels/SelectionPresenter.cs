using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using OmegaStudio.Audio;
using OmegaStudio.Plugins;
using OmegaStudio.Rendering;
using OmegaStudio.Services;
namespace OmegaStudio.ViewModels;
public interface ISelectionView
{
    TreeItemViewModel? Owner { get; }
    AnimationPlayer Animation { get; }
    VideoPlayer Video { get; }
    SceneGeometry? ViewportScene { get; set; }
    IAssetNode? SceneSource { get; set; }
    Bitmap? TexturePreview { get; set; }
    int SelectedPane { get; set; }
    ObservableCollection<PropertyItem> Properties { get; }
    ObservableCollection<AssetCommandViewModel> Commands { get; }
    Action<string> Log { get; }
    Task<T> ReadArchiveAsync<T>(Func<T> work);
    bool HasScene { get; }
    bool HasImage { get; }
    bool HasAudio { get; }
    bool HasVideo { get; }
    bool HasMap { get; }
    void ShowTileMap(ITileMapAsset? source, TileMap? map, Bitmap? image);
    string TextureStatus { get; set; }
    string AudioStatus { get; }
    AudioInfo? AudioInfo { get; set; }
    void ResetAudioState();
    Task<bool> LoadAudioAsync(IAudioAsset audio, byte[]? data);
    void CommitProperty(PropertyRow row, string value, TreeItemViewModel? owner, IAssetNode? node);
    void NotifyCapabilities();
}
internal interface ICommandRunner
{
    System.Threading.Tasks.Task RunCommandAsync(AssetCommand command);
}
internal sealed class SelectionPresenter
{
    internal const string NoTexture = "No texture selected";
    private static readonly Selection s_nothing = new(null, null, NoTexture, null, null, (".wav", null), [], []);
    private readonly ISelectionView _view;
    private readonly ICommandRunner _runner;
    private readonly Func<int> _frame;
    private IAssetNode? _node;
    private (string Clip, string? Soundtrack) _sound = s_nothing.Sound;
    private int _generation;
    public SelectionPresenter(ISelectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _runner = view as ICommandRunner ?? throw new ArgumentException(
            "the view must also run commands", nameof(view));
        _frame = () => _view.Video.CurrentFrame;
    }
    private bool _presenting;
    public bool IsPresenting => _presenting;
    public void Present(IAssetNode? node)
    {
        int generation = ++_generation;
        _node = node;
        _presenting = true;
        _view.Commands.Clear();
        if (node is null)
        {
            _view.ShowTileMap(null, null, null);
            Show(null, s_nothing, null);
            return;
        }
        VideoInfo? video = node is IVideoAsset clip
            ? PluginGuard.Try("Reading the video header", () => clip.Video, null)
            : null;
        _ = ShowAsync(node, generation, video);
    }
    private async Task ShowAsync(IAssetNode node, int generation, VideoInfo? video)
    {
        Selection? selection;
        Bitmap? bitmap = null;
        TileMap? tileMap = null;
        Bitmap? mapImage = null;
        try
        {
            selection = await _view.ReadArchiveAsync(
                () => Volatile.Read(ref _generation) == generation ? Read(node, _frame, video) : null);
            if (selection?.Image is { } image && generation == _generation)
            {
                bitmap = await Task.Run(
                    () => PluginGuard.Try<Bitmap?>($"Rendering '{image.Name}'", () => ImageIO.ToBitmap(image), null));
            }
            if (node is ITileMapAsset source && generation == _generation)
            {
                tileMap = await _view.ReadArchiveAsync(
                    () => PluginGuard.Try<TileMap?>($"Drawing the map of '{NodeAccess.Name(node)}'", () => source.TileMap, null));
                if (tileMap?.Image is { } picture && generation == _generation)
                {
                    mapImage = await Task.Run(
                        () => PluginGuard.Try<Bitmap?>($"Rendering the map of '{NodeAccess.Name(node)}'", () => ImageIO.ToBitmap(picture), null));
                }
            }
        }
        catch (ArchiveClosedException)
        {
            _presenting = false;
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PluginGuard.Report($"Showing '{NodeAccess.Name(node)}'", ex);
            selection = s_nothing;
        }
        if (selection is null || generation != _generation)
        {
            bitmap?.Dispose();
            mapImage?.Dispose();
            return;
        }
        _view.ShowTileMap(node as ITileMapAsset, tileMap, mapImage);
        Show(node, selection, bitmap);
    }
    private static Selection Read(IAssetNode node, Func<int> frame, VideoInfo? videoInfo)
    {
        string name = NodeAccess.Name(node);
        SceneGeometry? scene = node is ISceneAsset source ? BuildScene(name, source) : null;
        ImageData? image = node is IImageAsset texture
            ? PluginGuard.Try<ImageData?>($"Rendering '{name}'", () => texture.Image, null)
            : null;
        string status = node is not IImageAsset ? NoTexture
            : image is null ? "Texture could not be read"
            : $"{image.Name}   {image.Width} x {image.Height}   {image.FormatLabel}";
        byte[]? audio = node is IAudioAsset clip and not IStreamingAudioAsset ? NodeAccess.EncodedAudio(clip).ToArray() : null;
        AudioInfo? info = node is IStreamingAudioAsset streaming
            ? PluginGuard.Try($"Reading the audio of '{name}'", () => StreamInfo(streaming), null)
            : audio is null ? null : PluginGuard.Try(
                $"Reading the audio of '{name}'",
                () => AudioInfoHelper.TryGetInfo(audio, out AudioInfo found) ? found : (AudioInfo?)null,
                null);
        string? soundtrack = node is IVideoAsset video
            ? PluginGuard.Try<string?>(
                $"Reading the soundtrack of '{name}'",
                () => video.Soundtrack is { IsEmpty: false } track ? AudioFormats.ExtensionOf(track) : null,
                null)
            : null;
        (string Clip, string? Soundtrack) sound = (AudioFormats.ExtensionOf(audio), soundtrack);
        return new Selection(
            scene,
            image,
            status,
            audio,
            info,
            sound,
            PropertyInspector.Describe(node, info, videoInfo),
            NodeCommands.For(node, null, 0f, frame, sound.Clip, sound.Soundtrack));
    }
    private static AudioInfo? StreamInfo(IStreamingAudioAsset asset)
    {
        using IAudioReader reader = asset.OpenAudio();
        return new AudioInfo("Streamed", reader.SampleRate, reader.Channels, 16, reader.TotalFrames * reader.Channels);
    }
    private void Show(IAssetNode? node, Selection selection, Bitmap? bitmap)
    {
        try
        {
            if (node is ISceneAsset)
            {
                ShowScene(node, selection.Scene);
            }
            _view.TexturePreview = bitmap;
            _view.TextureStatus = selection.TextureStatus;
            _view.AudioInfo = selection.AudioInfo;
            if (node is IAudioAsset audio && (selection.Audio is not null || (node is IStreamingAudioAsset && selection.AudioInfo is not null)))
            {
                _ = _view.LoadAudioAsync(audio, selection.Audio);
            }
            else
            {
                _view.ResetAudioState();
            }
            ShowVideo(node);
        }
        finally
        {
            _presenting = false;
        }
        _sound = selection.Sound;
        ShowCommands(selection.Commands);
        ShowProperties(node, selection.Properties);
        _view.NotifyCapabilities();
        ChooseVisiblePane(node);
    }
    public void RefreshCommands()
    {
        if (!_presenting && _node is { } node)
        {
            ShowCommands(NodeCommands.For(
                node, _view.Animation.ExportClip, _view.Animation.ExportFps, _frame, _sound.Clip, _sound.Soundtrack));
        }
    }
    public void ShowScene(IAssetNode? node)
    {
        ShowScene(node, node is ISceneAsset source ? BuildScene(NodeAccess.Name(node), source) : null);
    }
    private static SceneGeometry? BuildScene(string name, ISceneAsset source)
    {
        SceneGeometry? scene = PluginGuard.Try($"Building the scene for '{name}'", source.GetScene, null);
        return scene is not null
            && PluginGuard.Try($"Validating the scene for '{name}'", () => { scene.Validate(); return true; }, false)
            ? scene
            : null;
    }
    private void ShowScene(IAssetNode? node, SceneGeometry? scene)
    {
        if (node is not ISceneAsset)
        {
            _view.Animation.Unbind();
            _view.ViewportScene = null;
            return;
        }
        _view.SceneSource = node;
        _view.Animation.ShowRestPose();
        _view.ViewportScene = scene;
        if (scene is not null && node is IAnimatedAsset animated)
        {
            _view.Animation.Bind(animated);
        }
        else
        {
            _view.Animation.Unbind();
        }
    }
    private void ShowVideo(IAssetNode? node)
    {
        if (node is IVideoAsset video)
        {
            _ = _view.Video.BindAsync(video);
        }
        else
        {
            _view.Video.Unbind();
        }
    }
    private void ChooseVisiblePane(IAssetNode? node)
    {
        int preferred = node switch
        {
            ITileMapAsset when _view.HasMap => 0,
            ISceneAsset => 1,
            IImageAsset => 2,
            IVideoAsset => 4,
            IAudioAsset => 3,
            _ => -1,
        };
        if (preferred >= 0)
        {
            _view.SelectedPane = preferred;
            return;
        }
        bool[] available = [_view.HasMap, _view.HasScene, _view.HasImage, _view.HasAudio, _view.HasVideo];
        int selected = _view.SelectedPane;
        if (selected >= 0 && selected < available.Length && available[selected])
        {
            return;
        }
        int fallback = Array.IndexOf(available, true);
        if (fallback >= 0)
        {
            _view.SelectedPane = fallback;
        }
    }
    public void RebuildProperties(IAssetNode? node)
    {
        if (!_presenting || !ReferenceEquals(node, _node))
        {
            VideoInfo? video = node is IVideoAsset clip
                ? PluginGuard.Try("Reading the video header", () => clip.Video, null)
                : null;
            ShowProperties(node, PropertyInspector.Describe(node, _view.AudioInfo, video));
        }
    }
    private void ShowProperties(IAssetNode? node, IReadOnlyList<PropertyRow> rows)
    {
        _view.Properties.Clear();
        TreeItemViewModel? owner = _view.Owner;
        foreach (PropertyRow row in rows)
        {
            _view.Properties.Add(PropertyItem.For(
                row, (edited, value) => _view.CommitProperty(edited, value, owner, node)));
        }
    }
    private void ShowCommands(IReadOnlyList<AssetCommand> commands)
    {
        _view.Commands.Clear();
        foreach (AssetCommand command in commands)
        {
            _view.Commands.Add(new AssetCommandViewModel(command, _runner));
        }
    }
    private sealed record Selection(
        SceneGeometry? Scene,
        ImageData? Image,
        string TextureStatus,
        byte[]? Audio,
        AudioInfo? AudioInfo,
        (string Clip, string? Soundtrack) Sound,
        IReadOnlyList<PropertyRow> Properties,
        IReadOnlyList<AssetCommand> Commands);
}
