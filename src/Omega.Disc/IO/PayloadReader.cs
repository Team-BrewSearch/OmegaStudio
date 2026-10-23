namespace Omega.Disc.IO;
public static class PayloadReader
{
    public static byte[] ReadAll(Stream stream, string what)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length > Array.MaxLength)
        {
            throw new InvalidDataException(
                $"'{what}' holds {stream.Length} bytes, more than this host can read into one node");
        }
        byte[] buffer = new byte[stream.Length];
        stream.Position = 0;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
