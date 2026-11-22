using System.Buffers.Binary;
using System.Globalization;
using Omega.Plugins;
namespace Disgaea.Formats;
internal enum FieldKind
{
    U8,
    S8,
    U16,
    S16,
    Height,
    Panel,
    Float,
}
internal sealed record MapField(string Name, int Offset, FieldKind Kind, IReadOnlyList<string>? Choices = null)
{
    public int Get(ReadOnlySpan<byte> record)
    {
        return Kind switch
        {
            FieldKind.U8 or FieldKind.Panel => record[Offset],
            FieldKind.S8 => (sbyte)record[Offset],
            FieldKind.Height => -(sbyte)record[Offset],
            FieldKind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(record[Offset..]),
            FieldKind.S16 => BinaryPrimitives.ReadInt16LittleEndian(record[Offset..]),
            _ => 0,
        };
    }
    public string Text(ReadOnlySpan<byte> record)
    {
        if (Kind == FieldKind.Float)
        {
            return BinaryPrimitives.ReadSingleLittleEndian(record[Offset..]).ToString("R", CultureInfo.InvariantCulture);
        }
        if (Kind == FieldKind.Panel)
        {
            return record[Offset] != 0 ? bool.TrueString : bool.FalseString;
        }
        int value = Get(record);
        return Choices is not null && value >= 0 && value < Choices.Count ? Choices[value] : value.ToString(CultureInfo.InvariantCulture);
    }
    public void Set(Span<byte> record, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (Kind == FieldKind.Float)
        {
            BinaryPrimitives.WriteSingleLittleEndian(record[Offset..], float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            return;
        }
        if (Kind == FieldKind.Panel)
        {
            record[Offset] = bool.Parse(text) ? (byte)100 : (byte)0;
            return;
        }
        int choice = Choices is null ? -1 : Choices.ToList().IndexOf(text);
        int value = choice >= 0 ? choice : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        (int min, int max) = Kind switch
        {
            FieldKind.U8 => (0, 255),
            FieldKind.S8 => (-128, 127),
            FieldKind.Height => (-127, 128),
            FieldKind.U16 => (0, 65535),
            _ => (-32768, 32767),
        };
        if (value < min || value > max)
        {
            throw new InvalidDataException($"{Name} takes {min} to {max}, not {value}");
        }
        switch (Kind)
        {
            case FieldKind.U8:
            case FieldKind.S8:
                record[Offset] = (byte)value;
                break;
            case FieldKind.Height:
                record[Offset] = (byte)(sbyte)-value;
                break;
            case FieldKind.U16:
            case FieldKind.S16:
                BinaryPrimitives.WriteUInt16LittleEndian(record[Offset..], (ushort)value);
                break;
        }
    }
    public PropertyRow Row(ReadOnlySpan<byte> record, Action<string> apply)
    {
        string value = Text(record);
        AssetChange changed = AssetChange.Properties | AssetChange.Dirty | AssetChange.Scene | AssetChange.Bytes;
        if (Kind == FieldKind.Panel)
        {
            return PropertyRow.Switch(Name, record[Offset] != 0, on => apply(on.ToString())) with { Changed = changed };
        }
        return Choices is not null
            ? PropertyRow.Options(Name, value, Choices, apply) with { Changed = changed }
            : PropertyRow.Editable(Name, value, apply) with { Changed = changed };
    }
}
internal static class MapFields
{
    public static readonly string[] Faces = ["-Z side", "+X side", "+Z side", "-X side", "Top", "-Z decal", "+X decal", "+Z decal", "-X decal", "Top decal", "Shadow", "Slot 11"];
    public static readonly string[] Mobility = ["Walkable", "Fly only", "Impassable"];
    public static readonly string[] Alliance = ["Player", "Enemy", "Third party", "Passive", "Neutral"];
    public static readonly string[] Ai = ["Disabled", "Guard", "Attack"];
    public static readonly string[] GeoColours = ["Colour 0", "Colour 1", "Colour 2", "Colour 3", "Colour 4", "Colour 5"];
    public static readonly MapField[] Tile =
    [
        new("X", 113, FieldKind.S8),
        new("Z", 114, FieldKind.S8),
        new("Height corner X0 Z0", 96, FieldKind.Height),
        new("Height corner X1 Z0", 97, FieldKind.Height),
        new("Height corner X0 Z1", 98, FieldKind.Height),
        new("Height corner X1 Z1", 99, FieldKind.Height),
        new("Mobility", 119, FieldKind.U8, Mobility),
        new("Geo panel", 121, FieldKind.Panel),
        new("Geo panel colour", 120, FieldKind.U8, GeoColours),
        new("Geo effect active (game recomputes)", 123, FieldKind.U8),
        new("Enabled", 112, FieldKind.U8),
        .. Bottoms(),
        .. Enumerable.Range(0, 9).SelectMany(Face),
    ];
    public static readonly MapField[] Actor =
    [
        new("Class", 0, FieldKind.S16),
        new("Level", 2, FieldKind.S16),
        new("Side", 4, FieldKind.S8, Alliance),
        new("X", 5, FieldKind.S8),
        new("Z", 6, FieldKind.S8),
        new("Facing (x 90 degrees)", 7, FieldKind.S8),
        new("Appearance condition (-1 = always)", 8, FieldKind.S8),
        new("AI", 9, FieldKind.S8, Ai),
        new("Stat multiplier (x value + 1)", 10, FieldKind.U8),
        new("Unused 0x0B", 11, FieldKind.U8),
        new("Item 1", 12, FieldKind.S16),
        new("Item 2", 14, FieldKind.S16),
        new("Item 3", 16, FieldKind.S16),
        new("Item 4", 18, FieldKind.S16),
        new("Spawn rule (0 = always)", 20, FieldKind.S16),
        new("Geo effect (tens effect, units colour + 1)", 22, FieldKind.S16),
        new("Weapon mastery (all 7 types)", 24, FieldKind.S16),
        new("Spell 1", 26, FieldKind.U16),
        new("Spell 2", 28, FieldKind.U16),
        new("Spell 3", 30, FieldKind.U16),
        new("Spell 4", 32, FieldKind.U16),
        new("Spell level 1", 34, FieldKind.U8),
        new("Spell level 2", 35, FieldKind.U8),
        new("Spell level 3", 36, FieldKind.U8),
        new("Spell level 4", 37, FieldKind.U8),
        new("AI override (non-zero = AI mode 3)", 38, FieldKind.U8),
    ];
    public static readonly MapField[] Event =
    [
        new("X", 0, FieldKind.S8),
        new("Z", 1, FieldKind.S8),
        new("Event", 2, FieldKind.U16),
    ];
    public static readonly MapField[] Object =
    [
        new("X", 0, FieldKind.S16),
        new("Y", 2, FieldKind.S16),
        new("Z", 4, FieldKind.S16),
        new("Rotation X", 6, FieldKind.S16),
        new("Rotation Y", 8, FieldKind.S16),
        new("Rotation Z", 10, FieldKind.S16),
        new("Scale X %", 12, FieldKind.S16),
        new("Scale Y %", 14, FieldKind.S16),
        new("Scale Z %", 16, FieldKind.S16),
        new("Start frame", 18, FieldKind.S16),
        new("Byte 0x14", 20, FieldKind.U8),
        new("Model", 21, FieldKind.S8),
        new("Visible", 22, FieldKind.U8),
        new("Byte 0x17", 23, FieldKind.S8),
        new("Scroll U", 24, FieldKind.S8),
        new("Scroll V", 25, FieldKind.S8),
    ];
    public static readonly MapField[] Chunk =
    [
        new("Offset X", 0, FieldKind.Float),
        new("Offset Z", 8, FieldKind.Float),
        new("Id", 48, FieldKind.U16),
    ];
    private static IEnumerable<MapField> Bottoms()
    {
        for (int side = 0; side < 4; side++)
        {
            yield return new MapField($"{Faces[side]} bottom A", 100 + (side * 2), FieldKind.Height);
            yield return new MapField($"{Faces[side]} bottom B", 101 + (side * 2), FieldKind.Height);
        }
    }
    private static IEnumerable<MapField> Face(int slot)
    {
        string face = Faces[slot];
        int at = slot * 8;
        return
        [
            new($"{face} texture", at + 6, FieldKind.U8),
            new($"{face} U", at, FieldKind.U8),
            new($"{face} V", at + 1, FieldKind.U8),
            new($"{face} V right", at + 2, FieldKind.U8),
            new($"{face} width", at + 3, FieldKind.U8),
            new($"{face} height", at + 4, FieldKind.U8),
            new($"{face} height right", at + 5, FieldKind.U8),
            new($"{face} transform", at + 7, FieldKind.U8),
        ];
    }
}
