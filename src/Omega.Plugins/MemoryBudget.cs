namespace Omega.Plugins;
public static class MemoryBudget
{
    public static readonly long Total = Measure(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.Is64BitProcess);
    public static readonly long Undo = Total / 8;
    public static readonly long PayloadCache = Total / 2;
    public static readonly long Export = Total / 4;
    public static readonly long DeferredWrites = Total / 8;
    [Obsolete("Compare held + want against the budget directly. This will be removed at the next contract version.")]
    public static bool Fits(long budget, long held, long want)
    {
        long room = budget - want;
        return held <= room;
    }
    [Obsolete("Use Sizes.Describe. This will be removed at the next contract version.")]
    public static string Describe(long bytes)
    {
        return Sizes.Describe(bytes);
    }
    private static long Measure(long available, bool is64Bit)
    {
        long ceiling = is64Bit ? 4L << 30 : 1L << 30;
        return available > 0 ? Math.Clamp(available / 4, 512L << 20, ceiling) : Math.Min(2L << 30, ceiling);
    }
}
