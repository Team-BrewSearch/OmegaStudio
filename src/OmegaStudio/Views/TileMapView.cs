using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Omega.Plugins;
namespace OmegaStudio.Views;
public sealed class TileMapView : Control
{
    private const double MinZoom = 4;
    private const double MaxZoom = 160;
    private const double WheelStep = 1.2;
    public static readonly StyledProperty<TileMap?> MapProperty =
        AvaloniaProperty.Register<TileMapView, TileMap?>(nameof(Map));
    public static readonly StyledProperty<Bitmap?> ImageProperty =
        AvaloniaProperty.Register<TileMapView, Bitmap?>(nameof(Image));
    public static readonly StyledProperty<int> LayerProperty =
        AvaloniaProperty.Register<TileMapView, int>(nameof(Layer), -1);
    public static readonly StyledProperty<TileMapTool?> ToolProperty =
        AvaloniaProperty.Register<TileMapView, TileMapTool?>(nameof(Tool));
    public static readonly StyledProperty<TileMapPoint?> SelectedProperty =
        AvaloniaProperty.Register<TileMapView, TileMapPoint?>(nameof(Selected));
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<TileMapView, bool>(nameof(ShowGrid), true);
    private static readonly Typeface s_font = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private readonly List<TileMapPoint> _stroke = [];
    private (int Left, int Top, int Columns, int Rows)? _shape;
    private double _zoom = 24;
    private Point _origin;
    private bool _panning;
    private bool _painting;
    private Point _last;
    private TileMapPoint? _hover;
    static TileMapView()
    {
        AffectsRender<TileMapView>(MapProperty, ImageProperty, LayerProperty, SelectedProperty, ShowGridProperty, ToolProperty);
        _ = MapProperty.Changed.AddClassHandler<TileMapView>((view, _) => view.OnMapChanged());
    }
    public TileMapView()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
    }
    public event Action<TileMapStroke>? StrokeFinished;
    public event Action<TileMapPoint>? CellPicked;
    public event Action<string>? HoverChanged;
    public TileMap? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }
    public Bitmap? Image
    {
        get => GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }
    public int Layer
    {
        get => GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }
    public TileMapTool? Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }
    public TileMapPoint? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }
    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }
    public double Zoom => _zoom;
    public void Fit()
    {
        if (Map is not { } map || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        _zoom = Math.Clamp(Math.Min(Bounds.Width / map.Columns, Bounds.Height / map.Rows), MinZoom, MaxZoom);
        _origin = new Point((Bounds.Width - (map.Columns * _zoom)) / 2, (Bounds.Height - (map.Rows * _zoom)) / 2);
        InvalidateVisual();
    }
    private void OnMapChanged()
    {
        if (Map is not { } map)
        {
            _shape = null;
            return;
        }
        (int, int, int, int) shape = (map.Left, map.Top, map.Columns, map.Rows);
        if (_shape is { } previous && previous == shape)
        {
            return;
        }
        if (_shape is { } old)
        {
            _origin += new Point((map.Left - old.Left) * _zoom, (map.Top - old.Top) * _zoom);
            _shape = shape;
            return;
        }
        _shape = shape;
        Dispatcher.UIThread.Post(Fit, Avalonia.Threading.DispatcherPriority.Background);
    }
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0)
        {
            Fit();
        }
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (Map is not { } map)
        {
            return;
        }
        Rect area = new(_origin.X, _origin.Y, map.Columns * _zoom, map.Rows * _zoom);
        if (Image is { } image)
        {
            context.DrawImage(image, new Rect(image.Size), area);
        }
        if (Layer >= 0 && Layer < map.LayerNames.Count)
        {
            bool text = _zoom >= 22;
            foreach (TileMapCell cell in map.Cells)
            {
                if (Layer >= cell.Layers.Count)
                {
                    continue;
                }
                TileMapMark mark = cell.Layers[Layer];
                Rect rect = CellRect(cell.X, cell.Y);
                if ((mark.Rgba & 0xFF) != 0)
                {
                    context.FillRectangle(new SolidColorBrush(Colour(mark.Rgba)), rect);
                }
                if (text && mark.Text.Length > 0)
                {
                    Label(context, mark.Text, rect.Center, Brushes.White, _zoom * 0.42);
                }
            }
        }
        if (ShowGrid && _zoom >= 8)
        {
            Pen grid = new(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1);
            for (int c = 0; c <= map.Columns; c++)
            {
                double x = Math.Round(_origin.X + (c * _zoom)) + 0.5;
                context.DrawLine(grid, new Point(x, area.Top), new Point(x, area.Bottom));
            }
            for (int r = 0; r <= map.Rows; r++)
            {
                double y = Math.Round(_origin.Y + (r * _zoom)) + 0.5;
                context.DrawLine(grid, new Point(area.Left, y), new Point(area.Right, y));
            }
        }
        Pen outline = new(Brushes.Black, Math.Max(1, _zoom * 0.06));
        foreach (TileMapMarker marker in map.Markers)
        {
            Rect rect = CellRect(marker.X, marker.Y);
            context.DrawEllipse(new SolidColorBrush(Colour(marker.Rgba)), outline, rect.Center, _zoom * 0.34, _zoom * 0.34);
            Label(context, marker.Label, rect.Center, Brushes.White, _zoom * 0.36);
        }
        if (_stroke.Count > 0)
        {
            IBrush preview = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
            foreach (TileMapPoint point in _stroke)
            {
                context.FillRectangle(preview, CellRect(point.X, point.Y));
            }
            if (Tool?.Kind == TileMapToolKind.Move && _hover is { } target)
            {
                context.DrawLine(new Pen(Brushes.White, 2, DashStyle.Dash), CellRect(_stroke[0].X, _stroke[0].Y).Center, CellRect(target.X, target.Y).Center);
            }
        }
        if (Selected is { } selected)
        {
            context.DrawRectangle(new Pen(Brushes.Gold, 3), CellRect(selected.X, selected.Y).Deflate(1.5));
        }
        if (_hover is { } hover)
        {
            context.DrawRectangle(new Pen(Brushes.White, 1.5), CellRect(hover.X, hover.Y).Deflate(0.75));
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        _last = point.Position;
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _panning = true;
            e.Pointer.Capture(this);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || Map is null || Cell(point.Position) is not { } cell)
        {
            return;
        }
        _painting = true;
        _stroke.Clear();
        _stroke.Add(cell);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_panning)
        {
            _origin += position - _last;
            _last = position;
            InvalidateVisual();
            return;
        }
        TileMapPoint? cell = Cell(position);
        if (cell != _hover)
        {
            _hover = cell;
            HoverChanged?.Invoke(Describe(cell));
            InvalidateVisual();
        }
        if (_painting && cell is { } c && Tool?.Kind == TileMapToolKind.Paint && !_stroke.Contains(c))
        {
            foreach (TileMapPoint step in Line(_stroke[^1], c))
            {
                if (!_stroke.Contains(step))
                {
                    _stroke.Add(step);
                }
            }
            InvalidateVisual();
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_panning)
        {
            _panning = false;
            return;
        }
        if (!_painting)
        {
            return;
        }
        _painting = false;
        List<TileMapPoint> points = [.. _stroke];
        _stroke.Clear();
        InvalidateVisual();
        if (Tool is not { } tool || points.Count == 0)
        {
            return;
        }
        TileMapPoint first = points[0];
        Selected = first;
        CellPicked?.Invoke(first);
        switch (tool.Kind)
        {
            case TileMapToolKind.Inspect:
                break;
            case TileMapToolKind.Move:
                if (Cell(e.GetPosition(this)) is { } end && end != first)
                {
                    StrokeFinished?.Invoke(new TileMapStroke(tool.Id, string.Empty, [first, end]));
                    Selected = end;
                }
                break;
            case TileMapToolKind.Fill:
                StrokeFinished?.Invoke(new TileMapStroke(tool.Id, string.Empty, [first]));
                break;
            default:
                StrokeFinished?.Invoke(new TileMapStroke(tool.Id, string.Empty, points));
                break;
        }
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        HoverChanged?.Invoke(string.Empty);
        InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point at = e.GetPosition(this);
        double zoom = Math.Clamp(_zoom * Math.Pow(WheelStep, e.Delta.Y), MinZoom, MaxZoom);
        _origin = at - ((at - _origin) * (zoom / _zoom));
        _zoom = zoom;
        e.Handled = true;
        InvalidateVisual();
    }
    private string Describe(TileMapPoint? point)
    {
        if (point is not { } p || Map is not { } map)
        {
            return string.Empty;
        }
        string text = $"Cell {p.X}, {-p.Y}";
        TileMapCell? cell = map.Cells.FirstOrDefault(c => c.X == p.X && c.Y == p.Y);
        if (cell is not null)
        {
            text += string.Concat(cell.Layers.Select((mark, i) => i < map.LayerNames.Count && mark.Text.Length > 0 ? $" · {map.LayerNames[i]} {mark.Text}" : string.Empty));
        }
        else
        {
            text += " · empty";
        }
        foreach (TileMapMarker marker in map.Markers.Where(m => m.X == p.X && m.Y == p.Y))
        {
            text += $" · {marker.Detail}";
        }
        return text;
    }
    private TileMapPoint? Cell(Point position)
    {
        if (Map is not { } map)
        {
            return null;
        }
        int column = (int)Math.Floor((position.X - _origin.X) / _zoom);
        int row = (int)Math.Floor((position.Y - _origin.Y) / _zoom);
        return column < 0 || row < 0 || column >= map.Columns || row >= map.Rows
            ? null
            : new TileMapPoint(column + map.Left, row + map.Top);
    }
    private Rect CellRect(int x, int y)
    {
        TileMap map = Map!;
        return new Rect(_origin.X + ((x - map.Left) * _zoom), _origin.Y + ((y - map.Top) * _zoom), _zoom, _zoom);
    }
    private static IEnumerable<TileMapPoint> Line(TileMapPoint from, TileMapPoint to)
    {
        int steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        for (int i = 1; i <= steps; i++)
        {
            yield return new TileMapPoint(
                from.X + (int)Math.Round((to.X - from.X) * (double)i / steps),
                from.Y + (int)Math.Round((to.Y - from.Y) * (double)i / steps));
        }
    }
    private static void Label(DrawingContext context, string text, Point centre, IBrush brush, double size)
    {
        FormattedText shadow = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, size, Brushes.Black);
        FormattedText label = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, s_font, size, brush);
        Point at = new(centre.X - (label.Width / 2), centre.Y - (label.Height / 2));
        context.DrawText(shadow, at + new Point(1, 1));
        context.DrawText(label, at);
    }
    private static Color Colour(uint rgba)
    {
        return Color.FromArgb((byte)rgba, (byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8));
    }
}
