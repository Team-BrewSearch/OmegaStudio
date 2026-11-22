using System.Text.Json;
using System.Text.Json.Nodes;
using Omega.Plugins;
namespace Disgaea.Formats;
internal static class MapJson
{
    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };
    public static IEnumerable<AssetCommand> Commands(MapNode node)
    {
        yield return new AssetCommand
        {
            Title = "Export map as JSON",
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter("Map JSON", "*.json")],
            SuggestedFileName = node.FileStem + ".json",
            Execute = context =>
            {
                AtomicSave.Write(context.Path!, System.Text.Encoding.UTF8.GetBytes(Export(node.Map).ToJsonString(s_options)));
                context.Log($"Exported {node.Name} to {Path.GetFileName(context.Path!)}.");
            },
        };
        yield return new AssetCommand
        {
            Title = "Replace map from JSON",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("Map JSON", "*.json")],
            Execute = context =>
            {
                Mpd map = Import(JsonNode.Parse(File.ReadAllText(context.Path!)) ?? throw new InvalidDataException("the file is empty"));
                node.Commit(map);
                context.Log($"Replaced {node.Name} from {Path.GetFileName(context.Path!)}: {map.Chunks.Count} chunk(s), {map.TileCount} tiles, {map.Actors.Count} actors.");
                context.Changed = AssetChange.Bytes | AssetChange.Children | AssetChange.Scene | AssetChange.Properties | AssetChange.Dirty;
            },
        };
    }
    public static JsonObject Export(Mpd map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new JsonObject
        {
            ["format"] = "Disgaea DS MPD",
            ["header"] = Convert.ToHexString(map.Header),
            ["textures"] = new JsonArray([.. map.Textures.Select(t => (JsonNode)JsonValue.Create(t))]),
            ["models"] = new JsonArray([.. map.Models.Select(t => (JsonNode)JsonValue.Create(t))]),
            ["chunks"] = new JsonArray([.. map.Chunks.Select(c => (JsonNode)new JsonObject
            {
                ["header"] = Convert.ToHexString(c.Header),
                ["tiles"] = new JsonArray([.. c.Tiles.Select(t => (JsonNode)Fields(t, MapFields.Tile))]),
            })]),
            ["actors"] = new JsonArray([.. map.Actors.Select(a => (JsonNode)Fields(a, MapFields.Actor))]),
        };
    }
    public static Mpd Import(JsonNode json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] header = Hex(json["header"], Mpd.HeaderSize, "header");
        List<MpdChunk> chunks = [];
        foreach (JsonNode? item in Array(json, "chunks"))
        {
            MpdChunk chunk = new(Hex(item?["header"], Mpd.ChunkSize, "chunk header"));
            foreach (JsonNode? tile in Array(item!, "tiles"))
            {
                chunk.Tiles.Add(Record(tile, Mpd.TileSize, MapFields.Tile, "tile"));
            }
            chunks.Add(chunk);
        }
        List<byte[]> actors = [.. Array(json, "actors").Select(a => Record(a, Mpd.ActorSize, MapFields.Actor, "actor"))];
        return Mpd.Create(header, chunks, actors, Numbers(json, "textures"), Numbers(json, "models"));
    }
    private static JsonObject Fields(byte[] record, MapField[] fields)
    {
        JsonObject result = [];
        foreach (MapField field in fields)
        {
            result[field.Name] = field.Text(record);
        }
        result["raw"] = Convert.ToHexString(record);
        return result;
    }
    private static byte[] Record(JsonNode? node, int size, MapField[] fields, string what)
    {
        if (node is not JsonObject item)
        {
            throw new InvalidDataException($"a {what} must be an object");
        }
        byte[] record = Hex(item["raw"], size, what);
        foreach (MapField field in fields)
        {
            if (item[field.Name] is { } value && value.GetValueKind() != JsonValueKind.Null)
            {
                try
                {
                    field.Set(record, value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString());
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException($"{what} field '{field.Name}': {ex.Message}", ex);
                }
            }
        }
        return record;
    }
    private static byte[] Hex(JsonNode? node, int size, string what)
    {
        string text = node?.GetValue<string>() ?? throw new InvalidDataException($"the {what} has no hex bytes");
        byte[] bytes = Convert.FromHexString(text);
        return bytes.Length == size ? bytes : throw new InvalidDataException($"a {what} is {size} bytes; {bytes.Length} were given");
    }
    private static IEnumerable<JsonNode?> Array(JsonNode node, string name)
    {
        return node[name] as JsonArray ?? throw new InvalidDataException($"'{name}' must be a list");
    }
    private static List<uint> Numbers(JsonNode node, string name)
    {
        return [.. Array(node, name).Select(v => v?.GetValue<uint>() ?? throw new InvalidDataException($"'{name}' holds an empty entry"))];
    }
}
