using System.Runtime.CompilerServices;
using Omega.Plugins;
namespace Disgaea.Formats;
internal static class MapTextures
{
    private static readonly ConditionalWeakTable<PackView, Func<byte[]?>> s_sources = [];
    public static void Register(PackView maps, Func<byte[]?> textures)
    {
        s_sources.AddOrUpdate(maps, textures);
    }
    public static Func<byte[]?>? For(PackView? maps)
    {
        return maps is not null && s_sources.TryGetValue(maps, out Func<byte[]?>? source) ? source : null;
    }
}
public sealed class MapContainerRecognizer : IContainerRecognizer
{
    public string Id => "disgaea.maps";
    public IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(children);
        PackView? maps = children.OfType<PackView>().FirstOrDefault(c => c.Name.Equals("mpds.dat", StringComparison.OrdinalIgnoreCase));
        IAssetNode? textures = children.FirstOrDefault(c => c.Name.Equals("maptex.dat", StringComparison.OrdinalIgnoreCase));
        if (maps is not null && (textures is RecognizedAsset { Inner: IBinaryAsset } || textures is IBinaryAsset))
        {
            IBinaryAsset bytes = (textures as RecognizedAsset)?.Inner as IBinaryAsset ?? (IBinaryAsset)textures;
            MapTextures.Register(maps, () => bytes.Data.ToArray());
        }
        return null;
    }
}
internal sealed class MapNode : PartNode, ISceneAsset, IDescribable, ICommandSource, ITileMapAsset
{
    private readonly Lock _gate = new();
    private Mpd? _map;
    private (string Key, IReadOnlyList<ImageData?> Images)? _textures;
    public MapNode(PackView owner, Pack pack, int index, IHostServices host)
        : base(owner, pack, index, host)
    {
        Kind = AssetKinds.Model;
        ChildFactory = Groups;
    }
    public Mpd Map
    {
        get
        {
            lock (_gate)
            {
                return _map ??= Mpd.Read(Data);
            }
        }
    }
    public void Commit(Mpd map)
    {
        byte[] bytes = map.Write();
        map.Touch();
        ReplaceBytes(bytes);
        lock (_gate)
        {
            _map = map;
        }
    }
    protected override void Write(byte[] payload)
    {
        Mpd.Read(payload);
        base.Write(payload);
        lock (_gate)
        {
            _map = null;
        }
    }
    public SceneGeometry? GetScene()
    {
        return Scene(default);
    }
    public TileMap TileMap
    {
        get
        {
            Mpd map = Map;
            return MapEditor.Snapshot(map, Textures(map));
        }
    }
    public IReadOnlyList<TileMapTool> Tools => MapEditor.Tools;
    public string Apply(TileMapStroke stroke)
    {
        Mpd map = Mpd.Read(Data);
        string message = MapEditor.Apply(map, stroke);
        Commit(map);
        return message;
    }
    public IReadOnlyList<PropertyRow> Inspect(TileMapPoint point)
    {
        Mpd map = Map;
        return [.. MapEditor.Inspect(map, point).Select(entry => entry.Field.Row(entry.Record.AsSpan(entry.Offset, entry.Length), text =>
        {
            Mpd current = Map;
            if (!ReferenceEquals(current, map))
            {
                throw new InvalidOperationException("the map changed; click the cell again");
            }
            entry.Field.Set(entry.Record.AsSpan(entry.Offset, entry.Length), text);
            Commit(current);
        }) with { Name = $"{entry.Section}: {entry.Field.Name}" })];
    }
    public SceneGeometry Scene(MapFocus focus)
    {
        Mpd map = Map;
        return MapScene.Build(map, Textures(map), focus);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        Mpd map = Map;
        yield return PropertyRow.Of("Content", "Disgaea DS map (MPD)");
        yield return PropertyRow.Of("Chunks", map.Chunks.Count);
        yield return PropertyRow.Of("Tiles", map.TileCount);
        yield return PropertyRow.Of("Actors", map.Actors.Count);
        yield return PropertyRow.Of("Textures", Textures(map).Count(t => t is not null) + " of " + map.Textures.Count + " found in maptex.dat");
        for (int i = 0; i < map.Textures.Count; i++)
        {
            int slot = i;
            yield return PropertyRow.Editable($"Texture {i + 1}", map.Textures[i] == Mpd.NoTexture ? "none" : map.Textures[i].ToString(System.Globalization.CultureInfo.InvariantCulture), text =>
            {
                Mpd edited = Map;
                edited.Textures[slot] = text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? Mpd.NoTexture : uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                Commit(edited);
            }) with { Changed = AssetChange.Properties | AssetChange.Dirty | AssetChange.Scene | AssetChange.Bytes };
        }
        yield return PropertyRow.Editable("Model list", string.Join(", ", map.Models), text =>
        {
            Mpd edited = Map;
            List<uint> models = [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => uint.Parse(v, System.Globalization.CultureInfo.InvariantCulture))];
            edited.Models.Clear();
            edited.Models.AddRange(models);
            Commit(edited);
        }) with { Changed = AssetChange.Properties | AssetChange.Dirty | AssetChange.Bytes };
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        return MapJson.Commands(this);
    }
    public IReadOnlyList<ImageData?> Textures(Mpd map)
    {
        string key = string.Join(",", map.Textures);
        lock (_gate)
        {
            if (_textures is { } cached && cached.Key == key)
            {
                return cached.Images;
            }
        }
        IReadOnlyList<ImageData?> images = Load(map);
        lock (_gate)
        {
            _textures = (key, images);
        }
        return images;
    }
    private List<ImageData?> Load(Mpd map)
    {
        Func<byte[]?>? source = MapTextures.For((Owner.Inner as PartNode)?.Owner);
        byte[]? bytes = source?.Invoke();
        Dsarc? archive = null;
        if (bytes is not null && Dsarc.Claims(bytes))
        {
            try
            {
                archive = Dsarc.Read(bytes);
            }
            catch (InvalidDataException)
            {
                archive = null;
            }
        }
        List<ImageData?> images = [];
        foreach (uint entry in map.Textures)
        {
            ImageData? image = null;
            if (archive is not null && entry != Mpd.NoTexture && entry < archive.Count && Imy.Claims(archive.Get((int)entry)))
            {
                try
                {
                    image = Imy.Read(archive.Get((int)entry)).ToImage(archive.NameOf((int)entry));
                }
                catch (InvalidDataException)
                {
                    image = null;
                }
            }
            images.Add(image);
        }
        return images;
    }
    private IEnumerable<IAssetNode> Groups()
    {
        Mpd map = Map;
        yield return new MapGroupNode(this, "Tiles", $"{map.TileCount} tiles", MapGroupKind.Tiles);
        yield return new MapGroupNode(this, "Actors", $"{map.Actors.Count} actors", MapGroupKind.Actors);
        yield return new MapGroupNode(this, "Chunks", $"{map.Chunks.Count} chunks", MapGroupKind.Chunks);
    }
}
internal enum MapGroupKind
{
    Tiles,
    Actors,
    Chunks,
    Chunk,
}
internal sealed class MapGroupNode : AssetNode, ISceneAsset, ICommandSource, IDescribable
{
    private readonly MapNode _map;
    private readonly MapGroupKind _kind;
    private readonly MpdChunk? _chunk;
    public MapGroupNode(MapNode map, string name, string detail, MapGroupKind kind, MpdChunk? chunk = null)
        : base(name, $"{map.PathText}/{name}", AssetKinds.Container, detail)
    {
        _map = map;
        _kind = kind;
        _chunk = chunk;
        ChildFactory = Children;
    }
    public SceneGeometry? GetScene()
    {
        Mpd map = _map.Map;
        return _map.Scene(new MapFocus(Chunk: _chunk is null ? -1 : map.Chunks.IndexOf(_chunk)));
    }
    public IEnumerable<PropertyRow> Describe()
    {
        if (_chunk is null || !_map.Map.Chunks.Contains(_chunk))
        {
            yield break;
        }
        MpdChunk chunk = _chunk;
        foreach (MapField field in MapFields.Chunk)
        {
            yield return field.Row(chunk.Header, text =>
            {
                Mpd map = _map.Map;
                if (!map.Chunks.Contains(chunk))
                {
                    throw new InvalidOperationException("this chunk is no longer in the map");
                }
                field.Set(chunk.Header, text);
                _map.Commit(map);
            });
        }
        yield return PropertyRow.Of("Tiles", chunk.Tiles.Count);
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        AssetChange all = AssetChange.Bytes | AssetChange.Children | AssetChange.Scene | AssetChange.Properties | AssetChange.Dirty;
        switch (_kind)
        {
            case MapGroupKind.Tiles:
                yield return new AssetCommand
                {
                    Title = "Add tile",
                    Execute = context =>
                    {
                        Mpd map = _map.Map;
                        MpdChunk chunk = map.Chunks.FirstOrDefault(c => c.Tiles.Count > 0) ?? throw new InvalidOperationException("this map has no chunk with tiles to copy");
                        byte[] tile = (byte[])chunk.Tiles[^1].Clone();
                        HashSet<(int, int)> used = [.. map.Tiles.Select(t => (Mpd.X(t), Mpd.Z(t)))];
                        (int x, int z) = (Mpd.X(tile), Mpd.Z(tile));
                        while (used.Contains((x, z)) && x < 127)
                        {
                            x++;
                        }
                        tile[113] = (byte)(sbyte)x;
                        tile[114] = (byte)(sbyte)z;
                        chunk.Tiles.Add(tile);
                        _map.Commit(map);
                        context.Log($"Added a tile at {x}, {z}, copied from the last tile of its chunk.");
                        context.Changed = all;
                    },
                };
                break;
            case MapGroupKind.Actors:
                yield return new AssetCommand
                {
                    Title = "Add actor",
                    Execute = context =>
                    {
                        Mpd map = _map.Map;
                        byte[] actor = map.Actors.Count > 0 ? (byte[])map.Actors[^1].Clone() : new byte[Mpd.ActorSize];
                        if (map.Actors.Count == 0)
                        {
                            actor[2] = 1;
                            actor[4] = 1;
                            actor[8] = 1;
                        }
                        map.Actors.Add(actor);
                        _map.Commit(map);
                        context.Log($"Added actor {map.Actors.Count}.");
                        context.Changed = all;
                    },
                };
                break;
        }
    }
    private IEnumerable<IAssetNode> Children()
    {
        Mpd map = _map.Map;
        switch (_kind)
        {
            case MapGroupKind.Tiles:
                return map.Tiles.Select(t => new MapRecordNode(_map, t, 0, Mpd.TileSize, MapFields.Tile, MapRecordKind.Tile, $"Tile {Mpd.X(t)}, {Mpd.Z(t)}"));
            case MapGroupKind.Actors:
                return map.Actors.Select((a, i) => new MapRecordNode(_map, a, 0, Mpd.ActorSize, MapFields.Actor, MapRecordKind.Actor, $"Actor {i + 1}"));
            case MapGroupKind.Chunks:
                return map.Chunks.Select((c, i) => new MapGroupNode(_map, $"Chunk {i} (id {c.Id})", $"{c.Tiles.Count} tiles", MapGroupKind.Chunk, c));
            default:
                MpdChunk chunk = _chunk!;
                return Enumerable.Range(0, Mpd.ObjectCount)
                    .Select(o => new MapRecordNode(_map, chunk.Header, 64 + (o * Mpd.ObjectSize), Mpd.ObjectSize, MapFields.Object, MapRecordKind.Object, $"Object {o}"))
                    .Concat(Enumerable.Range(0, Mpd.EventCount)
                    .Select(e => new MapRecordNode(_map, chunk.Header, Mpd.EventsAt + (e * 4), 4, MapFields.Event, MapRecordKind.Event, $"Event tile {e}")));
        }
    }
}
internal enum MapRecordKind
{
    Tile,
    Actor,
    Object,
    Event,
}
internal sealed class MapRecordNode : BinaryAssetNode, ISceneAsset, IDescribable, ICommandSource
{
    private readonly MapNode _map;
    private readonly byte[] _record;
    private readonly int _offset;
    private readonly int _length;
    private readonly MapField[] _fields;
    private readonly MapRecordKind _kind;
    public MapRecordNode(MapNode map, byte[] record, int offset, int length, MapField[] fields, MapRecordKind kind, string name)
        : base(name, $"{map.PathText}/{name}", AssetKinds.Model, Summary(kind, record, offset))
    {
        _map = map;
        _record = record;
        _offset = offset;
        _length = length;
        _fields = fields;
        _kind = kind;
    }
    public override ReadOnlySpan<byte> Data => _map.Data;
    public override string Extension => ".mpd";
    public override string FileStem => _map.FileStem;
    public override bool CanReplaceBytes => _map.CanReplaceBytes;
    private bool Attached
    {
        get
        {
            Mpd map = _map.Map;
            return _kind switch
            {
                MapRecordKind.Tile => map.Chunks.Any(c => c.Tiles.Contains(_record)),
                MapRecordKind.Actor => map.Actors.Contains(_record),
                _ => map.Chunks.Any(c => c.Header == _record),
            };
        }
    }
    protected override void Write(byte[] payload)
    {
        _map.ReplaceBytes(payload);
    }
    public SceneGeometry? GetScene()
    {
        Mpd map = _map.Map;
        MapFocus focus = _kind switch
        {
            MapRecordKind.Tile => new MapFocus(Tile: map.Tiles.ToList().IndexOf(_record)),
            MapRecordKind.Actor => new MapFocus(Actor: map.Actors.IndexOf(_record)),
            MapRecordKind.Event => new MapFocus(Chunk: map.Chunks.FindIndex(c => c.Header == _record), Event: (_offset - Mpd.EventsAt) / 4),
            _ => new MapFocus(Chunk: map.Chunks.FindIndex(c => c.Header == _record)),
        };
        return _map.Scene(focus);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        if (!Attached)
        {
            yield return PropertyRow.Of("State", "No longer in the map; select the map again to see its current contents");
            yield break;
        }
        foreach (MapField field in _fields)
        {
            yield return field.Row(_record.AsSpan(_offset, _length), text => Edit(record => field.Set(record, text)));
        }
        yield return PropertyRow.Editable("Raw bytes", Convert.ToHexString(_record, _offset, _length), text => Edit(record =>
        {
            byte[] bytes = Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));
            if (bytes.Length != _length)
            {
                throw new InvalidDataException($"the record is {_length} bytes; {bytes.Length} were given");
            }
            bytes.CopyTo(record);
        })) with { Changed = AssetChange.Properties | AssetChange.Dirty | AssetChange.Scene | AssetChange.Bytes };
    }
    public IEnumerable<AssetCommand> GetCommands()
    {
        if (_kind is not (MapRecordKind.Tile or MapRecordKind.Actor))
        {
            yield break;
        }
        string what = _kind == MapRecordKind.Tile ? "tile" : "actor";
        yield return new AssetCommand
        {
            Title = $"Duplicate {what}",
            Execute = context => Structure(context, map =>
            {
                List<byte[]> list = Owner(map);
                list.Insert(list.IndexOf(_record) + 1, (byte[])_record.Clone());
                return $"Duplicated {Name}; the copy follows it.";
            }),
        };
        yield return new AssetCommand
        {
            Title = $"Remove {what}",
            Execute = context => Structure(context, map =>
            {
                _ = Owner(map).Remove(_record);
                Detail = "removed";
                return $"Removed {Name}.";
            }),
        };
    }
    private List<byte[]> Owner(Mpd map)
    {
        return _kind == MapRecordKind.Actor
            ? map.Actors
            : map.Chunks.FirstOrDefault(c => c.Tiles.Contains(_record))?.Tiles ?? throw new InvalidOperationException($"{Name} is no longer in the map");
    }
    private void Structure(AssetCommandContext context, Func<Mpd, string> change)
    {
        if (!Attached)
        {
            throw new InvalidOperationException($"{Name} is no longer in the map");
        }
        Mpd map = _map.Map;
        string message = change(map);
        _map.Commit(map);
        context.Log(message + " Select the map again to refresh the list.");
        context.Changed = AssetChange.Bytes | AssetChange.Scene | AssetChange.Properties | AssetChange.Dirty;
    }
    private void Edit(SpanAction change)
    {
        if (!Attached)
        {
            throw new InvalidOperationException($"{Name} is no longer in the map");
        }
        Mpd map = _map.Map;
        change(_record.AsSpan(_offset, _length));
        _map.Commit(map);
        Detail = Summary(_kind, _record, _offset);
    }
    private static string Summary(MapRecordKind kind, byte[] record, int offset)
    {
        return kind switch
        {
            MapRecordKind.Tile => $"height {-(sbyte)record[96]}{(record[121] != 0 ? ", geo panel" : string.Empty)}",
            MapRecordKind.Actor => $"class {BitConverter.ToUInt16(record, 0)}, level {BitConverter.ToUInt16(record, 2)}, at {(sbyte)record[5]}, {(sbyte)record[6]}",
            MapRecordKind.Event => record.AsSpan(offset, 4).ContainsAnyExcept((byte)0) ? $"event {BitConverter.ToUInt16(record, offset + 2)} at {(sbyte)record[offset]}, {(sbyte)record[offset + 1]}" : "empty",
            _ => record[offset + 22] != 0 ? $"model {(sbyte)record[offset + 21]}" : record.AsSpan(offset, Mpd.ObjectSize).ContainsAnyExcept((byte)0) ? "hidden" : "empty",
        };
    }
    private delegate void SpanAction(Span<byte> record);
}
