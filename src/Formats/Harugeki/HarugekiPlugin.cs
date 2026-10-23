using Harugeki.Formats;
using Harugeki.Formats.Nodes;
using Omega.Plugins;
[assembly: OmegaPlugin(typeof(HarugekiPlugin))]
namespace Harugeki.Formats;
public sealed class HarugekiPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "harugeki",
        DisplayName = "Suzumiya Haruhi no Chourantou / Gekitou",
        Version = "1.0.1",
        Author = "Omega Studio",
        Description = "Models, textures and animation from the 2007 PC game's data/*.bin archives.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new RingArchiveFormat()];
}
public sealed class RingArchiveFormat : IArchiveFormat
{
    public string Id => "harugeki.bin";
    public string DisplayName => "Harugeki archive (.bin)";
    public FileFilter Filter { get; } = new("Harugeki archive", "*.bin");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return RingArchive.LooksLikeArchive(head, length);
    }
    public IArchive Open(string path, IHostServices host)
    {
        return new RingArchiveHandle(RingArchive.Load(path, host), path, host);
    }
}
internal sealed class RingArchiveHandle : IArchive
{
    private readonly RingArchive _archive;
    private readonly string _path;
    private readonly IHostServices? _host;
    public RingArchiveHandle(RingArchive archive, string path, IHostServices? host = null)
    {
        _archive = archive;
        _path = path;
        _host = host;
        DisplayName = Path.GetFileName(path);
        Root = new ArchiveRootNode(archive, DisplayName);
    }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _archive.IsDirty;
    public IReadOnlyList<string> BackupPaths => [_path];
    public void Save()
    {
        byte[] bytes = _archive.Save();
        List<(string Path, byte[] Rebuilt)> files = [(_path, bytes)];
        WriteGuard.Commit(DisplayName, files, temporaries =>
        {
            byte[] written = File.ReadAllBytes(temporaries[0]);
            return RingArchive.LooksLikeArchive(written, written.Length)
                && RingArchive.Load(temporaries[0], _host).Root.Children.Count
                    == _archive.Root.Children.Count;
        });
        _archive.MarkSaved();
    }
    public void Dispose()
    {
    }
}
internal sealed class ArchiveRootNode : AssetNode, IDescribable
{
    private readonly RingArchive _archive;
    public ArchiveRootNode(RingArchive archive, string name)
        : base(name, "/", AssetKinds.Container, Sizes.Describe(archive.Data.Length))
    {
        _archive = archive;
        ChildFactory = () => NodeFactory.Children(archive.Root);
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Archive", Path.GetFileName(_archive.Path ?? string.Empty));
        yield return PropertyRow.Of("Size", Sizes.Describe(_archive.Data.Length));
        yield return PropertyRow.Of("Root entries", _archive.Root.Children.Count);
    }
}
