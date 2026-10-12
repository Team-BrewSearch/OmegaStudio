using System.Text;
using DiscUtils.Iso9660;
using Omega.Disc.Formats.PlayStation;
using Omega.Disc.Streams;
using Omega.Plugins;
namespace PlayStation.Formats;
public sealed class Ps3DiscArchive : IArchive
{
    private readonly FileStream _stream;
    private Ps3DiscArchive(string path, IHostServices host, FileStream stream, CDReader reader, string description)
    {
        _stream = stream;
        Host = host;
        Reader = reader;
        Description = description;
        DisplayName = Path.GetFileName(path);
        Root = new Nodes.Ps3RootNode(this, DisplayName);
    }
    public CDReader Reader { get; }
    public string Description { get; }
    public IHostServices Host { get; }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => false;
    public IReadOnlyList<string> BackupPaths => [];
    public void Save() => throw new InvalidOperationException(
        $"{DisplayName} is open for reading, so there is nothing to write back to it.");
    public static Ps3DiscArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        byte[] head = new byte[Ps3DiscArchiveFormat.DescriptorSector * 0x800 + 0x40];
        Ps3KeyResult? key;
        using (FileStream probe = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            probe.ReadExactly(head);
            key = Ps3KeyResolver.Resolve(head, path);
        }
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            Stream readable = key is { Key.Length: 16, Source: Ps3KeySource.Embedded3K3Y or Ps3KeySource.Sidecar }
                ? new Ps3CipherStream(stream, 0, stream.Length, key.Key)
                : stream;
            CDReader reader = new(readable, true);
            return new Ps3DiscArchive(path, host, stream, reader, Describe(key));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
    private static string Describe(Ps3KeyResult? key) => key?.Source switch
    {
        Ps3KeySource.Embedded3K3Y => "3k3y key, decrypted on read",
        Ps3KeySource.Sidecar => "key from the sidecar file beside it",
        _ => "plain image",
    };
    public void Dispose()
    {
        Reader.Dispose();
        _stream.Dispose();
    }
}
