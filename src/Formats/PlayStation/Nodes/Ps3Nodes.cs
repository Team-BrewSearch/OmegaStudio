using System.Text;
using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace PlayStation.Formats.Nodes;
internal sealed class Ps3RootNode : AssetNode
{
    public Ps3RootNode(Ps3DiscArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, archive.Description)
    {
        ChildFactory = () => Ps3Listing.Of(archive, "\\");
    }
}
internal sealed class Ps3DirectoryNode : AssetNode
{
    public Ps3DirectoryNode(Ps3DiscArchive archive, string path)
        : base(Ps3Listing.Label(path), path, AssetKinds.Container, "")
    {
        ChildFactory = () => Ps3Listing.Of(archive, path);
    }
}
internal static class Ps3Listing
{
    public static List<IAssetNode> Of(Ps3DiscArchive archive, string directory)
    {
        lock (archive.Gate)
        {
            List<IAssetNode> rows = [.. archive.Reader.GetDirectories(directory).Select(name => new Ps3DirectoryNode(archive, name))];
            rows.AddRange(archive.Reader.GetFiles(directory).Select(name => new Ps3FileNode(archive, name)));
            return rows;
        }
    }
    public static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];
}
internal sealed class Ps3FileNode : StreamingAssetNode
{
    private readonly string _path;
    public Ps3FileNode(Ps3DiscArchive archive, string path)
        : base(
            Ps3Listing.Label(path),
            path,
            new FileWindow(archive, path),
            AssetKinds.Auto,
            Sizes.Describe(new FileWindow(archive, path).Length))
    {
        _path = path;
    }
    public override string Extension => Path.GetExtension(Ps3Listing.Label(_path)) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Ps3Listing.Label(_path));
    public override bool CanReplaceBytes => false;
    protected override void Write(byte[] payload)
    {
        throw new InvalidOperationException(
            $"'{Ps3Listing.Label(_path)}' is read only: a PS3 disc is read through its own filesystem, "
            + "and this build does not rewrite it.");
    }
    private sealed class FileWindow(Ps3DiscArchive archive, string path) : IPayloadSource
    {
        public long Length
        {
            get
            {
                lock (archive.Gate)
                {
                    return archive.Reader.GetFileLength(path);
                }
            }
        }
        public int Head(Span<byte> destination)
        {
            lock (archive.Gate)
            {
                if (archive.Reader.GetFileLength(path) == 0)
                {
                    return 0;
                }
                using Stream stream = archive.Reader.OpenFile(path, FileMode.Open);
                return stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
            }
        }
        public byte[] ReadAll()
        {
            lock (archive.Gate)
            {
                if (archive.Reader.GetFileLength(path) == 0)
                {
                    return [];
                }
                using Stream stream = archive.Reader.OpenFile(path, FileMode.Open);
                return PayloadReader.ReadAll(stream, path);
            }
        }
    }
}
