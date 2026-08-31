using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// Reconstructs the 1-based texture table a TRK's chunk headers index into, from the geometry
/// alone.
///
/// <para>
/// This is the fallback. The table's real order is authored, and <see cref="KartzResourceIndex"/>
/// reads it out of the <c>.rdf</c> databases that ship beside the archive; <see
/// cref="TrackTextureTable"/> prefers that whenever they are present. What is left for this class
/// is the case where they are not — an archive triple copied out on its own — and then the only
/// thing left to go on is how the geometry uses each slot.
/// </para>
///
/// <para>
/// The guess it makes: a track's textures live in two sibling archive directories —
/// <c>Textures/</c> and <c>textures/</c> — and the table is the uppercase group in GDF order with
/// the lowercase group filling the slots left over. That is a decent approximation of the authored
/// order but not the same thing, so results differ from the real table on tracks where the two
/// disagree.
/// </para>
///
/// <para>
/// The oracle is simple: a texture is authored to be used, so geometry that covers less than
/// ~55% of a candidate's width or height means the candidate is wrong. Two structural priors
/// go with it — an uppercase entry should not land on a slot nothing draws, and a lowercase
/// entry should not displace one that does. Tests for "the UV extent is a whole multiple of
/// the texture size" were tried and dropped: they fire on plainly-correct slots and are noise.
/// </para>
///
/// <para>
/// Textures of identical dimensions produce identical measurements, so the solver cannot tell
/// them apart; where a slot has several same-size candidates the choice among them is arbitrary.
/// Measured against the authored order, it agrees on 513 of Super Star Kartz's 549 drawn slots,
/// and every one of the 36 differences is a same-size choice like that.
/// </para>
/// </summary>
public static class SskTextureSlotSolver
{
    // Under-use test — the one measurement that survived calibration against known-good tracks.
    private const double RatioFloor = 0.55;
    private const double UnderusePenalty = 1.0;
    private const double UnderuseSlope = 3.0;
    private const double BadDimensionPenalty = 5.0;

    // Weak pull toward the texture the geometry uses most nearly 1:1, in octaves off. Heavy
    // tiling is real (road strips repeat along travel), so this only settles otherwise-open
    // choices; above ~0.2 it starts overriding correct arrangements.
    private const double TightFitWeight = 0.15;

    // Structural priors. The packer emits into Textures/ exactly what the track draws, so an
    // uppercase entry on a slot nothing references means the alignment slipped; the mirror
    // holds for lowercase entries taking a slot that does draw. Without both, the search
    // scatters lowercase entries through the referenced range to dodge its own fit penalties.
    private const double FillerOnDrawnSlot = 2.5;
    private const double UpperOnUnusedSlot = 2.5;

    // Tie-break. Equal-cost arrangements are common and an arbitrary pick shifts every
    // texture after it, so settle ties toward the earliest position — what the confirmed
    // FarFarAway layout does. Small enough never to overturn a structural decision.
    private const double EarlyBias = 0.001;

    private const byte StepNone = 0;
    private const byte StepUpper = 1;
    private const byte StepFiller = 2;

