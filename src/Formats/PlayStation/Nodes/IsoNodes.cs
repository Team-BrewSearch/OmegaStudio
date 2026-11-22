using Omega.Disc.IO;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace PlayStation.Formats.Nodes;

internal sealed class IsoRootNode : AssetNode
{
    public IsoRootNode(IsoDiscArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, archive.Description)
    {
        ChildFactory = () => IsoListing.Of(archive, "\\");
    }
}

internal sealed class IsoDirNode : AssetNode
{
    public IsoDirNode(IsoDiscArchive archive, string path)
        : base(IsoListing.Label(path), path, AssetKinds.Container, "")
    {
        ChildFactory = () => IsoListing.Of(archive, path);
    }
}

internal static class IsoListing
{
    public static List<IAssetNode> Of(IsoDiscArchive archive, string dir)
    {
        lock (archive.Gate)
        {
            List<IAssetNode> rows = [.. archive.Reader.GetDirectories(dir).Select(d => (IAssetNode)new IsoDirNode(archive, d))];
            rows.AddRange(archive.Reader.GetFiles(dir).Select(f => new IsoFileNode(archive, f)));
            return rows;
        }
    }
    public static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];
}

internal sealed class IsoFileNode : StreamingAssetNode
{
    private readonly string _path;
    public IsoFileNode(IsoDiscArchive archive, string path)
        : base(
            IsoListing.Label(path), path,
            new FileWindow(archive, path),
            AssetKinds.Auto,
            Sizes.Describe(GetLength(archive, path)))
    {
        _path = path;
    }
    public override string Extension  => Path.GetExtension(IsoListing.Label(_path)) is { Length: > 1 } e ? e : ".bin";
    public override string FileStem   => Path.GetFileNameWithoutExtension(IsoListing.Label(_path));
    public override bool   CanReplaceBytes => false;
    protected override void Write(byte[] payload) =>
        throw new InvalidOperationException($"'{IsoListing.Label(_path)}' is in a read-only disc image.");

    private static long GetLength(IsoDiscArchive archive, string path)
    {
        lock (archive.Gate) { return archive.Reader.GetFileLength(path); }
    }

    private sealed class FileWindow(IsoDiscArchive archive, string path) : IPayloadSource
    {
        public long Length
        {
            get { lock (archive.Gate) { return archive.Reader.GetFileLength(path); } }
        }
        public int Head(Span<byte> destination)
        {
            lock (archive.Gate)
            {
                if (archive.Reader.GetFileLength(path) == 0) return 0;
                using Stream s = archive.Reader.OpenFile(path, FileMode.Open);
                return s.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
            }
        }
        public byte[] ReadAll()
        {
            lock (archive.Gate)
            {
                if (archive.Reader.GetFileLength(path) == 0) return [];
                using Stream s = archive.Reader.OpenFile(path, FileMode.Open);
                return PayloadReader.ReadAll(s, path);
            }
        }
    }
}
