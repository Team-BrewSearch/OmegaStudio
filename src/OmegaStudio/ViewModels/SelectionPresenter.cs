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
    GlViewport? Viewport { get; }
    SceneGeometry? ViewportScene { get; set; }
    Bitmap? TexturePreview { get; set; }
    int SelectedPane { get; set; }
    ObservableCollection<PropertyItem> Properties { get; }
    ObservableCollection<AssetCommandViewModel> Commands { get; }
    Action<string> Log { get; }
    bool HasScene { get; }
    bool HasImage { get; }
    bool HasAudio { get; }
    string TextureStatus { get; }
    string AudioStatus { get; }
    AudioInfo? AudioInfo { get; set; }
    void ResetAudioState();
    Task LoadAudioAsync(IAudioAsset audio);
    void CommitProperty(PropertyRow row, string value, TreeItemViewModel? owner, IAssetNode? node);
    void RebuildProperties(IAssetNode? node);
    void NotifyCapabilities();
}
internal interface ICommandRunner
{
    System.Threading.Tasks.Task RunCommandAsync(AssetCommand command);
}
internal sealed class SelectionPresenter
{
    private readonly ISelectionView _view;
    private readonly ICommandRunner _runner;
    public SelectionPresenter(ISelectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        _runner = view as ICommandRunner ?? throw new ArgumentException(
            "the view must also run commands", nameof(view));
    }
    public void Present(IAssetNode? node)
    {
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
        _view.NotifyCapabilities();
        ChooseVisiblePane(node);
    }
    private void ShowScene(IAssetNode? node)
    {
        if (node is not ISceneAsset source)
        {
            _view.Animation.Unbind();
            _view.ViewportScene = null;
            return;
        }
        _view.Viewport?.ClearAnimatedFrame();
        SceneGeometry? scene = PluginGuard.Try(
            $"Building the scene for '{NodeAccess.Name(node)}'", source.GetScene, null);
        if (scene is not null)
        {
            if (!PluginGuard.Try(
                $"Validating the scene for '{NodeAccess.Name(node)}'", () => { scene.Validate(); return true; }, false))
            {
                scene = null;
            }
        }
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
    private void ShowImage(IAssetNode? node)
    {
        _view.TexturePreview = node is not IImageAsset image
            ? null
            : PluginGuard.Try($"Rendering '{NodeAccess.Name(node)}'", () => ImageIO.ToBitmap(image.Image), null);
    }
    private void ShowAudio(IAssetNode? node)
    {
        if (node is not IAudioAsset audio)
        {
            _view.ResetAudioState();
            _view.AudioInfo = null;
            return;
        }
        _view.AudioInfo = PluginGuard.Try(
            $"Reading the audio of '{NodeAccess.Name(node)}'",
            () => AudioInfoHelper.TryGetInfo(NodeAccess.EncodedAudio(audio), out AudioInfo info)
                ? info
                : (AudioInfo?)null,
            null);
        _view.ResetAudioState();
        _ = _view.LoadAudioAsync(audio);
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
            _view.SelectedPane = preferred;
            return;
        }
        bool[] available = [_view.HasScene, _view.HasImage, _view.HasAudio];
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
    private void RebuildProperties(IAssetNode? node)
    {
        _view.Properties.Clear();
        TreeItemViewModel? owner = _view.Owner;
        foreach (PropertyRow row in PropertyInspector.Describe(node, _view.AudioInfo))
        {
            _view.Properties.Add(PropertyItem.For(
                row, (edited, value) => _view.CommitProperty(edited, value, owner, node)));
        }
    }
    private void RebuildCommands(IAssetNode? node)
    {
        _view.Commands.Clear();
        if (node is null)
        {
            return;
        }
        foreach (AssetCommand command in NodeCommands.For(node, _view.Animation.ExportClip, _view.Animation.ExportFps))
        {
            _view.Commands.Add(new AssetCommandViewModel(command, _runner));
        }
    }
}
