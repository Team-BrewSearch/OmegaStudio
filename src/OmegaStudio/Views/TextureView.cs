using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace OmegaStudio.Views;

/// <summary>
/// The Texture pane's canvas: an image you can zoom and pan.
///
/// <para>
/// A control rather than a <c>ScrollViewer</c> around an <c>Image</c>, because the
/// useful behaviour here is not scrolling. Game textures are small and often pixel
/// art, so what you want is to magnify a 32x32 sprite until you can see its texels —
/// which means zooming about the pointer, and nearest-neighbour sampling once past
/// 1:1, since bilinear turns the very pixels you are inspecting into mush.
/// </para>
///
/// <para>
/// It belongs to the host and knows nothing about any format, so every plugin's
/// textures gained this at once.
/// </para>
/// </summary>
public sealed class TextureView : Control
{
    /// <summary>Zoom limits. Below the first a texture is a dot; above it, one texel fills the pane.</summary>
    private const double MinZoom = 0.05;
    private const double MaxZoom = 64.0;

    /// <summary>Multiplier per wheel notch. A shallow step so a wheel is a scrub, not a jump.</summary>
    private const double WheelStep = 1.15;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<TextureView, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TextureView, double>(nameof(Zoom), 1.0);

    /// <summary>Where the image's top-left sits in control space.</summary>
    private Point _origin;

    /// <summary>
    /// A new image arrived before the control had been laid out, so its opening view
    /// still has to be chosen. Without this the first texture of a session keeps
    /// whatever zoom the control was constructed with: sizing needs
    /// <see cref="Visual.Bounds"/>, and the binding can set the source first.
    /// </summary>
    private bool _pendingFirstView;

    private bool _dragging;
    private Point _lastPointer;

    static TextureView()
    {
        AffectsRender<TextureView>(SourceProperty, ZoomProperty);

        // A new image opens the way ShowFirstView describes — 1:1, or shrunk if it will not
        // fit — rather than wherever the last one was left. This called Fit instead, which made
        // both ShowFirstView and the _pendingFirstView flag unreachable and quietly reversed
        // the decision documented on them.
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

    /// <summary>Current magnification, 1.0 being one screen pixel per texel.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        private set => SetValue(ZoomProperty, value);
    }

    /// <summary>
    /// Scales the image so the whole of it fills the pane, magnifying if it is small.
    ///
    /// <para>
    /// Magnifying matters more here than it sounds. Game textures are mostly smaller
    /// than the pane — a 64x64 DS texture, a 128x128 face — so a Fit that refused to
    /// enlarge produced exactly 100% for almost every texture in both games, which is
    /// what 1:1 already does. The two buttons did the same thing for everything except
    /// the occasional oversized PNG.
    /// </para>
    /// </summary>
    public void Fit()
    {
        _ = Rescale(magnify: true);
    }

    /// <summary>
    /// The view a newly selected texture opens in: shrunk to fit if it is too big,
    /// otherwise 1:1.
    ///
    /// <para>
    /// Deliberately not <see cref="Fit"/>. Auto-magnifying on selection gives every
    /// texture a different zoom, so clicking down a list of them jumps about; starting
    /// at 1:1 keeps them comparable, and Fit is one click away when you want it.
    /// </para>
    /// </summary>
    private void ShowFirstView()
    {
        _pendingFirstView = !Rescale(magnify: false);
    }

    /// <summary>
    /// Sets the zoom from the image and pane sizes. Returns false when the control has
    /// not been laid out yet and there is nothing to measure against.
    /// </summary>
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

    /// <summary>Snaps to one screen pixel per texel and re-centres.</summary>
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

        // Keep whatever texel is under the pointer under the pointer. Without this the
        // image drifts away as you zoom and you spend the time panning it back.
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

        // Nearest-neighbour once magnified: at 8x the point is to see the texels, and
        // interpolating them is exactly what hides what you are looking for.
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
