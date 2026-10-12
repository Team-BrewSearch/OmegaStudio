namespace Omega.Plugins;
public static class MemoryBudget
{
    public const long Total = 2L * 1024 * 1024 * 1024;
    public const long Undo = Total / 8;
    public const long PayloadCache = Total / 2;
    public const long Export = Total / 4;
    public const long DeferredWrites = Total / 8;
    public static bool Fits(long budget, long held, long want)
    {
        long room = budget - want;
        return held <= room;
    }
    public static string Describe(long bytes)
    {
        return Sizes.Describe(bytes);
    }
}
