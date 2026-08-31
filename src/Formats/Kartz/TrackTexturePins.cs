namespace Kartz.Formats;

/// <summary>
/// Texture slots confirmed by eye against the running game, for the few tracks whose table
/// cannot be derived from the shipped data.
///
/// <para>
/// Most of a level's table comes from the authored sequence in its <c>.rdf</c>
/// (<see cref="KartzResourceIndex"/>), and for most tracks that sequence is exactly as long as
/// the count of slots the track draws, which pins every slot on its own. The exceptions are
/// tracks that draw a lot of scenery billboards: those textures are referenced by no database
/// entry at all — FoosaRocks' seven (<c>FR_tree</c>, <c>FR_coco_tree</c>, the <c>*_spar</c>
/// plants) appear nowhere in <c>main.rdf</c> as archive references — so nothing records where
/// in the table they load, and the solver has to guess which slots they take. Every slot after
/// a wrong guess is displaced.
/// </para>
///
/// <para>
/// These entries are measurements, not derivations: each was read off a glTF export compared
/// against the game running the same track. They also record something no model here could have
/// produced — the runtime table is <b>not</b> a permutation of the level's textures. Two of
/// PlaneWreck's slots show textures that also sit at slots 1 and 2, and FoosaRocks binds three
/// of its own (<c>FR_fast_rock</c>, <c>FR_grass_tile</c>, <c>FR_fast_mud</c>) to a second slot,
/// so the loader evidently binds one texture into more than one slot.
/// <see cref="SskTextureSlotSolver.SolveOrdered"/> honours that by letting a pin be satisfied
/// directly, consuming nothing from either pool.
/// </para>
///
/// <para>
/// Slots are 1-based, matching a chunk header's <c>texture_index</c>. A glTF export names its
/// materials <c>kartz_tex_&lt;n&gt;</c> with <c>n</c> 0-based, so material <c>kartz_tex_4</c> is
/// slot 5, and its <c>Image_&lt;n&gt;</c> likewise indexes the table from 0.
/// </para>
/// </summary>
public static class TrackTexturePins
{
    /// <summary>
    /// Confirmed slots for one level, with a texture that identifies it.
    /// </summary>
    /// <param name="Witness">
    /// A texture the track must own for these pins to apply. Six track names — PlaneWreck,
    /// KingOfNewYork, SharkBeach and others — exist in both games with largely the same texture
    /// names, so the name alone does not identify a level.
    /// </param>
    /// <param name="Slots">1-based slot to archive file name.</param>
    public readonly record struct PinSet(string Witness, IReadOnlyDictionary<int, string> Slots);

    // FoosaRocks and its reverse share one authored sequence, identical entry for entry, and
    // their per-slot draw counts differ only where the mirrored geometry does (five slots, by a
    // handful of corners each). The same alignment therefore applies to both.
    // Sequence entries 1-20 land on slots 1-3, 8, 10-25, and three of them are bound to a
    // second slot as well: FR_fast_rock at 4, FR_grass_tile at 5, FR_fast_mud at 7. That leaves
    // only slots 6 and 9 to billboards, and both are slots the track never draws. Entry 1 falls
    // to slot 1 and entries 11-12 to slots 16-17 by elimination, so every drawn slot on this
    // track is now either pinned or forced.
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

    // The two ShreksSwamp variants were authored with genuinely different orders, not mirrored
    // ones - SS_Trunk_01 is entry 8 of the reverse and entry 18 of the forward - so they get
    // separate sets rather than sharing one the way FoosaRocks does.
    // SS_Treewall01_A is bound twice on both variants. Its sequence placement is recorded
    // alongside the second binding, because otherwise the search prefers to shift the whole run
    // one slot later - cheaper than a second binding - which displaces the slots either side.
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

            // PW_sky is Madagascar's; Super Star Kartz's PlaneWreck has PW_backgroundmontain
            // in its place and shares 33 texture names with it otherwise.
            ["PlaneWreck"] = new("PW_sky.5tx", PlaneWreck),

            ["ShreksSwamp"] = new("SS_swamp.5tx", ShreksSwamp),
            ["ShreksSwamp_Reverse"] = new("SS_swamp.5tx", ShreksSwampReverse),
        };

    /// <summary>
    /// Confirmed slots for a track directory name, or null when none were recorded — which is
    /// the normal case, and means the authored sequence alone decides.
    /// </summary>
    public static PinSet? For(string trackName)
    {
        ArgumentNullException.ThrowIfNull(trackName);
        return ByTrack.TryGetValue(trackName, out PinSet pins) ? pins : null;
    }
}