    /// <summary>
    /// Returns the archive entries in texture-slot order: slot <c>i</c> of the result is the
    /// texture a chunk header's <c>texture_index</c> of <c>i + 1</c> refers to.
    /// </summary>
    /// <param name="upper">The <c>Textures/</c> group, in GDF order.</param>
    /// <param name="lower">The <c>textures/</c> group, in GDF order.</param>
    /// <param name="usage">Per-slot UV extents from <see cref="SskTrack.MeasureSlotUsage"/>.</param>
    public static List<DwkEntry> Solve(
        IReadOnlyList<DwkEntry> upper,
        IReadOnlyList<DwkEntry> lower,
        IReadOnlyList<SskTrack.SlotUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(upper);
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(usage);

        int nu = upper.Count;
        int nl = lower.Count;
        int n = nu + nl;

        List<DwkEntry> fallback = [.. upper, .. lower];
        if (nl == 0 || n == 0)
        {
            return fallback;
        }

        (int W, int H)[] upperDims = ReadDimensions(upper);
        (int W, int H)[] lowerDims = ReadDimensions(lower);

        // dp[slot][uppercase placed][next selectable lowercase index].
        //
        // A lowercase entry taking a slot the track draws must come after the previous such
        // entry in GDF order — every confirmed arrangement obeys this (FarFarAway draws
        // lower[0], lower[4] and lower[8], in that order). Entries on slots nothing draws are
        // unobservable, so they do not move the pointer and are bound afterwards.
        int strideK = nl + 1;
        int strideJ = (nu + 1) * strideK;
        int cells = (n + 1) * strideJ;

        double[] cost = new double[cells];
        byte[] step = new byte[cells];
        int[] cameFromK = new int[cells];
        int[] tookLower = new int[cells];

        Array.Fill(cost, double.PositiveInfinity);
        Array.Fill(tookLower, -1);
        cost[0] = 0.0;

        for (int s = 1; s <= n; s++)
        {
            int slot = s - 1;
            bool drawn = IsDrawn(usage, slot);
            int maxJ = SMath.Min(s, nu);

            for (int j = 0; j <= maxJ; j++)
            {
                int fillers = s - j;
                if (fillers < 0 || fillers > nl)
                {
                    continue;
                }

                for (int k = 0; k <= nl; k++)
                {
                    int here = (s * strideJ) + (j * strideK) + k;

                    // Place upper[j - 1] here; the lowercase pointer stays put.
                    if (j > 0)
                    {
                        double prev = cost[((s - 1) * strideJ) + ((j - 1) * strideK) + k];
                        if (!double.IsPositiveInfinity(prev))
                        {
                            double c = prev + SlotCost(usage, slot, upperDims[j - 1]);
                            if (!drawn)
                            {
                                c += UpperOnUnusedSlot;
                            }

                            if (c < cost[here])
                            {
                                cost[here] = c;
                                step[here] = StepUpper;
                                cameFromK[here] = k;
                                tookLower[here] = -1;
                            }
                        }
                    }

                    if (fillers < 1)
                    {
                        continue;
                    }

                    double basis = cost[((s - 1) * strideJ) + (j * strideK) + k];
                    if (double.IsPositiveInfinity(basis))
                    {
                        continue;
                    }

                    basis += EarlyBias * slot;

                    if (!drawn)
                    {
                        // Nothing renders here, so which lowercase entry lands is unobservable.
                        if (basis < cost[here])
                        {
                            cost[here] = basis;
                            step[here] = StepFiller;
                            cameFromK[here] = k;
                            tookLower[here] = -1;
                        }

                        continue;
                    }

                    for (int kp = k; kp < nl; kp++)
                    {
                        int target = (s * strideJ) + (j * strideK) + kp + 1;
                        double c = basis + SlotCost(usage, slot, lowerDims[kp]) + FillerOnDrawnSlot;
                        if (c < cost[target])
                        {
                            cost[target] = c;
                            step[target] = StepFiller;
                            cameFromK[target] = k;
                            tookLower[target] = kp;
                        }
                    }
                }
            }
        }

        int bestK = -1;
        double best = double.PositiveInfinity;
        for (int k = 0; k <= nl; k++)
        {
            double v = cost[(n * strideJ) + (nu * strideK) + k];
            if (v < best)
            {
                best = v;
                bestK = k;
            }
        }

        if (bestK < 0 || double.IsPositiveInfinity(best))
        {
            return fallback;
        }

        byte[] kinds = new byte[n];
        int[] picks = new int[n];
        int cs = n;
        int cj = nu;
        int ck = bestK;

        while (cs > 0)
        {
            int here = (cs * strideJ) + (cj * strideK) + ck;
            byte kind = step[here];
            if (kind == StepNone)
            {
                return fallback;
            }

            kinds[cs - 1] = kind;
            picks[cs - 1] = tookLower[here];
            if (kind == StepUpper)
            {
                cj--;
            }

            ck = cameFromK[here];
            cs--;
        }

        return Bind(upper, lower, kinds, picks) ?? fallback;
    }

    // Structural terms for SolveOrdered, ranked so a fit measurement can never buy its way past
    // one. Scaled rather than compared lexicographically so the whole thing stays one DP: a fit
    // total cannot reach 1e6 (the worst per-slot fit is single digits, over at most a few
    // hundred slots), and a drawn slot left with no texture at all outranks every other term.
    private const double DrawnSlotWithFiller = 1e6;

    // Binding one texture into two slots is real - the shipped tables do it - but it is the
    // unusual case, so a pin is satisfied from the sequence wherever the alignment allows and
    // only bound a second time when nothing else can put it there.
    private const double PinBoundTwice = 1e7;
    private const double DrawnSlotLeftEmpty = 1e9;

    private const byte StepRun = 1;
    private const byte StepFill = 2;
    private const byte StepPin = 3;

