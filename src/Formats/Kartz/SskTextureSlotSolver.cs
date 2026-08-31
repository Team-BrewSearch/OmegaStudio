using SMath = System.Math;
namespace Kartz.Formats;

public static class SskTextureSlotSolver
{
    private const double RatioFloor = 0.55;
    private const double UnderusePenalty = 1.0;
    private const double UnderuseSlope = 3.0;
    private const double BadDimensionPenalty = 5.0;
    private const double TightFitWeight = 0.15;
    private const double FillerOnDrawnSlot = 2.5;
    private const double UpperOnUnusedSlot = 2.5;
    private const double EarlyBias = 0.001;
    private const byte StepNone = 0;
    private const byte StepUpper = 1;
    private const byte StepFiller = 2;
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
    private const double DrawnSlotWithFiller = 1e6;
    private const double PinBoundTwice = 1e7;
    private const double DrawnSlotLeftEmpty = 1e9;
    private const byte StepRun = 1;
    private const byte StepFill = 2;
    private const byte StepPin = 3;
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
