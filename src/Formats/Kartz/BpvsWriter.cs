namespace Kartz.Formats;

public static class BpvsWriter
{
    public static byte[] BuildMinimalSingleSection()
    {
        const int numSections = 1;
        const int tableStart = 8;
        int tableEnd = tableStart + (numSections * 4);
        int section0Offset = tableEnd + 4;
        byte[] blob = new byte[section0Offset];
        WriteU32(blob, 0x00, numSections);
        WriteU32(blob, 0x04, tableStart);
        WriteU32(blob, tableStart, (uint)section0Offset);
        return blob;
    }
    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)(v >> 24);
    }
}
