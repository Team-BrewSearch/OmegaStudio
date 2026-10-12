using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using OmegaStudio.Rendering;
using OmegaStudio.ViewModels;
namespace OmegaStudio.Views;
public partial class MainWindow : Window
{
    private MainViewModel? _currentVm;
    private PropertyChangedEventHandler? _consolePropertyChangedHandler;
    private Action? _resetViewHandler;
    private readonly GlViewport? _vp;
    private readonly ScrollViewer? _consoleScroll;
    private readonly TextureView? _texture;
    private readonly TreeView? _outliner;
    public MainWindow()
    {
        InitializeComponent();
        _vp = this.FindControl<GlViewport>("Viewport");
        _consoleScroll = this.FindControl<ScrollViewer>("ConsoleScroll");
        _texture = this.FindControl<TextureView>("TexturePreviewView");
        _outliner = this.FindControl<TreeView>("OutlinerTree");
        Slider? seek = this.FindControl<Slider>("AudioSeek");
        if (seek is not null)
        {
            seek.AddHandler(Thumb.DragStartedEvent, AudioSeek_OnThumbHeld, handledEventsToo: true);
            seek.AddHandler(Thumb.DragCompletedEvent, AudioSeek_OnThumbReleased, handledEventsToo: true);
            seek.AddHandler(InputElement.PointerCaptureLostEvent, AudioSeek_OnThumbReleased, handledEventsToo: true);
        }
        if (_vp is not null && DataContext is MainViewModel vm)
        {
            vm.Viewport = _vp;
        }
        DataContextChanged += OnDataContextChanged;
    }
    public async Task CloseAsync(MainViewModel vm)
    {
        if (await vm.CanDiscardAsync())
        {
            Close();
        }
    }
    public async Task<bool> AskAboutUnsavedChangesAsync(MainViewModel vm)
    {
        UnsavedChangesWindow prompt = new();
        string what = string.IsNullOrEmpty(vm.OpenArchiveName) ? "The archive" : vm.OpenArchiveName;
        UnsavedChangesChoice choice = await prompt.AskAsync(this, what);
        switch (choice)
        {
            case UnsavedChangesChoice.Cancel:
                return false;
            case UnsavedChangesChoice.Discard:
                return true;
            default:
                await vm.SaveAsync();
                return !vm.HasUnsavedChanges;
        }
    }
    private void PropertyBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: TextPropertyItem row } box)
        {
            row.Value = box.Text ?? string.Empty;
            e.Handled = true;
        }
    }
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    private void UnbindViewModel()
    {
        if (_currentVm is null)
        {
            return;
        }
        if (_resetViewHandler is not null)
        {
            _currentVm.ResetViewRequested -= _resetViewHandler;
        }
        if (_consolePropertyChangedHandler is not null)
        {
            _currentVm.PropertyChanged -= _consolePropertyChangedHandler;
        }
        _resetViewHandler = null;
        _consolePropertyChangedHandler = null;
        _currentVm = null;
    }
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        _currentVm = vm;
        if (_vp is not null)
        {
            vm.Viewport = _vp;
        }
        _resetViewHandler = () => _vp?.ResetCamera();
        vm.ResetViewRequested += _resetViewHandler;
        _consolePropertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ConsoleText))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _consoleScroll?.ScrollToEnd());
            }
        };
        vm.PropertyChanged += _consolePropertyChangedHandler;
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        UnbindViewModel();
        DataContextChanged -= OnDataContextChanged;
    }
    private bool _dragging, _panning;
    private Point _lastPointer;
    private void Tree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || DataContext is not MainViewModel vm)
        {
            return;
        }
        TreeViewItem? row = (e.Source as Visual)
            ?.GetLogicalAncestors()
            .OfType<TreeViewItem>()
            .FirstOrDefault();
        if (row?.DataContext is TreeItemViewModel under)
        {
            vm.SelectedItem = under;
        }
    }
    private void Overlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vp is null)
        {
            return;
        }
        PointerPoint p = e.GetCurrentPoint(_vp);
        _dragging = p.Properties.IsLeftButtonPressed && !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _panning = p.Properties.IsMiddleButtonPressed ||
                   (p.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (_dragging || _panning)
        {
            _lastPointer = p.Position;
            e.Pointer.Capture(sender as IInputElement);
        }
        e.Handled = true;
    }
    private void Overlay_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if ((!_dragging && !_panning) || _vp is null)
        {
            return;
        }
        Point pos = e.GetPosition(_vp);
        double dx = pos.X - _lastPointer.X;
        double dy = pos.Y - _lastPointer.Y;
        _lastPointer = pos;
        if (_dragging)
        {
            _vp.Orbit(dx, dy);
        }
        else
        {
            _vp.Pan(dx, dy);
        }
        e.Handled = true;
    }
    private void Overlay_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
    private void Overlay_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _vp?.Zoom((float)e.Delta.Y);
        e.Handled = true;
    }
    private void About_OnClick(object? sender, RoutedEventArgs e)
    {
        _ = new AboutWindow().ShowDialog(this);
    }
    private void TextureFit_OnClick(object? sender, RoutedEventArgs e)
    {
        _texture?.Fit();
    }
    private void TextureActualSize_OnClick(object? sender, RoutedEventArgs e)
    {
        _texture?.ActualSize();
    }
    private void AudioSeek_OnThumbHeld(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetThumbHeld(true);
    }
    private void AudioSeek_OnThumbReleased(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetThumbHeld(false);
    }
}
