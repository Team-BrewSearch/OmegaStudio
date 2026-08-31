namespace Omega.Plugins;

/// <summary>
/// Byte counts as the UI shows them. Here rather than in each plugin so that every
/// size in the window reads the same way, whichever format produced it.
/// </summary>
public static class Sizes
{
    /// <summary>Formats a byte count, e.g. <c>1.4 MB</c>, <c>912.0 KB</c>, <c>48 B</c>.</summary>
    public static string Describe(long bytes)
    {
        return bytes switch
        {
            >= 1 << 20 => $"{bytes / 1048576.0:0.0} MB",
            >= 1 << 10 => $"{bytes / 1024.0:0.0} KB",
            _ => $"{bytes} B",
        };
    }
}
