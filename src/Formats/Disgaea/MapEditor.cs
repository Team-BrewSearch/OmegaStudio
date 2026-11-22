using System.Globalization;
using Omega.Plugins;
namespace Disgaea.Formats;
internal static class MapEditor
{
    public const int CellPixels = 16;
    private const int UnitsPerHeight = 4;
    private static readonly int[][] s_walls =
    [
        [0, 100, 1, 101, 2, 3, 0, -1],
        [1, 103, 3, 102, 0, 2, 1, 0],
        [3, 104, 2, 105, 1, 0, 0, 1],
        [2, 107, 0, 106, 3, 1, -1, 0],
    ];
    private static readonly string[] s_geoNames = ["None", .. MapFields.GeoColours];
    private static readonly uint[] s_geoColours = [0xE0E0E0A0, 0xE63C3CA0, 0x46C85AA0, 0x466EE6A0, 0xF0D23CA0, 0xB45ADCA0];
    private static readonly uint[] s_alliance = [0x468CFFFF, 0xE63C3CFF, 0xF08C32FF, 0xA0A0A0FF, 0x5AC878FF];
    public static IReadOnlyList<TileMapTool> Tools { get; } =
    [
        new("select", "Select", "Inspect", TileMapToolKind.Inspect) { Hint = "Click a cell to edit its tile, unit and event below" },
        new("raise", "Raise", "Terrain", TileMapToolKind.Paint) { Values = ["1", "2", "4", "6", "12"], Value = "2", Hint = "Raise painted tiles; walls follow" },
        new("lower", "Lower", "Terrain", TileMapToolKind.Paint) { Values = ["1", "2", "4", "6", "12"], Value = "2", Hint = "Lower painted tiles; walls follow" },
        new("height", "Set height", "Terrain", TileMapToolKind.Paint) { Values = ["0", "4", "8", "12", "16", "24", "36"], Value = "12", Hint = "Set painted tiles flat at this height" },
        new("level", "Level", "Terrain", TileMapToolKind.Paint) { Hint = "Drag from a tile: every painted tile takes its height" },
        new("fill-height", "Fill height", "Terrain", TileMapToolKind.Fill) { Values = ["0", "4", "8", "12", "16", "24", "36"], Value = "12", Hint = "Set the connected area of equal height" },
        new("add", "Add tile", "Tiles", TileMapToolKind.Paint) { Hint = "Add tiles to empty cells, copied from the nearest tile" },
        new("erase", "Erase tile", "Tiles", TileMapToolKind.Paint) { Hint = "Remove the tiles under the brush" },
        new("stamp", "Stamp look", "Tiles", TileMapToolKind.Paint) { Hint = "Drag from a tile: painted tiles copy its top texture" },
        new("mobility", "Mobility", "Attributes", TileMapToolKind.Paint) { Values = MapFields.Mobility, Value = MapFields.Mobility[2] },
        new("fill-mobility", "Fill mobility", "Attributes", TileMapToolKind.Fill) { Values = MapFields.Mobility, Value = MapFields.Mobility[0] },
        new("geo", "Geo panel", "Attributes", TileMapToolKind.Paint) { Values = s_geoNames, Value = s_geoNames[1], Hint = "Paint panels of a colour, or None to remove them" },
        new("fill-geo", "Fill Geo panels", "Attributes", TileMapToolKind.Fill) { Values = s_geoNames, Value = s_geoNames[1], Hint = "Paint the connected area of equal height" },
        new("unit-add", "Place unit", "Units", TileMapToolKind.Paint) { Values = MapFields.Alliance, Value = MapFields.Alliance[1], Hint = "Place a copy of the last unit, on this side" },
        new("move", "Move", "Units", TileMapToolKind.Move) { Hint = "Drag a unit or event tile to a new cell" },
        new("unit-remove", "Remove unit", "Units", TileMapToolKind.Paint),
        new("event-add", "Place event tile", "Events", TileMapToolKind.Paint) { Values = ["1", "2", "3", "4", "5"], Value = "1", Hint = "Event 1 is a base panel start point" },
        new("event-remove", "Remove event tile", "Events", TileMapToolKind.Paint),
    ];
    public static TileMap Snapshot(Mpd map, IReadOnlyList<ImageData?> textures)
    {
        List<(int X, int Y, byte[] Tile)> tiles = [.. Cells(map)];
        List<(int X, int Y, string Label, uint Rgba, string Detail)> markers = [];
        for (int a = 0; a < map.Actors.Count; a++)
        {
            byte[] actor = map.Actors[a];
            markers.Add(((sbyte)actor[5], -(sbyte)actor[6], $"{a + 1}", s_alliance[Math.Min(actor[4], s_alliance.Length - 1)],
                $"Unit {a + 1}: class {BitConverter.ToUInt16(actor, 0)}, level {BitConverter.ToUInt16(actor, 2)}, {MapFields.Alliance[Math.Min(actor[4], MapFields.Alliance.Length - 1)]}"));
        }
        foreach ((MpdChunk chunk, int slot, int x, int z) in Events(map))
        {
            int index = BitConverter.ToUInt16(chunk.Header, Mpd.EventsAt + (slot * 4) + 2);
            markers.Add((x, -z, $"E{index}", 0xFF5AC8FF, $"Event tile {index}"));
        }
        if (tiles.Count == 0 && markers.Count == 0)
        {
            return new TileMap { Left = 0, Top = 0, Columns = 1, Rows = 1, CellPixels = CellPixels };
        }
        IEnumerable<(int X, int Y)> all = tiles.Select(t => (t.X, t.Y)).Concat(markers.Select(m => (m.X, m.Y)));
        int left = all.Min(p => p.X) - 1;
        int top = all.Min(p => p.Y) - 1;
        int columns = all.Max(p => p.X) - left + 2;
        int rows = all.Max(p => p.Y) - top + 2;
        byte[] rgba = new byte[columns * rows * CellPixels * CellPixels * 4];
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }
        Dictionary<(int, int), byte[]> topmost = [];
        foreach ((int x, int y, byte[] tile) in tiles)
        {
            if (!topmost.TryGetValue((x, y), out byte[]? existing) || Top(tile) > Top(existing))
            {
                topmost[(x, y)] = tile;
            }
        }
        int maxHeight = Math.Max(1, topmost.Values.Select(Top).DefaultIfEmpty(0).Max());
        List<TileMapCell> cells = [];
        foreach (((int x, int y), byte[] tile) in topmost)
        {
            Paint(rgba, columns * CellPixels, (x - left) * CellPixels, (y - top) * CellPixels, map, textures, tile);
            int height = Top(tile);
            byte heat = (byte)Math.Clamp(height * 255 / maxHeight, 0, 255);
            uint mobility = tile[119] switch
            {
                1 => 0xF0C83CB0,
                2 => 0xE63C3CC0,
                _ => 0,
            };
            cells.Add(new TileMapCell(x, y,
            [
                new TileMapMark(((uint)heat << 24) | (60u << 16) | ((uint)(255 - heat) << 8) | 0x90, height.ToString(CultureInfo.InvariantCulture)),
                new TileMapMark(mobility, tile[119] switch { 1 => "F", 2 => "X", _ => string.Empty }),
                new TileMapMark(tile[121] != 0 ? s_geoColours[Math.Min(tile[120], s_geoColours.Length - 1)] : 0, tile[121] != 0 ? tile[120].ToString(CultureInfo.InvariantCulture) : string.Empty),
            ]));
        }
        return new TileMap
        {
            Left = left,
            Top = top,
            Columns = columns,
            Rows = rows,
            CellPixels = CellPixels,
            Image = ImageData.Create("map", columns * CellPixels, rows * CellPixels, rgba, "top-down"),
            LayerNames = ["Height", "Mobility", "Geo panels"],
            Cells = cells,
            Markers = [.. markers.Select(m => new TileMapMarker(m.X, m.Y, m.Rgba, m.Label, m.Detail))],
        };
    }
    public static string Apply(Mpd map, TileMapStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        TileMapTool tool = Tools.FirstOrDefault(t => t.Id == stroke.ToolId) ?? throw new InvalidOperationException($"unknown tool '{stroke.ToolId}'");
        List<(int X, int Z)> points = [.. stroke.Points.Select(p => (p.X, -p.Y)).Distinct()];
        if (points.Count == 0)
        {
            return "Nothing was painted.";
        }
        HashSet<(int, int)> touched = [];
        int changed = 0;
        switch (tool.Id)
        {
            case "raise":
            case "lower":
                int step = Number(stroke.Value, 1, 127) * (tool.Id == "raise" ? 1 : -1);
                foreach ((int x, int z, byte[] tile) in AtCells(map, points))
                {
                    for (int c = 0; c < 4; c++)
                    {
                        tile[96 + c] = (byte)(sbyte)Math.Clamp(-(-(sbyte)tile[96 + c] + step), -128, 127);
                    }
                    changed += Touch(touched, x, z);
                }
                break;
            case "height":
            case "fill-height":
                int height = Number(stroke.Value, -127, 128);
                List<(int X, int Z, byte[] Tile)> targets = tool.Kind == TileMapToolKind.Fill ? Flood(map, points[0], t => Top(t)) : AtCells(map, points);
                foreach ((int x, int z, byte[] tile) in targets)
                {
                    SetHeight(tile, height);
                    changed += Touch(touched, x, z);
                }
                break;
            case "level":
                byte[] source = At(map, [points[0]]).FirstOrDefault() ?? throw new InvalidOperationException("start the stroke on a tile");
                foreach ((int x, int z, byte[] tile) in AtCells(map, points.Skip(1)))
                {
                    source.AsSpan(96, 4).CopyTo(tile.AsSpan(96));
                    changed += Touch(touched, x, z);
                }
                break;
            case "stamp":
                byte[] look = At(map, [points[0]]).FirstOrDefault() ?? throw new InvalidOperationException("start the stroke on a tile");
                foreach (byte[] tile in At(map, points.Skip(1)))
                {
                    look.AsSpan(4 * 8, 8).CopyTo(tile.AsSpan(4 * 8));
                    look.AsSpan(9 * 8, 8).CopyTo(tile.AsSpan(9 * 8));
                    changed++;
                }
                break;
            case "add":
                foreach ((int x, int z) in points)
                {
                    if (At(map, [(x, z)]).Count > 0)
                    {
                        continue;
                    }
                    (MpdChunk chunk, byte[] nearest) = Nearest(map, x, z) ?? throw new InvalidOperationException("this map has no tile to copy");
                    (float ox, float oz) = MapScene.Offset(chunk);
                    byte[] tile = (byte[])nearest.Clone();
                    tile[113] = (byte)(sbyte)Math.Clamp(x - (int)ox, -128, 127);
                    tile[114] = (byte)(sbyte)Math.Clamp(z - (int)oz, -128, 127);
                    for (int slot = 0; slot < 4; slot++)
                    {
                        tile.AsSpan(slot * 8, 8).Clear();
                    }
                    chunk.Tiles.Add(tile);
                    changed += Touch(touched, x, z);
                }
                break;
            case "erase":
                foreach (MpdChunk chunk in map.Chunks)
                {
                    (float ox, float oz) = MapScene.Offset(chunk);
                    changed += chunk.Tiles.RemoveAll(t =>
                    {
                        bool hit = points.Contains((Mpd.X(t) + (int)ox, Mpd.Z(t) + (int)oz));
                        if (hit)
                        {
                            _ = touched.Add((Mpd.X(t) + (int)ox, Mpd.Z(t) + (int)oz));
                        }
                        return hit;
                    });
                }
                break;
            case "mobility":
            case "fill-mobility":
                int mobility = Choice(MapFields.Mobility, stroke.Value);
                foreach (byte[] tile in tool.Kind == TileMapToolKind.Fill ? Flood(map, points[0], t => t[119]).Select(c => c.Tile) : At(map, points))
                {
                    tile[119] = (byte)mobility;
                    changed++;
                }
                break;
            case "geo":
            case "fill-geo":
                int geo = Choice(s_geoNames, stroke.Value);
                foreach (byte[] tile in tool.Kind == TileMapToolKind.Fill ? Flood(map, points[0], t => Top(t)).Select(c => c.Tile) : At(map, points))
                {
                    tile[121] = geo == 0 ? (byte)0 : (byte)100;
                    if (geo > 0)
                    {
                        tile[120] = (byte)(geo - 1);
                    }
                    changed++;
                }
                break;
            case "unit-add":
                int side = Choice(MapFields.Alliance, stroke.Value);
                foreach ((int x, int z) in points)
                {
                    if (map.Actors.Any(a => (sbyte)a[5] == x && (sbyte)a[6] == z))
                    {
                        continue;
                    }
                    byte[] actor = map.Actors.Count > 0 ? (byte[])map.Actors[^1].Clone() : NewActor();
                    actor[4] = (byte)side;
                    actor[5] = (byte)(sbyte)Math.Clamp(x, -128, 127);
                    actor[6] = (byte)(sbyte)Math.Clamp(z, -128, 127);
                    map.Actors.Add(actor);
                    changed++;
                }
                break;
            case "unit-remove":
                changed += map.Actors.RemoveAll(a => points.Contains(((sbyte)a[5], (sbyte)a[6])));
                break;
            case "move":
                if (points.Count < 2 && stroke.Points.Count < 2)
                {
                    return "Drag from a unit or event tile to the cell it should move to.";
                }
                (int fx, int fz) = (stroke.Points[0].X, -stroke.Points[0].Y);
                (int tx, int tz) = (stroke.Points[^1].X, -stroke.Points[^1].Y);
                foreach (byte[] actor in map.Actors.Where(a => (sbyte)a[5] == fx && (sbyte)a[6] == fz))
                {
                    actor[5] = (byte)(sbyte)Math.Clamp(tx, -128, 127);
                    actor[6] = (byte)(sbyte)Math.Clamp(tz, -128, 127);
                    changed++;
                }
                foreach ((MpdChunk chunk, int slot, int x, int z) in Events(map).ToList())
                {
                    if (x == fx && z == fz)
                    {
                        (float ox, float oz) = MapScene.Offset(chunk);
                        chunk.Header[Mpd.EventsAt + (slot * 4)] = (byte)(sbyte)Math.Clamp(tx - (int)ox, -128, 127);
                        chunk.Header[Mpd.EventsAt + (slot * 4) + 1] = (byte)(sbyte)Math.Clamp(tz - (int)oz, -128, 127);
                        changed++;
                    }
                }
                break;
            case "event-add":
                int index = Number(stroke.Value, 1, 65535);
                foreach ((int x, int z) in points)
                {
                    if (Events(map).Any(e => e.X == x && e.Z == z))
                    {
                        continue;
                    }
                    MpdChunk chunk = Nearest(map, x, z)?.Chunk ?? map.Chunks.FirstOrDefault() ?? throw new InvalidOperationException("this map has no chunk to hold event tiles");
                    int slot = Enumerable.Range(0, Mpd.EventCount).FirstOrDefault(s => !chunk.Header.AsSpan(Mpd.EventsAt + (s * 4), 4).ContainsAnyExcept((byte)0), -1);
                    if (slot < 0)
                    {
                        throw new InvalidOperationException($"chunk {map.Chunks.IndexOf(chunk)} already holds the 16 event tiles a chunk allows");
                    }
                    (float ox, float oz) = MapScene.Offset(chunk);
                    int at = Mpd.EventsAt + (slot * 4);
                    chunk.Header[at] = (byte)(sbyte)Math.Clamp(x - (int)ox, -128, 127);
                    chunk.Header[at + 1] = (byte)(sbyte)Math.Clamp(z - (int)oz, -128, 127);
                    BitConverter.TryWriteBytes(chunk.Header.AsSpan(at + 2), (ushort)index);
                    changed++;
                }
                break;
            case "event-remove":
                foreach ((MpdChunk chunk, int slot, int x, int z) in Events(map).ToList())
                {
                    if (points.Contains((x, z)))
                    {
                        chunk.Header.AsSpan(Mpd.EventsAt + (slot * 4), 4).Clear();
                        changed++;
                    }
                }
                break;
            default:
                return "This tool does not edit the map.";
        }
        if (touched.Count > 0)
        {
            Walls(map, touched);
        }
        return changed == 0 ? $"{tool.Name}: nothing to change there." : $"{tool.Name}: {changed} change(s).";
    }
    public static IEnumerable<(string Section, MapField Field, byte[] Record, int Offset, int Length)> Inspect(Mpd map, TileMapPoint point)
    {
        (int x, int z) = (point.X, -point.Y);
        foreach (byte[] tile in At(map, [(x, z)]))
        {
            foreach (MapField field in MapFields.Tile)
            {
                yield return ("Tile", field, tile, 0, Mpd.TileSize);
            }
        }
        foreach (byte[] actor in map.Actors.Where(a => (sbyte)a[5] == x && (sbyte)a[6] == z))
        {
            foreach (MapField field in MapFields.Actor)
            {
                yield return ($"Unit {map.Actors.IndexOf(actor) + 1}", field, actor, 0, Mpd.ActorSize);
            }
        }
        foreach ((MpdChunk chunk, int slot, int ex, int ez) in Events(map))
        {
            if (ex == x && ez == z)
            {
                foreach (MapField field in MapFields.Event)
                {
                    yield return ($"Event tile {slot}", field, chunk.Header, Mpd.EventsAt + (slot * 4), 4);
                }
            }
        }
    }
    private static void Walls(Mpd map, HashSet<(int X, int Z)> touched)
    {
        Dictionary<(int, int), byte[]> grid = [];
        foreach ((int x, int y, byte[] tile) in Cells(map))
        {
            (int, int) key = (x, -y);
            if (!grid.TryGetValue(key, out byte[]? existing) || Top(tile) > Top(existing))
            {
                grid[key] = tile;
            }
        }
        byte[]?[] templates = [.. Enumerable.Range(0, 4).Select(slot => Template(map, slot))];
        HashSet<(int, int)> area = [.. touched.SelectMany(p => new[] { p, (p.X + 1, p.Z), (p.X - 1, p.Z), (p.X, p.Z + 1), (p.X, p.Z - 1) })];
        foreach ((int x, int z) in area)
        {
            if (!grid.TryGetValue((x, z), out byte[]? tile))
            {
                continue;
            }
            for (int slot = 0; slot < 4; slot++)
            {
                int[] wall = s_walls[slot];
                grid.TryGetValue((x + wall[6], z + wall[7]), out byte[]? neighbour);
                int bottomLeft = neighbour is null ? 0 : (sbyte)neighbour[96 + wall[4]];
                int bottomRight = neighbour is null ? 0 : (sbyte)neighbour[96 + wall[5]];
                int left = UnitsPerHeight * (bottomLeft - (sbyte)tile[96 + wall[0]]);
                int right = UnitsPerHeight * (bottomRight - (sbyte)tile[96 + wall[2]]);
                int face = slot * 8;
                if (Math.Max(left, right) <= 0)
                {
                    tile.AsSpan(face, 8).Clear();
                    tile[wall[1]] = 0;
                    tile[wall[3]] = 0;
                    continue;
                }
                if (tile[face + 3] == 0 && tile[face + 4] == 0 && tile[face + 5] == 0)
                {
                    if (templates[slot] is not { } template)
                    {
                        continue;
                    }
                    template.CopyTo(tile, face);
                }
                tile[wall[1]] = (byte)(sbyte)bottomLeft;
                tile[wall[3]] = (byte)(sbyte)bottomRight;
                tile[face + 4] = (byte)Math.Clamp(left, 0, 255);
                tile[face + 5] = (byte)Math.Clamp(right, 0, 255);
            }
        }
    }
    private static byte[]? Template(Mpd map, int slot)
    {
        return map.Tiles
            .Select(t => t.AsSpan(slot * 8, 8).ToArray())
            .Where(f => f[6] != 0 && f[3] != 0 && f[6] <= map.Textures.Count && map.Textures[f[6] - 1] != Mpd.NoTexture)
            .GroupBy(f => (f[0], f[1], f[2], f[3], f[6], f[7]))
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();
    }
    private static void Paint(byte[] rgba, int stride, int left, int top, Mpd map, IReadOnlyList<ImageData?> textures, byte[] tile)
    {
        int index = tile[(4 * 8) + 6];
        ImageData? image = index > 0 && index <= map.Textures.Count && index <= textures.Count ? textures[index - 1] : null;
        int shade = 70 + Math.Clamp(Top(tile) * 4, 0, 120);
        if (image is null || (tile[(4 * 8) + 3] == 0 && tile[(4 * 8) + 4] == 0))
        {
            for (int y = 0; y < CellPixels; y++)
            {
                for (int x = 0; x < CellPixels; x++)
                {
                    int o = (((top + y) * stride) + left + x) * 4;
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)shade;
                }
            }
            return;
        }
        (float U, float V)[] uv = MapScene.TopUv(tile, image);
        for (int y = 0; y < CellPixels; y++)
        {
            float t = 1f - ((y + 0.5f) / CellPixels);
            for (int x = 0; x < CellPixels; x++)
            {
                float s = (x + 0.5f) / CellPixels;
                float u = Lerp(Lerp(uv[0].U, uv[1].U, s), Lerp(uv[2].U, uv[3].U, s), t);
                float v = Lerp(Lerp(uv[0].V, uv[1].V, s), Lerp(uv[2].V, uv[3].V, s), t);
                int tx = (((int)MathF.Floor(u * image.Width) % image.Width) + image.Width) % image.Width;
                int ty = (((int)MathF.Floor(v * image.Height) % image.Height) + image.Height) % image.Height;
                int from = ((ty * image.Width) + tx) * 4;
                int o = (((top + y) * stride) + left + x) * 4;
                bool clear = image.Rgba[from + 3] < 128;
                rgba[o] = clear ? (byte)shade : image.Rgba[from];
                rgba[o + 1] = clear ? (byte)shade : image.Rgba[from + 1];
                rgba[o + 2] = clear ? (byte)shade : image.Rgba[from + 2];
            }
        }
    }
    private static float Lerp(float a, float b, float t)
    {
        return a + ((b - a) * t);
    }
    private static IEnumerable<(int X, int Y, byte[] Tile)> Cells(Mpd map)
    {
        foreach (MpdChunk chunk in map.Chunks)
        {
            (float ox, float oz) = MapScene.Offset(chunk);
            foreach (byte[] tile in chunk.Tiles)
            {
                yield return (Mpd.X(tile) + (int)ox, -(Mpd.Z(tile) + (int)oz), tile);
            }
        }
    }
    private static List<byte[]> At(Mpd map, IEnumerable<(int X, int Z)> points)
    {
        return [.. AtCells(map, points).Select(c => c.Tile)];
    }
    private static List<(int X, int Z, byte[] Tile)> AtCells(Mpd map, IEnumerable<(int X, int Z)> points)
    {
        HashSet<(int, int)> wanted = [.. points];
        return [.. Cells(map).Where(c => wanted.Contains((c.X, -c.Y))).Select(c => (c.X, -c.Y, c.Tile))];
    }
    private static IEnumerable<(MpdChunk Chunk, int Slot, int X, int Z)> Events(Mpd map)
    {
        foreach (MpdChunk chunk in map.Chunks)
        {
            (float ox, float oz) = MapScene.Offset(chunk);
            for (int slot = 0; slot < Mpd.EventCount; slot++)
            {
                int at = Mpd.EventsAt + (slot * 4);
                if (chunk.Header.AsSpan(at, 4).ContainsAnyExcept((byte)0))
                {
                    yield return (chunk, slot, (sbyte)chunk.Header[at] + (int)ox, (sbyte)chunk.Header[at + 1] + (int)oz);
                }
            }
        }
    }
    private static List<(int X, int Z, byte[] Tile)> Flood(Mpd map, (int X, int Z) start, Func<byte[], int> key)
    {
        Dictionary<(int, int), byte[]> grid = [];
        foreach ((int x, int y, byte[] tile) in Cells(map))
        {
            grid[(x, -y)] = tile;
        }
        if (!grid.TryGetValue(start, out byte[]? first))
        {
            return [];
        }
        int match = key(first);
        HashSet<(int, int)> seen = [start];
        Queue<(int X, int Z)> queue = new([start]);
        List<(int X, int Z, byte[] Tile)> result = [];
        while (queue.Count > 0)
        {
            (int x, int z) = queue.Dequeue();
            result.Add((x, z, grid[(x, z)]));
            foreach ((int, int) next in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
            {
                if (grid.TryGetValue(next, out byte[]? tile) && key(tile) == match && seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }
        return result;
    }
    private static (MpdChunk Chunk, byte[] Tile)? Nearest(Mpd map, int x, int z)
    {
        (MpdChunk Chunk, byte[] Tile)? best = null;
        int distance = int.MaxValue;
        foreach (MpdChunk chunk in map.Chunks)
        {
            (float ox, float oz) = MapScene.Offset(chunk);
            foreach (byte[] tile in chunk.Tiles)
            {
                int d = Math.Abs(Mpd.X(tile) + (int)ox - x) + Math.Abs(Mpd.Z(tile) + (int)oz - z);
                if (d < distance)
                {
                    distance = d;
                    best = (chunk, tile);
                }
            }
        }
        return best;
    }
    private static int Top(byte[] tile)
    {
        return -Math.Min(Math.Min((sbyte)tile[96], (sbyte)tile[97]), Math.Min((sbyte)tile[98], (sbyte)tile[99]));
    }
    private static void SetHeight(byte[] tile, int height)
    {
        for (int c = 0; c < 4; c++)
        {
            tile[96 + c] = (byte)(sbyte)-height;
        }
    }
    private static int Touch(HashSet<(int, int)> touched, int x, int z)
    {
        _ = touched.Add((x, z));
        return 1;
    }
    private static byte[] NewActor()
    {
        byte[] actor = new byte[Mpd.ActorSize];
        actor[2] = 1;
        actor[8] = 1;
        return actor;
    }
    private static int Number(string text, int min, int max)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max
            ? value
            : throw new InvalidOperationException($"enter a whole number from {min} to {max}");
    }
    private static int Choice(IReadOnlyList<string> choices, string value)
    {
        int index = choices.ToList().IndexOf(value);
        return index >= 0 ? index : throw new InvalidOperationException($"choose one of: {string.Join(", ", choices)}");
    }
}
