using Kartz.Formats.Binary;
using Omega.Plugins;
using System.Buffers.Binary;
namespace Kartz.Formats;

public sealed class ColSection
{
    public const int HeaderSize = 16;
    public required int Offset { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Flags2 { get; init; }
    public required int[] Positions { get; init; }
    public required int[] Normals { get; init; }
    public required uint[] SegmentLengths { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int Size => HeaderSize + (Positions.Length * 4) + (Normals.Length * 4) + (SegmentLengths.Length * 4);
    public float X(int vertex)
    {
        return Positions[vertex * 3] / 4096f;
    }
    public float Y(int vertex)
    {
        return Positions[(vertex * 3) + 1] / 4096f;
    }
    public float Z(int vertex)
    {
        return Positions[(vertex * 3) + 2] / 4096f;
    }
}
public sealed class ColSector
{
    public required int Offset { get; init; }
    public required byte[] Record { get; init; }
    public required ColSection Start { get; init; }
    public required ColSection End { get; init; }
    public int Index => BinaryPrimitives.ReadUInt16LittleEndian(Record);
}
public sealed class ColModel
{
    public const int RecordSize = 112;
    private const int RecordStartField = 0x60;
    private const int RecordEndField = 0x64;
    private const int RecordLengthField = 0x04;
    private const int RecordForwardField = 0x08;
    private const int RecordShapeField = 0x44;
    private const int ShapeTolerance = 205;
    private const double CollapseDistance = 0.1;
    private static readonly int[] LinkFields = [0x30, 0x34, 0x38, 0x3C];
    private readonly List<ColSector> _sectors;
    private readonly List<ColSection> _sections;
    private ColModel(List<ColSector> sectors, List<ColSection> sections)
    {
        _sectors = sectors;
        _sections = sections;
    }
    public IReadOnlyList<ColSector> Sectors => _sectors;
    public IReadOnlyList<ColSection> Sections => _sections;
    public static bool IsCollision(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out _);
    }
    public static ColModel Parse(ReadOnlySpan<byte> blob)
    {
        return TryRead(blob, out ColModel? model) && model is not null
            ? model
            : throw new InvalidDataException("not a Kartz .col collision surface");
    }
    public byte[] Serialize()
    {
        int table = (Sectors.Count + 1) * 4;
        Dictionary<ColSection, int> placed = [];
        int cursor = table;
        foreach (ColSection section in Sections)
        {
            placed[section] = cursor;
            cursor += section.Size;
        }
        int[] spot = new int[Sectors.Count];
        int at = cursor;
        for (int i = 0; i < Sectors.Count; i++)
        {
            spot[i] = at;
            at += Sectors[i].Record.Length;
        }
        Dictionary<int, int> links = [];
        for (int i = 0; i < Sectors.Count; i++)
        {
            links[Sectors[i].Offset] = spot[i];
        }
        byte[] output = new byte[at];
        for (int i = 0; i < Sectors.Count; i++)
        {
            ColSector sector = Sectors[i];
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), (uint)spot[i]);
            sector.Record.CopyTo(output.AsSpan(spot[i]));
            Write(output, spot[i] + RecordStartField, (uint)placed[sector.Start], sector.Record.Length);
            Write(output, spot[i] + RecordEndField, (uint)placed[sector.End], sector.Record.Length);
            foreach (int field in LinkFields)
            {
                if (field + 4 > sector.Record.Length)
                {
                    continue;
                }
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(sector.Record.AsSpan(field));
                if (links.TryGetValue((int)value, out int destination))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        output.AsSpan(spot[i] + field), (uint)destination);
                }
            }
        }
        foreach (ColSection section in Sections)
        {
            WriteSection(output, placed[section], section);
        }
        return output;
    }
    public SceneGeometry ToScene(string name)
    {
        ColSurface surface = Tessellate();
        List<float> positions = new(surface.Slots.Count * 3);
        List<float> normals = new(surface.Slots.Count * 3);
        List<byte> colors = new(surface.Slots.Count * 4);
        foreach ((ColSection section, int vertex, _) in surface.Slots)
        {
            Add(positions, normals, colors, section, vertex);
        }
        SceneMesh mesh = new()
        {
            Name = name,
            Positions = [.. positions],
            Normals = [.. normals],
            Colors = [.. colors],
            Indices = [.. surface.Indices],
            Submeshes = [new SceneSubmesh("collision", 0, surface.Indices.Count, -1)],
        };
        return new SceneGeometry { Meshes = [mesh], Lighting = SceneLighting.Shaded };
    }
    public int SceneVertexCount
    {
        get
        {
            int total = 0;
            foreach (ColSector sector in Sectors)
            {
                if (sector.Start.VertexCount >= 2 && sector.End.VertexCount >= 2)
                {
                    total += sector.Start.VertexCount + sector.End.VertexCount;
                }
            }
            return total;
        }
    }
    public void ApplyMesh(
        IReadOnlyList<float> positions,
        IReadOnlyList<float> normals,
        IReadOnlyList<int> indices,
        Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(indices);
        if (positions.Count < 9)
        {
            throw new InvalidDataException("the model has no geometry to read a collision surface from");
        }
        ColSurface surface = Tessellate();
        int[] source = Match(surface, positions, indices, log, out HashSet<ColSection> missing);
        HashSet<ColSector> describable = [];
        foreach (ColSector sector in Sectors)
        {
            byte[] probe = [.. sector.Record];
            Reshape(sector, probe);
            if (Describes(sector.Record, probe))
            {
                describable.Add(sector);
            }
        }
        Dictionary<ColSection, int[]> before = [];
        Dictionary<ColSection, int[]> facing = [];
        foreach (ColSection section in Sections)
        {
            before[section] = [.. section.Positions];
            facing[section] = [.. section.Normals];
        }
        for (int slot = 0; slot < surface.Slots.Count; slot++)
        {
            if (source[slot] < 0)
            {
                continue;
            }
            (ColSection section, int vertex, _) = surface.Slots[slot];
            for (int axis = 0; axis < 3; axis++)
            {
                int at = (vertex * 3) + axis;
                int read = (source[slot] * 3) + axis;
                section.Positions[at] = Fixed(positions[read]);
                section.Normals[at] = read < normals.Count ? Fixed(normals[read]) : section.Normals[at];
            }
        }
        Flatten(missing, log);
        foreach (ColSector sector in Sectors)
        {
            double[] from = Vertex(sector.Start, (sector.Start.VertexCount - 1) / 2);
            double[] to = Vertex(sector.End, (sector.End.VertexCount - 1) / 2);
            _ = Direction(to, from, out double span);
            if (span >= CollapseDistance)
            {
                continue;
            }
            throw new InvalidDataException(
                $"the model puts the cross section at ({from[0]:0.###}, {from[1]:0.###}, {from[2]:0.###}) "
                + "on top of the next one, which would leave a sector with no length for the game to stand on. "
                + "Move it clear of its neighbour: the chain of cross sections has a fixed length, so one that "
                + "the model leaves out is laid flat between its neighbours rather than removed.");
        }
        HashSet<ColSection> moved = [];
        foreach (ColSection section in Sections)
        {
            if (section.Positions.AsSpan().SequenceEqual(before[section]))
            {
                facing[section].CopyTo(section.Normals.AsSpan());
                continue;
            }
            moved.Add(section);
            Rewidth(section);
        }
        int rebuilt = 0;
        int kept = 0;
        foreach (ColSector sector in Sectors)
        {
            if (!moved.Contains(sector.Start) && !moved.Contains(sector.End))
            {
                continue;
            }
            if (describable.Contains(sector))
            {
                Reshape(sector, sector.Record);
                rebuilt++;
            }
            else
            {
                kept++;
            }
        }
        if (moved.Count > 0)
        {
            log?.Invoke(
                $"{moved.Count} of {Sections.Count} cross sections moved, so the length, heading and shape "
                + $"of {rebuilt} sector record(s) were rebuilt"
                + (kept == 0
                    ? "."
                    : $" and {kept} were left alone, whose stored shape the plugin cannot reproduce."));
        }
    }
    private static bool Describes(ReadOnlySpan<byte> record, ReadOnlySpan<byte> probe)
    {
        for (int at = RecordShapeField; at + 4 <= RecordShapeField + 24 && at + 4 <= record.Length; at += 4)
        {
            int stored = BinaryPrimitives.ReadInt32LittleEndian(record[at..]);
            int rebuilt = BinaryPrimitives.ReadInt32LittleEndian(probe[at..]);
            if (Math.Abs(stored - rebuilt) > ShapeTolerance)
            {
                return false;
            }
        }
        return true;
    }
    private static int[] Match(
        ColSurface surface,
        IReadOnlyList<float> positions,
        IReadOnlyList<int> indices,
        Action<string>? log,
        out HashSet<ColSection> missing)
    {
        missing = [];
        int[] source = new int[surface.Slots.Count];
        int vertices = positions.Count / 3;
        if (vertices == surface.Slots.Count && Exported(surface, indices))
        {
            for (int slot = 0; slot < source.Length; slot++)
            {
                source[slot] = slot;
            }
            return source;
        }
        int[] points = Trace(surface, positions, indices, vertices);
        int matched = points.Count(point => point >= 0);
        if (matched * 2 >= points.Length)
        {
            HashSet<ColSection> traced = [];
            foreach ((ColSection section, int _, int point) in surface.Slots)
            {
                _ = (points[point] >= 0 ? traced : missing).Add(section);
            }
            missing.ExceptWith(traced);
            for (int slot = 0; slot < source.Length; slot++)
            {
                source[slot] = points[surface.Slots[slot].Point];
            }
            int stranded = points.Length - matched;
            log?.Invoke(
                $"The model has {vertices} vertices where the surface has {surface.PointCount} points, so they "
                + $"were matched by position and by the triangles around them: {matched} matched"
                + (stranded == 0
                    ? "."
                    : $", {stranded} left where they are, with nothing in the model to match."));
            return source;
        }
        for (int slot = 0; slot < source.Length; slot++)
        {
            (ColSection section, int vertex, _) = surface.Slots[slot];
            source[slot] = Nearest(section, vertex, positions, vertices);
        }
        log?.Invoke(
            $"The model has {vertices} vertices and {indices.Count / 3} triangles where the surface has "
            + $"{surface.PointCount} points and {surface.Indices.Count / 3} triangles, and too little of it lines "
            + "up with the surface, so each point took the nearest vertex of the model.");
        return source;
    }
    private static bool Exported(ColSurface surface, IReadOnlyList<int> indices)
    {
        if (indices.Count != surface.Indices.Count)
        {
            return false;
        }
        for (int corner = 0; corner < indices.Count; corner++)
        {
            if (indices[corner] != surface.Indices[corner])
            {
                return false;
            }
        }
        return true;
    }
    private static int[] Trace(
        ColSurface surface, IReadOnlyList<float> positions, IReadOnlyList<int> indices, int vertices)
    {
        (int, int, int)[] grid = new (int, int, int)[vertices];
        Dictionary<(int, int, int), int> byPosition = [];
        for (int vertex = 0; vertex < vertices; vertex++)
        {
            grid[vertex] = (
                Fixed(positions[vertex * 3]),
                Fixed(positions[(vertex * 3) + 1]),
                Fixed(positions[(vertex * 3) + 2]));
            byPosition.TryAdd(grid[vertex], vertex);
        }
        int[] points = new int[surface.PointCount];
        Array.Fill(points, -1);
        HashSet<(int, int, int)> claimed = [];
        foreach ((ColSection section, int vertex, int point) in surface.Slots)
        {
            (int, int, int) key = (
                section.Positions[vertex * 3],
                section.Positions[(vertex * 3) + 1],
                section.Positions[(vertex * 3) + 2]);
            if (byPosition.TryGetValue(key, out int found))
            {
                points[point] = found;
                claimed.Add(key);
            }
        }
        Dictionary<(int, int), List<int>> opposite = [];
        for (int at = 0; at + 2 < indices.Count; at += 3)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int a = indices[at + corner];
                int b = indices[at + ((corner + 1) % 3)];
                int c = indices[at + ((corner + 2) % 3)];
                if (a < 0 || b < 0 || c < 0 || a >= vertices || b >= vertices || c >= vertices)
                {
                    continue;
                }
                (int, int) edge = a < b ? (a, b) : (b, a);
                if (!opposite.TryGetValue(edge, out List<int>? third))
                {
                    opposite[edge] = third = [];
                }
                third.Add(c);
            }
        }
        for (bool progress = true; progress;)
        {
            progress = false;
            Dictionary<int, Dictionary<int, int>> votes = [];
            for (int at = 0; at + 2 < surface.Indices.Count; at += 3)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    int point = surface.Slots[surface.Indices[at + corner]].Point;
                    int one = points[surface.Slots[surface.Indices[at + ((corner + 1) % 3)]].Point];
                    int other = points[surface.Slots[surface.Indices[at + ((corner + 2) % 3)]].Point];
                    if (points[point] >= 0 || one < 0 || other < 0)
                    {
                        continue;
                    }
                    (int, int) edge = one < other ? (one, other) : (other, one);
                    if (!opposite.TryGetValue(edge, out List<int>? candidates))
                    {
                        continue;
                    }
                    foreach (int candidate in candidates)
                    {
                        if (claimed.Contains(grid[candidate]))
                        {
                            continue;
                        }
                        if (!votes.TryGetValue(point, out Dictionary<int, int>? tally))
                        {
                            votes[point] = tally = [];
                        }
                        tally[candidate] = tally.GetValueOrDefault(candidate) + 1;
                    }
                }
            }
            foreach ((int point, Dictionary<int, int> tally) in votes)
            {
                int best = -1;
                int score = 0;
                foreach ((int candidate, int count) in tally)
                {
                    if (count > score)
                    {
                        score = count;
                        best = candidate;
                    }
                }
                if (best >= 0 && claimed.Add(grid[best]))
                {
                    points[point] = best;
                    progress = true;
                }
            }
        }
        return points;
    }
    private void Flatten(HashSet<ColSection> missing, Action<string>? log)
    {
        int laid = 0;
        foreach (ColSection section in missing.OrderBy(item => item.Offset))
        {
            ColSector? before = null;
            ColSector? after = null;
            foreach (ColSector sector in Sectors)
            {
                if (sector.End == section)
                {
                    before = sector;
                }
                if (sector.Start == section)
                {
                    after = sector;
                }
            }
            if (before is null || after is null || before.Start == section || after.End == section)
            {
                log?.Invoke(
                    $"The cross section at ({section.X(0):0.###}, {section.Y(0):0.###}, {section.Z(0):0.###}) is "
                    + "missing from the model but sits at the end of the chain, with no pair of neighbours to lie "
                    + "between, so it was left where it is.");
                continue;
            }
            Lay(section, before.Start, after.End);
            laid++;
        }
        if (laid > 0)
        {
            log?.Invoke(
                $"{laid} cross section(s) the model leaves out were laid on the line between their neighbours, "
                + "so the road runs straight through them and the chain keeps its length.");
        }
    }
    private static void Lay(ColSection section, ColSection previous, ColSection next)
    {
        int middle = (section.VertexCount - 1) / 2;
        double[] here = Vertex(section, middle);
        double[] from = Vertex(previous, (previous.VertexCount - 1) / 2);
        double[] to = Vertex(next, (next.VertexCount - 1) / 2);
        double[] along = [to[0] - from[0], to[1] - from[1], to[2] - from[2]];
        double span = (along[0] * along[0]) + (along[1] * along[1]) + (along[2] * along[2]);
        double where = span > 0
            ? (((here[0] - from[0]) * along[0])
                + ((here[1] - from[1]) * along[1])
                + ((here[2] - from[2]) * along[2])) / span
            : 0.5;
        where = Math.Clamp(where, 0.05, 0.95);
        for (int vertex = 0; vertex < section.VertexCount; vertex++)
        {
            int offset = vertex - middle;
            int fromArm = Arm(previous, offset);
            int toArm = Arm(next, offset);
            double[] start = Vertex(previous, fromArm);
            double[] end = Vertex(next, toArm);
            double[] facing = new double[3];
            for (int axis = 0; axis < 3; axis++)
            {
                section.Positions[(vertex * 3) + axis] =
                    Fixed(start[axis] + ((end[axis] - start[axis]) * where));
                facing[axis] = previous.Normals[(fromArm * 3) + axis]
                    + next.Normals[(toArm * 3) + axis];
            }
            double length = Math.Sqrt(
                (facing[0] * facing[0]) + (facing[1] * facing[1]) + (facing[2] * facing[2]));
            for (int axis = 0; axis < 3; axis++)
            {
                section.Normals[(vertex * 3) + axis] = length > 0
                    ? Fixed(facing[axis] / length)
                    : previous.Normals[(fromArm * 3) + axis];
            }
        }
    }
    private static int Arm(ColSection section, int offset)
    {
        int middle = (section.VertexCount - 1) / 2;
        return Math.Clamp(middle + offset, 0, section.VertexCount - 1);
    }
    private static int Nearest(
        ColSection section, int vertex, IReadOnlyList<float> positions, int vertices)
    {
        double x = section.X(vertex);
        double y = section.Y(vertex);
        double z = section.Z(vertex);
        int best = -1;
        double nearest = double.MaxValue;
        for (int i = 0; i < vertices; i++)
        {
            double dx = positions[i * 3] - x;
            double dy = positions[(i * 3) + 1] - y;
            double dz = positions[(i * 3) + 2] - z;
            double distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < nearest)
            {
                nearest = distance;
                best = i;
            }
        }
        return best;
    }
    private static void Rewidth(ColSection section)
    {
        int count = section.VertexCount;
        int middle = (count - 1) / 2;
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == middle)
            {
                continue;
            }
            double walked = 0;
            int from = Math.Min(i, middle);
            int to = Math.Max(i, middle);
            for (int step = from; step < to; step++)
            {
                walked += Distance(section, step, step + 1);
            }
            section.SegmentLengths[at++] = (uint)Fixed(walked);
        }
    }
    private static void Reshape(ColSector sector, Span<byte> record)
    {
        if (record.Length < RecordShapeField + 24)
        {
            return;
        }
        double[] startMiddle = Vertex(sector.Start, (sector.Start.VertexCount - 1) / 2);
        double[] endMiddle = Vertex(sector.End, (sector.End.VertexCount - 1) / 2);
        double[] forward = Direction(endMiddle, startMiddle, out double length);
        if (length <= 0)
        {
            return;
        }
        BinaryPrimitives.WriteInt32LittleEndian(record[RecordLengthField..], Fixed(length));
        for (int axis = 0; axis < 3; axis++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                record[(RecordForwardField + (axis * 4))..], Fixed(forward[axis]));
        }
        BinaryPrimitives.WriteInt32LittleEndian(
            record[RecordShapeField..], Fixed(Slant(sector.Start, forward)));
        BinaryPrimitives.WriteInt32LittleEndian(
            record[(RecordShapeField + 4)..], Fixed(Slant(sector.End, forward)));
        int[] corners = [0, 1];
        double[]?[] shape = new double[2][];
        double[] sides = new double[2];
        foreach (int corner in corners)
        {
            int startVertex = corner == 0 ? 0 : sector.Start.VertexCount - 1;
            int endVertex = corner == 0 ? 0 : sector.End.VertexCount - 1;
            double[] arm = Direction(Vertex(sector.Start, startVertex), startMiddle, out double reach);
            double[] point = Vertex(sector.End, endVertex);
            shape[corner] = reach > 0 ? Project(point, startMiddle, arm, forward) : null;
            sides[corner] = (forward[0] * (point[2] - endMiddle[2]))
                - (forward[2] * (point[0] - endMiddle[0]));
        }
        int outer = sides[1] >= sides[0] ? 1 : 0;
        if (shape[outer] is not { } near || shape[1 - outer] is not { } far)
        {
            return;
        }
        BinaryPrimitives.WriteInt32LittleEndian(record[(RecordShapeField + 8)..], Fixed(near[1]));
        BinaryPrimitives.WriteInt32LittleEndian(record[(RecordShapeField + 12)..], Fixed(Math.Abs(near[0])));
        BinaryPrimitives.WriteInt32LittleEndian(record[(RecordShapeField + 16)..], Fixed(far[1]));
        BinaryPrimitives.WriteInt32LittleEndian(record[(RecordShapeField + 20)..], Fixed(Math.Abs(far[0])));
    }
    private static double Slant(ColSection section, double[] forward)
    {
        double[] chord = Direction(
            Vertex(section, section.VertexCount - 1), Vertex(section, 0), out double span);
        if (span <= 0)
        {
            return 0;
        }
        double cosine = (chord[0] * forward[0]) + (chord[1] * forward[1]) + (chord[2] * forward[2]);
        return Math.Acos(Math.Clamp(cosine, -1, 1));
    }
    private static double[]? Project(double[] point, double[] origin, double[] across, double[] forward)
    {
        double determinant = (across[0] * forward[2]) - (across[2] * forward[0]);
        if (Math.Abs(determinant) < 1e-9)
        {
            return null;
        }
        double x = point[0] - origin[0];
        double z = point[2] - origin[2];
        return [
            ((x * forward[2]) - (z * forward[0])) / determinant,
            ((across[0] * z) - (across[2] * x)) / determinant,
        ];
    }
    private static double[] Direction(double[] to, double[] from, out double length)
    {
        double[] delta = [to[0] - from[0], to[1] - from[1], to[2] - from[2]];
        length = Math.Sqrt((delta[0] * delta[0]) + (delta[1] * delta[1]) + (delta[2] * delta[2]));
        if (length > 0)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                delta[axis] /= length;
            }
        }
        return delta;
    }
    private static double[] Vertex(ColSection section, int vertex)
    {
        return [
            section.Positions[vertex * 3] / 4096.0,
            section.Positions[(vertex * 3) + 1] / 4096.0,
            section.Positions[(vertex * 3) + 2] / 4096.0,
        ];
    }
    private static double Distance(ColSection section, int from, int to)
    {
        double dx = (section.Positions[to * 3] - section.Positions[from * 3]) / 4096.0;
        double dy = (section.Positions[(to * 3) + 1] - section.Positions[(from * 3) + 1]) / 4096.0;
        double dz = (section.Positions[(to * 3) + 2] - section.Positions[(from * 3) + 2]) / 4096.0;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
    private static int Fixed(double value)
    {
        return (int)Math.Round(value * 4096.0);
    }
    private sealed record ColSurface(
        List<(ColSection Section, int Vertex, int Point)> Slots, List<int> Indices, int PointCount);
    private ColSurface Tessellate()
    {
        List<(ColSection Section, int Vertex, int Point)> slots = [];
        List<int> indices = [];
        Dictionary<(ColSection, int), int> points = [];
        foreach (ColSector sector in Sectors)
        {
            AddSector(sector, slots, indices, points);
        }
        return new ColSurface(slots, indices, points.Count);
    }
    private static void AddSector(
        ColSector sector,
        List<(ColSection Section, int Vertex, int Point)> slots,
        List<int> indices,
        Dictionary<(ColSection, int), int> points)
    {
        ColSection start = sector.Start;
        ColSection end = sector.End;
        if (start.VertexCount < 2 || end.VertexCount < 2)
        {
            return;
        }
        int first = slots.Count;
        for (int i = 0; i < start.VertexCount; i++)
        {
            slots.Add((start, i, Point(points, start, i)));
        }
        int second = slots.Count;
        for (int i = 0; i < end.VertexCount; i++)
        {
            slots.Add((end, i, Point(points, end, i)));
        }
        int startHalf = (start.VertexCount - 1) / 2;
        int endHalf = (end.VertexCount - 1) / 2;
        int startMiddle = first + startHalf;
        int endMiddle = second + endHalf;
        int shared = Math.Min(startHalf, endHalf);
        for (int offset = -shared; offset < shared; offset++)
        {
            Triangle(indices, startMiddle + offset, endMiddle + offset, startMiddle + offset + 1);
            Triangle(indices, startMiddle + offset + 1, endMiddle + offset, endMiddle + offset + 1);
        }
        for (int offset = -startHalf; offset < -shared; offset++)
        {
            Triangle(indices, startMiddle + offset, endMiddle - shared, startMiddle + offset + 1);
        }
        for (int offset = shared; offset < startHalf; offset++)
        {
            Triangle(indices, startMiddle + offset, endMiddle + shared, startMiddle + offset + 1);
        }
        for (int offset = -endHalf; offset < -shared; offset++)
        {
            Triangle(indices, startMiddle - shared, endMiddle + offset, endMiddle + offset + 1);
        }
        for (int offset = shared; offset < endHalf; offset++)
        {
            Triangle(indices, startMiddle + shared, endMiddle + offset, endMiddle + offset + 1);
        }
    }
    private static int Point(Dictionary<(ColSection, int), int> points, ColSection section, int vertex)
    {
        if (!points.TryGetValue((section, vertex), out int found))
        {
            points[(section, vertex)] = found = points.Count;
        }
        return found;
    }
    private static void Triangle(List<int> indices, int a, int b, int c)
    {
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }
    private static void Add(
        List<float> positions, List<float> normals, List<byte> colors, ColSection section, int vertex)
    {
        positions.Add(section.X(vertex));
        positions.Add(section.Y(vertex));
        positions.Add(section.Z(vertex));
        normals.Add(section.Normals[vertex * 3] / 4096f);
        normals.Add(section.Normals[(vertex * 3) + 1] / 4096f);
        normals.Add(section.Normals[(vertex * 3) + 2] / 4096f);
        colors.Add(255);
        colors.Add(255);
        colors.Add(255);
        colors.Add(255);
    }
    private static void Write(byte[] output, int at, uint value, int recordLength)
    {
        if (at + 4 <= output.Length && recordLength >= RecordEndField + 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
        }
    }
    private static void WriteSection(byte[] output, int at, ColSection section)
    {
        int count = section.VertexCount;
        int positions = at + ColSection.HeaderSize;
        int normals = positions + (count * 12);
        int lengths = normals + (count * 12);
        int middle = (count - 1) / 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at), section.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 2), section.Flags2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 4), (uint)(positions + (middle * 12)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 8), (uint)(normals + (middle * 12)));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 12), (uint)(lengths + (middle * 4)));
        for (int i = 0; i < section.Positions.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(positions + (i * 4)), section.Positions[i]);
        }
        for (int i = 0; i < section.Normals.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(normals + (i * 4)), section.Normals[i]);
        }
        for (int i = 0; i < section.SegmentLengths.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(lengths + (i * 4)), section.SegmentLengths[i]);
        }
    }
    private static bool TryRead(ReadOnlySpan<byte> blob, out ColModel? model)
    {
        model = null;
        if (blob.Length < 8)
        {
            return false;
        }
        List<int> table = [];
        while (true)
        {
            int at = table.Count * 4;
            if (at + 4 > blob.Length)
            {
                return false;
            }
            uint value = BinaryRead.U32(blob, at);
            if (value == 0)
            {
                break;
            }
            if (value + RecordStartField + 8 > blob.Length || table.Count > 8192)
            {
                return false;
            }
            table.Add((int)value);
        }
        if (table.Count == 0)
        {
            return false;
        }
        Dictionary<int, ColSection> sections = [];
        List<ColSector> sectors = new(table.Count);
        int recordsStart = table[0];
        foreach (int record in table)
        {
            ColSection? start = ReadSection(blob, (int)BinaryRead.U32(blob, record + RecordStartField), sections);
            ColSection? end = ReadSection(blob, (int)BinaryRead.U32(blob, record + RecordEndField), sections);
            if (start is null || end is null)
            {
                return false;
            }
            int length = Math.Min(RecordSize, blob.Length - record);
            sectors.Add(new ColSector
            {
                Offset = record,
                Record = blob.Slice(record, length).ToArray(),
                Start = start,
                End = end,
            });
        }
        List<ColSection> ordered = [.. sections.Values.OrderBy(section => section.Offset)];
        int expected = (table.Count + 1) * 4;
        foreach (ColSection section in ordered)
        {
            if (section.Offset != expected)
            {
                return false;
            }
            expected += section.Size;
        }
        if (expected != recordsStart)
        {
            return false;
        }
        model = new ColModel(sectors, ordered);
        return true;
    }
    private static ColSection? ReadSection(
        ReadOnlySpan<byte> blob, int at, Dictionary<int, ColSection> sections)
    {
        if (sections.TryGetValue(at, out ColSection? found))
        {
            return found;
        }
        if (at <= 0 || at + ColSection.HeaderSize > blob.Length)
        {
            return null;
        }
        long middlePointer = BinaryRead.U32(blob, at + 4);
        long positions = at + ColSection.HeaderSize;
        if (middlePointer <= positions || (middlePointer - positions) % 12 != 0)
        {
            return null;
        }
        int middle = (int)((middlePointer - positions) / 12);
        int count = (2 * middle) + 1;
        if (BinaryPrimitives.ReadUInt16LittleEndian(blob[at..]) != (ushort)(0x8000 | middle))
        {
            return null;
        }
        long normals = positions + (12L * count);
        long lengths = normals + (12L * count);
        long end = lengths + (4L * (count - 1));
        if (end > blob.Length
            || BinaryRead.U32(blob, at + 8) != normals + (12L * middle)
            || BinaryRead.U32(blob, at + 12) != lengths + (4L * middle))
        {
            return null;
        }
        int[] readPositions = new int[count * 3];
        int[] readNormals = new int[count * 3];
        uint[] readLengths = new uint[count - 1];
        for (int i = 0; i < readPositions.Length; i++)
        {
            readPositions[i] = (int)BinaryRead.U32(blob, (int)positions + (i * 4));
            readNormals[i] = (int)BinaryRead.U32(blob, (int)normals + (i * 4));
        }
        for (int i = 0; i < readLengths.Length; i++)
        {
            readLengths[i] = BinaryRead.U32(blob, (int)lengths + (i * 4));
        }
        ColSection section = new()
        {
            Offset = at,
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(blob[at..]),
            Flags2 = BinaryPrimitives.ReadUInt16LittleEndian(blob[(at + 2)..]),
            Positions = readPositions,
            Normals = readNormals,
            SegmentLengths = readLengths,
        };
        sections[at] = section;
        return section;
    }
}
