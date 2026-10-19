namespace DWSSK.Formats.Core;
internal enum Platform
{
    PS3,
    Xbox360,
    Wii,
    N3ds,
}
internal static class Platforms
{
    public static string Name(this Platform p)
    {
        return p switch
        {
            Platform.PS3 => "PS3",
            Platform.Xbox360 => "XBOX360",
            Platform.Wii => "Wii",
            _ => "3DS",
        };
    }
    public static string Label(this Platform p)
    {
        return p switch
        {
            Platform.PS3 => "PlayStation 3",
            Platform.Xbox360 => "Xbox 360",
            Platform.Wii => "Wii",
            _ => "Nintendo 3DS",
        };
    }
    public static Platform Parse(string name)
    {
        return name switch
        {
            "PS3" => Platform.PS3,
            "XBOX360" => Platform.Xbox360,
            "Wii" => Platform.Wii,
            "3DS" => Platform.N3ds,
            _ => throw new InvalidDataException($"unknown platform \"{name}\""),
        };
    }
    public static bool BigEndian(this Platform p)
    {
        return p != Platform.N3ds;
    }
    public static IReadOnlyList<string> Names { get; } = ["PS3", "XBOX360", "Wii", "3DS"];
}
