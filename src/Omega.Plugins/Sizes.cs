namespace Omega.Plugins;
/// <summary>Renders byte counts the way the tree and the Properties pane show them.</summary>
public static class Sizes
{
    /// <summary>Renders a byte count as bytes, KB or MB, with one decimal above a kilobyte.</summary>
    /// <param name="bytes">The count to render.</param>
    /// <returns>A short human-readable string.</returns>
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
