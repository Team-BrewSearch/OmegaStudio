using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
namespace OmegaStudio.Views;

public sealed class TextureView : Control
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 64.0;
    private const double WheelStep = 1.15;
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<TextureView, Bitmap?>(nameof(Source));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TextureView, double>(nameof(Zoom), 1.0);
    private Point _origin;
    private bool _pendingFirstView;
    private bool _dragging;
    private Point _lastPointer;
    static TextureView()
    {
        AffectsRender<TextureView>(SourceProperty, ZoomProperty);
        _ = SourceProperty.Changed.AddClassHandler<TextureView>((view, _) => view.ShowFirstView());
    }
    public TextureView()
    {
        ClipToBounds = true;
        Focusable = true;
    }
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        private set => SetValue(ZoomProperty, value);
    }
    public void Fit()
    {
        _ = Rescale(magnify: true);
    }
    private void ShowFirstView()
    {
        _pendingFirstView = !Rescale(magnify: false);
    }
    private bool Rescale(bool magnify)
    {
        if (Source is not { } image || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return false;
        }
        Size size = image.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return false;
        }
        double scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
        Zoom = Math.Clamp(magnify ? scale : Math.Min(scale, 1.0), MinZoom, MaxZoom);
        Centre();
        return true;
    }
    public void ActualSize()
    {
        Zoom = 1.0;
        Centre();
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_pendingFirstView)
        {
            ShowFirstView();
            return;
        }
        Centre();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Source is null)
        {
            return;
        }
        Point pointer = e.GetPosition(this);
        double target = Math.Clamp(Zoom * Math.Pow(WheelStep, e.Delta.Y), MinZoom, MaxZoom);
        double factor = target / Zoom;
        _origin = new Point(
            pointer.X - ((pointer.X - _origin.X) * factor),
            pointer.Y - ((pointer.Y - _origin.Y) * factor));
        Zoom = target;
        e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }
        Point position = e.GetPosition(this);
        _origin += position - _lastPointer;
        _lastPointer = position;
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { } image)
        {
            return;
        }
        Rect destination = new(_origin, image.Size * Zoom);
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = Zoom >= 1.0
                ? BitmapInterpolationMode.None
                : BitmapInterpolationMode.HighQuality,
        }))
        {
            context.DrawImage(image, new Rect(image.Size), destination);
        }
    }
    private void Centre()
    {
        if (Source is not { } image)
        {
            return;
        }
        Size scaled = image.Size * Zoom;
        _origin = new Point(
            (Bounds.Width - scaled.Width) / 2,
            (Bounds.Height - scaled.Height) / 2);
        InvalidateVisual();
    }
}
