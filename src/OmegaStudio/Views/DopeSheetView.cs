using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Omega.Plugins;
namespace OmegaStudio.Views;
public class DopeSheetView : Control
{
    private const double TrackHeight = 20;
    private const double HeaderWidth = 150;
    private const double LabelPadding = 6;
    private const double LabelSize = 11;
    private static readonly Typeface s_typeface = new("Consolas,Cascadia Mono,monospace");
    private IBrush _labelBrush = Brushes.Gray;
    private IPen _keyframePen = new Pen(Brushes.Gray, 2);
    private IPen _playheadPen = new Pen(Brushes.Gray, 1.5);
    private IPen _gridPen = new Pen(Brushes.Gray, 1);
    public static readonly StyledProperty<AnimationClip?> SelectedAnimationProperty =
        AvaloniaProperty.Register<DopeSheetView, AnimationClip?>(nameof(SelectedAnimation));
    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(CurrentTime));
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(Duration));
    private readonly Playhead _playhead;
    private FormattedText[] _labels = [];
    private Rect _visible = new(0, 0, double.PositiveInfinity, double.PositiveInfinity);
    static DopeSheetView()
    {
        AffectsRender<DopeSheetView>(SelectedAnimationProperty, DurationProperty);
        AffectsMeasure<DopeSheetView>(SelectedAnimationProperty);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedAnimationProperty)
        {
            RebuildLabels();
        }
        if (change.Property == CurrentTimeProperty || change.Property == DurationProperty
            || change.Property == SelectedAnimationProperty || change.Property == BoundsProperty)
        {
            _playhead?.InvalidateVisual();
        }
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        _playhead.Arrange(new Rect(finalSize));
        return finalSize;
    }
    private void RebuildLabels()
    {
        _labels = SelectedAnimation is { } clip ? [.. clip.Tracks.Select(track => Text(track.Name))] : [];
    }
    public AnimationClip? SelectedAnimation
    {
        get => GetValue(SelectedAnimationProperty);
        set => SetValue(SelectedAnimationProperty, value);
    }
    public double CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }
    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebuildBrushes();
    }
    public DopeSheetView()
    {
        _playhead = new Playhead(this);
        VisualChildren.Add(_playhead);
        ActualThemeVariantChanged += (_, _) =>
        {
            RebuildBrushes();
            RebuildLabels();
            InvalidateVisual();
            _playhead.InvalidateVisual();
        };
        EffectiveViewportChanged += (_, e) =>
        {
            _visible = e.EffectiveViewport;
            InvalidateVisual();
        };
    }
    private void RebuildBrushes()
    {
        _labelBrush = Resolve("TextMuted");
        _keyframePen = new Pen(Resolve("KeyframeBrush"), 2);
        _playheadPen = new Pen(Resolve("PlayheadBrush"), 1.5);
        _gridPen = new Pen(Resolve("Divider"), 1);
        IBrush Resolve(string key)
        {
            return this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
                ? brush
                : Brushes.Gray;
        }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        int tracks = SelectedAnimation?.Tracks.Count ?? 0;
        double height = Math.Max(TrackHeight * 2, tracks * TrackHeight);
        double width = double.IsInfinity(availableSize.Width) ? HeaderWidth * 3 : availableSize.Width;
        return new Size(width, height);
    }
    public override void Render(DrawingContext context)
    {
        AnimationClip? clip = SelectedAnimation;
        if (clip is null || clip.Tracks.Count == 0)
        {
            DrawPlaceholder(context);
            return;
        }
        double scale = Scale();
        float fps = Fps(clip);
        if (_labels.Length != clip.Tracks.Count)
        {
            RebuildLabels();
        }
        int first = Math.Max(0, (int)Math.Floor(_visible.Top / TrackHeight));
        int last = double.IsInfinity(_visible.Bottom)
            ? clip.Tracks.Count - 1
            : Math.Min(clip.Tracks.Count - 1, (int)Math.Ceiling(_visible.Bottom / TrackHeight));
        for (int i = first; i <= last; i++)
        {
            AnimationTrack track = clip.Tracks[i];
            double y = i * TrackHeight;
            FormattedText label = _labels[i];
            context.DrawText(label, new Point(LabelPadding, y + ((TrackHeight - label.Height) / 2)));
            context.DrawLine(
                _gridPen,
                new Point(HeaderWidth, y + TrackHeight),
                new Point(Bounds.Width, y + TrackHeight));
            foreach (float frame in track.Times)
            {
                double x = HeaderWidth + (frame / fps * scale);
                context.DrawLine(_keyframePen, new Point(x, y + 3), new Point(x, y + TrackHeight - 3));
            }
        }
    }
    private double Scale()
    {
        return Math.Max(1, Bounds.Width - HeaderWidth) / (Duration > 0 ? Duration : 1);
    }
    private sealed class Playhead(DopeSheetView owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner.Duration > 0 && owner.SelectedAnimation is { Tracks.Count: > 0 })
            {
                double x = HeaderWidth + (owner.CurrentTime * owner.Scale());
                context.DrawLine(owner._playheadPen, new Point(x, 0), new Point(x, Bounds.Height));
            }
        }
    }
    private static float Fps(AnimationClip clip)
    {
        return clip.Fps > 0f ? clip.Fps : 60f;
    }
    private void DrawPlaceholder(DrawingContext context)
    {
        FormattedText text = Text("No animation selected");
        context.DrawText(text, new Point(
            (Bounds.Width - text.Width) / 2,
            (Bounds.Height - text.Height) / 2));
    }
    private FormattedText Text(string value)
    {
        return new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            LabelSize,
            _labelBrush);
    }
}