    /// <summary>
    /// Returns the slot table for a track whose authored texture order is known, or null when
    /// the run cannot span the table.
    ///
    /// <para>
    /// The run fixes the order but not the positions: a level also loads textures for its
    /// scenery, and those take slots interleaved with the run's. Which slots they take is the
    /// only unknown, and it is bounded - the run has to end up spanning the table, so the number
    /// of slots it skips is fixed at <c>slots - run</c>. Placing them is what this searches,
    /// preferring skips onto slots the track never draws, then onto whichever drawn slots the
    /// usage measurements fit worst, and only then settling ties on fit.
    /// </para>
    ///
    /// <para>
    /// Where a track has exactly as many drawn slots as run entries there is no freedom left and
    /// the search is a formality; that is the case for 7 of Madagascar Kartz twelve tracks and
    /// 14 of Super Star Kartz sixteen, all of which render correctly. The rest is what this is
    /// for, and where <paramref name="pins"/> exist they cut it down further.
    /// </para>
    /// </summary>
    /// <param name="ordered">The authored run, in order.</param>
    /// <param name="filler">Everything else the track owns, in GDF order.</param>
    /// <param name="usage">Per-slot UV extents, one per slot in the table.</param>
    /// <param name="pins">
    /// Slots (0-based) confirmed against the running game - see <see cref="TrackTexturePins"/>.
    /// A pinned slot accepts only its own texture. When neither pool can supply it there (the
    /// texture is already placed elsewhere, which the shipped tables do contain) the pin is
    /// placed directly, consuming nothing, so the rest of the alignment is unaffected.
    /// </param>
    /// <returns>
    /// Slot <c>i</c> of the result is the texture a chunk header <c>texture_index</c> of
    /// <c>i + 1</c> refers to; null entries are slots no texture reaches.
    /// </returns>
    public static List<DwkEntry?>? SolveOrdered(
        IReadOnlyList<DwkEntry> ordered,
        IReadOnlyList<DwkEntry> filler,
        IReadOnlyList<SskTrack.SlotUsage> usage,
        IReadOnlyDictionary<int, DwkEntry>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(filler);
        ArgumentNullException.ThrowIfNull(usage);

        int slots = usage.Count;
        int run = ordered.Count;
        if (run == 0 || run > slots)
        {
            return null;
        }

        (int W, int H)[] orderedDims = ReadDimensions(ordered);
        (int W, int H)[] fillerDims = ReadDimensions(filler);

        // dp[slot, placed, direct] - cost of filling slots [0, slot) having placed `placed` run
        // entries and satisfied `direct` pins from neither pool. Fillers are consumed in order,
        // so their count is always slot - placed - direct and needs no state of its own.
        int maxDirect = pins is null ? 0 : SMath.Min(pins.Count, slots);
        int strideDirect = maxDirect + 1;
        int stridePlaced = (run + 1) * strideDirect;

        double[] cost = new double[(slots + 1) * stridePlaced];
        byte[] step = new byte[cost.Length];
        Array.Fill(cost, double.PositiveInfinity);
        cost[0] = 0.0;

        for (int slot = 0; slot < slots; slot++)
        {
            bool drawn = IsDrawn(usage, slot);
            DwkEntry? pin = null;
            _ = (pins?.TryGetValue(slot, out pin));
            (int W, int H) pinDims = pin is null ? (0, 0) : ReadDimensions([pin])[0];

            for (int placed = 0; placed <= SMath.Min(slot, run); placed++)
            {
                for (int direct = 0; direct <= maxDirect; direct++)
                {
                    int from = (slot * stridePlaced) + (placed * strideDirect) + direct;
                    double here = cost[from];
                    if (double.IsPositiveInfinity(here))
                    {
                        continue;
                    }

                    int next = ((slot + 1) * stridePlaced) + (placed * strideDirect) + direct;

                    if (placed < run && (pin is null || ReferenceEquals(pin, ordered[placed])))
                    {
                        Relax(cost, step, next + strideDirect,
                            here + SlotCost(usage, slot, orderedDims[placed]), StepRun);
                    }

                    int spare = slot - placed - direct;
                    if (spare < filler.Count)
                    {
                        if (pin is null || ReferenceEquals(pin, filler[spare]))
                        {
                            Relax(cost, step, next,
                                here + SlotCost(usage, slot, fillerDims[spare])
                                    + (drawn ? DrawnSlotWithFiller : 0.0),
                                StepFill);
                        }
                    }
                    else if (pin is null)
                    {
                        Relax(cost, step, next, here + (drawn ? DrawnSlotLeftEmpty : 0.0), StepFill);
                    }

                    if (pin is not null && direct < maxDirect)
                    {
                        Relax(cost, step, next + 1,
                            here + SlotCost(usage, slot, pinDims) + PinBoundTwice, StepPin);
                    }
                }
            }
        }

        int bestDirect = -1;
        double best = double.PositiveInfinity;
        for (int direct = 0; direct <= maxDirect; direct++)
        {
            double v = cost[(slots * stridePlaced) + (run * strideDirect) + direct];
            if (v < best)
            {
                best = v;
                bestDirect = direct;
            }
        }

        if (bestDirect < 0 || double.IsPositiveInfinity(best))
        {
            return null;
        }

        DwkEntry?[] table = new DwkEntry?[slots];
        int atPlaced = run;
        int atDirect = bestDirect;
        for (int slot = slots - 1; slot >= 0; slot--)
        {
            int at = ((slot + 1) * stridePlaced) + (atPlaced * strideDirect) + atDirect;
            switch (step[at])
            {
                case StepRun:
                    table[slot] = ordered[--atPlaced];
                    break;

                case StepPin:
                    atDirect--;
                    table[slot] = pins?[slot];
                    break;

                default:
                    int spare = slot - atPlaced - atDirect;
                    table[slot] = spare < filler.Count ? filler[spare] : null;
                    break;
            }
        }

        return [.. table];
    }

