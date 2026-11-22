using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private ITileMapAsset? _mapSource;
    [ObservableProperty]
    public partial TileMap? MapSnapshot { get; set; }
    [ObservableProperty]
    public partial TileMapTool? MapTool { get; set; }
    [ObservableProperty]
    public partial string? MapToolValue { get; set; }
    [ObservableProperty]
    public partial int MapLayerIndex { get; set; } = 1;
    [ObservableProperty]
    public partial TileMapPoint? MapSelection { get; set; }
    [ObservableProperty]
    public partial string MapStatus { get; set; } = string.Empty;
    public Bitmap? MapImage
    {
        get;
        private set
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
    public ObservableCollection<TileMapTool> MapTools { get; } = [];
    public ObservableCollection<string> MapToolValues { get; } = [];
    public ObservableCollection<string> MapLayers { get; } = [];
    public ObservableCollection<PropertyItem> MapInspector { get; } = [];
    public bool HasMap => MapSnapshot is not null;
    public bool MapToolHasValues => MapToolValues.Count > 0;
    public int MapViewLayer => MapLayerIndex - 1;
    public string MapHint => MapTool is { } tool
        ? (tool.Hint.Length > 0 ? tool.Hint : tool.Name) + " · Right-drag to pan · Wheel to zoom"
        : string.Empty;
    public void ShowTileMap(ITileMapAsset? source, TileMap? map, Bitmap? image)
    {
        bool same = source is not null && ReferenceEquals(source, _mapSource);
        _mapSource = source;
        MapImage = image;
        MapSnapshot = map;
        if (!same)
        {
            MapSelection = null;
            MapInspector.Clear();
            MapTools.Clear();
            MapLayers.Clear();
            if (source is not null && map is not null)
            {
                foreach (TileMapTool tool in PluginGuard.Try("Listing the map tools", () => (IReadOnlyList<TileMapTool>)[.. source.Tools], []))
                {
                    MapTools.Add(tool);
                }
                MapLayers.Add("None");
                foreach (string layer in map.LayerNames)
                {
                    MapLayers.Add(layer);
                }
                MapLayerIndex = Math.Min(1, MapLayers.Count - 1);
                MapTool = MapTools.FirstOrDefault();
            }
        }
        else if (MapSelection is { } selected)
        {
            InspectMapCell(selected);
        }
        OnPropertyChanged(nameof(HasMap));
    }
    public async Task PaintMapAsync(TileMapStroke stroke)
    {
        if (_mapSource is not { } map || MapTool is not { } tool || stroke.ToolId != tool.Id)
        {
            return;
        }
        TileMapStroke applied = stroke with { Value = MapToolValue ?? tool.Value };
        string message = string.Empty;
        await RunCommandAsync(new AssetCommand
        {
            Title = $"Map: {tool.Name}",
            Execute = context =>
            {
                message = map.Apply(applied);
                context.Log(message);
                context.Changed = AssetChange.Bytes | AssetChange.Scene | AssetChange.Properties | AssetChange.Dirty;
            },
        });
        if (message.Length > 0)
        {
            MapStatus = message;
        }
    }
    public void InspectMapCell(TileMapPoint point)
    {
        MapSelection = point;
        MapInspector.Clear();
        if (_mapSource is not { } map)
        {
            return;
        }
        IReadOnlyList<PropertyRow> rows = PluginGuard.Try($"Reading cell {point.X}, {point.Y}", () => map.Inspect(point), []);
        if (rows.Count == 0)
        {
            MapInspector.Add(PropertyItem.For(PropertyRow.Of("Cell", "Empty: no tile, unit or event here"), null));
            return;
        }
        foreach (PropertyRow row in rows)
        {
            MapInspector.Add(PropertyItem.For(row, (edited, value) => CommitProperty(edited, value, SelectedItem, SelectedItem?.Node)));
        }
    }
    partial void OnMapSnapshotChanged(TileMap? value)
    {
        OnPropertyChanged(nameof(HasMap));
    }
    partial void OnMapToolChanged(TileMapTool? value)
    {
        MapToolValues.Clear();
        foreach (string choice in value?.Values ?? [])
        {
            MapToolValues.Add(choice);
        }
        MapToolValue = value?.Value is { Length: > 0 } preset ? preset : MapToolValues.FirstOrDefault();
        OnPropertyChanged(nameof(MapToolHasValues));
        OnPropertyChanged(nameof(MapHint));
    }
    partial void OnMapLayerIndexChanged(int value)
    {
        OnPropertyChanged(nameof(MapViewLayer));
    }
}
