using System.Text;
using Omega.Plugins;
namespace PlayStation.Formats;
public sealed class Ps3DiscArchiveFormat : IArchiveFormat
{
    public const int SignatureSector = 1;
    public const int SignatureLength = 12;
    public const int DescriptorSector = 16;
    public string Id => "playstation.ps3";
    public string DisplayName => "PlayStation 3 game disc (.iso)";
    public FileFilter Filter { get; } = new("PlayStation 3 game disc", "*.iso", "*.dec.iso");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        if (head.Length < (SignatureSector * 0x800) + SignatureLength)
        {
            return false;
        }
        if (!head.Slice(SignatureSector * 0x800, SignatureLength)
            .SequenceEqual(Encoding.ASCII.GetBytes("PlayStation3")))
        {
            return false;
        }
        return head.Length >= (DescriptorSector * 0x800) + 6
            ? head.Slice(DescriptorSector * 0x800 + 1, 5).SequenceEqual(Encoding.ASCII.GetBytes("CD001"))
            : HasDescriptor(path);
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Ps3DiscArchive archive = Ps3DiscArchive.Open(path, host);
        host.Log($"{archive.DisplayName}: PlayStation 3 disc, {archive.Description}.");
        return archive;
    }
    private static bool HasDescriptor(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] head = new byte[(DescriptorSector * 0x800) + 8];
            stream.ReadExactly(head);
            return head.AsSpan(DescriptorSector * 0x800 + 1, 5).SequenceEqual(Encoding.ASCII.GetBytes("CD001"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