    private static void Relax(double[] cost, byte[] step, int target, double candidate, byte kind)
    {
        if (candidate < cost[target])
        {
            cost[target] = candidate;
            step[target] = kind;
        }
    }

    private static List<DwkEntry>? Bind(
        IReadOnlyList<DwkEntry> upper, IReadOnlyList<DwkEntry> lower, byte[] kinds, int[] picks)
    {
        bool[] taken = new bool[lower.Count];
        foreach (int pick in picks)
        {
            if (pick >= 0)
            {
                taken[pick] = true;
            }
        }

        List<DwkEntry> spare = [];
        for (int i = 0; i < lower.Count; i++)
        {
            if (!taken[i])
            {
                spare.Add(lower[i]);
            }
        }

        List<DwkEntry> order = new(kinds.Length);
        int ui = 0;
        int si = 0;

        for (int i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] == StepUpper)
            {
                if (ui >= upper.Count)
                {
                    return null;
                }

                order.Add(upper[ui++]);
            }
            else if (picks[i] >= 0)
            {
                order.Add(lower[picks[i]]);
            }
            else
            {
                if (si >= spare.Count)
                {
                    return null;
                }

                order.Add(spare[si++]);
            }
        }

        return order;
    }

    private static (int W, int H)[] ReadDimensions(IReadOnlyList<DwkEntry> entries)
    {
        (int W, int H)[] dims = new (int, int)[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            dims[i] = SskTexture.TryReadDimensions(entries[i].Span, out int w, out int h)
                ? (w, h)
                : (0, 0);
        }

        return dims;
    }

    private static bool IsDrawn(IReadOnlyList<SskTrack.SlotUsage> usage, int slot)
    {
        return slot >= 0 && slot < usage.Count && usage[slot].Samples > 0;
    }

    private static double SlotCost(IReadOnlyList<SskTrack.SlotUsage> usage, int slot, (int W, int H) dims)
    {
        if (!IsDrawn(usage, slot))
        {
            return 0.0;
        }

        SskTrack.SlotUsage u = usage[slot];
        double fit = AxisPenalty(u.MaxU, dims.W)
            + AxisPenalty(u.MaxV, dims.H)
            + TightFitPenalty(u, dims);

        // Weight by how much geometry draws the slot, so a busy surface outvotes a stray quad.
        return SMath.Log(1.0 + u.Samples) * fit;
    }

    private static double AxisPenalty(float measured, int dimension)
    {
        if (dimension <= 0)
        {
            return BadDimensionPenalty;
        }

        double ratio = measured / (double)dimension;
        return ratio < RatioFloor
            ? UnderusePenalty + (UnderuseSlope * (RatioFloor - ratio) / RatioFloor)
            : 0.0;
    }

    private static double TightFitPenalty(SskTrack.SlotUsage u, (int W, int H) dims)
    {
        if (dims.W <= 0 || dims.H <= 0 || u.MaxU <= 0f || u.MaxV <= 0f)
        {
            return 0.0;
        }

        double octavesU = SMath.Abs(SMath.Log2(u.MaxU / (double)dims.W));
        double octavesV = SMath.Abs(SMath.Log2(u.MaxV / (double)dims.H));
        return TightFitWeight * (octavesU + octavesV);
    }
}
