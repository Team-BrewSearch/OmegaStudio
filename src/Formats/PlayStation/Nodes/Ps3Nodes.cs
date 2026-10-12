using System.Text;
using Omega.Plugins;
using Sizes = Omega.Plugins.Sizes;
namespace PlayStation.Formats.Nodes;
internal sealed class Ps3RootNode : AssetNode
{
    private readonly Ps3DiscArchive _archive;
    public Ps3RootNode(Ps3DiscArchive archive, string displayName)
        : base(displayName, displayName, AssetKinds.Container, archive.Description)
    {
        _archive = archive;
        ChildFactory = () => Children("\\");
    }
    private List<IAssetNode> Children(string directory)
    {
        List<IAssetNode> rows = [];
        foreach (string name in _archive.Reader.GetDirectories(directory))
        {
            rows.Add(new Ps3DirectoryNode(_archive, name));
        }
        foreach (string name in _archive.Reader.GetFiles(directory))
        {
            rows.Add(new Ps3FileNode(_archive, name));
        }
        return rows;
    }
}
internal sealed class Ps3DirectoryNode : AssetNode
{
    private readonly Ps3DiscArchive _archive;
    private readonly string _path;
    public Ps3DirectoryNode(Ps3DiscArchive archive, string path)
        : base(Label(path), path, AssetKinds.Container, "")
    {
        _archive = archive;
        _path = path;
        ChildFactory = () =>
        {
            List<IAssetNode> rows = [];
            foreach (string name in _archive.Reader.GetDirectories(_path))
            {
                rows.Add(new Ps3DirectoryNode(_archive, name));
            }
            foreach (string name in _archive.Reader.GetFiles(_path))
            {
                rows.Add(new Ps3FileNode(_archive, name));
            }
            return rows;
        };
    }
    private static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];
}
internal sealed class Ps3FileNode : StreamingAssetNode
{
    private readonly Ps3DiscArchive _archive;
    private readonly string _path;
    public Ps3FileNode(Ps3DiscArchive archive, string path)
        : base(
            Label(path),
            path,
            new FileWindow(archive, path),
            AssetKinds.Auto,
            Sizes.Describe(archive.Reader.GetFileLength(path)))
    {
        _archive = archive;
        _path = path;
    }
    public override string Extension => Path.GetExtension(Label(_path)) is { Length: > 1 } found ? found : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Label(_path));
    public override bool CanReplaceBytes => false;
    protected override void Write(byte[] payload)
    {
        throw new InvalidOperationException(
            $"'{Label(_path)}' is read only: a PS3 disc is read through its own filesystem, "
            + "and this build does not rewrite it.");
    }
    private static string Label(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];
    private sealed class FileWindow(Ps3DiscArchive archive, string path) : IPayloadSource
    {
        public long Length => archive.Reader.GetFileLength(path);
        public int Head(Span<byte> destination)
        {
            if (Length == 0)
            {
                return 0;
            }
            using Stream stream = archive.Reader.OpenFile(path, FileMode.Open);
            return stream.Read(destination);
        }
        public byte[] ReadAll()
        {
            if (Length == 0)
            {
                return [];
            }
            using Stream stream = archive.Reader.OpenFile(path, FileMode.Open);
            byte[] payload = new byte[stream.Length];
            stream.ReadExactly(payload);
            return payload;
        }
    }
}
