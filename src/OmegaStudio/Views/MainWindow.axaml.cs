using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OmegaStudio.Rendering;
using OmegaStudio.ViewModels;
using System.ComponentModel;

namespace OmegaStudio.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _currentVm;
    private PropertyChangedEventHandler? _consolePropertyChangedHandler;
    private Action? _resetViewHandler;

    // Cached control references — FindControl traverses the tree on every call,
    // so we resolve once after InitializeComponent.
    private readonly GlViewport? _vp;
    private readonly ScrollViewer? _consoleScroll;
    private readonly TextureView? _texture;

    public MainWindow()
    {
        InitializeComponent();
        _vp = this.FindControl<GlViewport>("Viewport");
        _consoleScroll = this.FindControl<ScrollViewer>("ConsoleScroll");
        _texture = this.FindControl<TextureView>("TexturePreviewView");

        // The transport slider's Thumb marks its own pointer events handled, so a
        // plain XAML PointerPressed/Released handler on the Slider never sees a
        // drag. Thumb's drag events do bubble; handledEventsToo covers the Slider
        // handling them itself, and PointerCaptureLost is the belt and braces for a
        // drag that ends without a release (window deactivated mid-scrub).
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

    /// <summary>
    /// Shows the About box, modal to this window. Fire and forget: the dialog blocks
    /// the menu that opened it, so there is no second one to wait for.
    /// </summary>
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

    /// <summary>
    /// Tells the view model the transport thumb is being held, so the poll timer
    /// leaves the slider alone until the drag is over.
    ///
    /// <para>
    /// The seek itself is not done here — the two-way binding on Value already
    /// carries every change through, whether it came from this thumb, a click on the
    /// track or the arrow keys. All the view contributes is who is in charge of the
    /// number right now.
    /// </para>
    /// </summary>
    private void AudioSeek_OnThumbHeld(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetThumbHeld(true);
    }

    private void AudioSeek_OnThumbReleased(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetThumbHeld(false);
    }
}
