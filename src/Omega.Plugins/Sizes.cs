namespace Omega.Plugins;
public static class Sizes
{
    public static string Describe(long bytes)
    {
        return bytes switch
        {
            >= 1 << 30 => $"{bytes / 1073741824.0:0.0} GB",
            >= 1 << 20 => $"{bytes / 1048576.0:0.0} MB",
            >= 1 << 10 => $"{bytes / 1024.0:0.0} KB",
            _ => $"{bytes} B",
        };
    }
}
