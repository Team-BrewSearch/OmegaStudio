namespace Omega.Plugins;
public readonly record struct TileMapPoint(int X, int Y);
public sealed record TileMapCell(int X, int Y, IReadOnlyList<TileMapMark> Layers);
public readonly record struct TileMapMark(uint Rgba, string Text);
public sealed record TileMapMarker(int X, int Y, uint Rgba, string Label, string Detail);
public enum TileMapToolKind
{
    Paint,
    Fill,
    Move,
    Inspect,
}
public sealed record TileMapTool(string Id, string Name, string Group, TileMapToolKind Kind)
{
    public IReadOnlyList<string> Values { get; init; } = [];
    public string Value { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;
}
public sealed class TileMap
{
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required int CellPixels { get; init; }
    public ImageData? Image { get; init; }
    public IReadOnlyList<string> LayerNames { get; init; } = [];
    public IReadOnlyList<TileMapCell> Cells { get; init; } = [];
    public IReadOnlyList<TileMapMarker> Markers { get; init; } = [];
}
public sealed record TileMapStroke(string ToolId, string Value, IReadOnlyList<TileMapPoint> Points);
public interface ITileMapAsset
{
    TileMap TileMap { get; }
    IReadOnlyList<TileMapTool> Tools { get; }
    string Apply(TileMapStroke stroke);
    IReadOnlyList<PropertyRow> Inspect(TileMapPoint point);
}
