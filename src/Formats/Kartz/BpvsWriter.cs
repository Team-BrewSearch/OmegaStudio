namespace Kartz.Formats;

public static class BpvsWriter
{
    public static byte[] BuildMinimalSingleSection()
    {
        return BuildAllVisible(sectors: 1, chunkCount: 1);
    }
    private static byte[] BuildAllVisible(int sectors, int chunkCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectors);
        ArgumentOutOfRangeException.ThrowIfNegative(chunkCount);
        List<bool[]> visibility = new(sectors);
        for (int i = 0; i < sectors; i++)
        {
            bool[] drawn = new bool[chunkCount];
            Array.Fill(drawn, true);
            visibility.Add(drawn);
        }
        return BpvsModel.Build(visibility);
    }
}
