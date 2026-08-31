namespace Kartz.Formats;

public static class TrackTexturePins
{
    public readonly record struct PinSet(string Witness, IReadOnlyDictionary<int, string> Slots);
    private static readonly Dictionary<int, string> FoosaRocks = new()
    {
        [2] = "FR_roadstart.5tx",
        [3] = "FR_fast_rock.5tx",
        [4] = "FR_fast_rock.5tx",
        [5] = "FR_grass_tile.5tx",
        [7] = "FR_fast_mud.5tx",
        [8] = "FR_grass_tile.5tx",
        [10] = "FR_blend05.5tx",
        [11] = "FR_hill.5tx",
        [12] = "FR_treewall_mid.5tx",
        [13] = "FR_rock01.5tx",
        [14] = "FR_alex.5tx",
        [15] = "FR_grass01.5tx",
        [18] = "FR_blend01.5tx",
        [19] = "FR_blend03.5tx",
        [20] = "FR_hill01.5tx",
        [21] = "FR_arrows01.5tx",
        [22] = "FR_grasswall.5tx",
        [23] = "FR_treewall_back.5tx",
        [24] = "FR_stock.5tx",
        [25] = "FR_Start.5tx",
    };
    private static readonly Dictionary<int, string> PlaneWreck = new()
    {
        [26] = "PW_DirtRoad01.5tx",
        [27] = "PW_DirtRoad_sand01.5tx",
    };
    private static readonly Dictionary<int, string> ShreksSwamp = new()
    {
        [26] = "SS_Treewall01_A.5tx",
        [27] = "SS_Treewall01_A.5tx",
    };
    private static readonly Dictionary<int, string> ShreksSwampReverse = new()
    {
        [22] = "SS_RockRoadSide_02.5tx",
        [23] = "SS_Treewall01_A.5tx",
        [24] = "SS_Treewall01_A.5tx",
    };
    private static readonly Dictionary<string, PinSet> ByTrack =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FoosaRocks"] = new("FR_fast_mud.5tx", FoosaRocks),
            ["FoosaRocks_Reverse"] = new("FR_fast_mud.5tx", FoosaRocks),
            ["PlaneWreck"] = new("PW_sky.5tx", PlaneWreck),
            ["ShreksSwamp"] = new("SS_swamp.5tx", ShreksSwamp),
            ["ShreksSwamp_Reverse"] = new("SS_swamp.5tx", ShreksSwampReverse),
        };
    public static PinSet? For(string trackName)
    {
        ArgumentNullException.ThrowIfNull(trackName);
        return ByTrack.TryGetValue(trackName, out PinSet pins) ? pins : null;
    }
}
