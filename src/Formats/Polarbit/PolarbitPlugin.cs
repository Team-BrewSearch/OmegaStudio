using Omega.Plugins;
using Polarbit.Formats;
using Polarbit.Formats.Nodes;
[assembly: OmegaPlugin(typeof(PolarbitPlugin))]
namespace Polarbit.Formats;
public sealed class PolarbitPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "polarbit",
        DisplayName = "Polarbit iOS Racing Games",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Models, tracks, scenes, textures, fonts, sounds, music and strings from Polarbit's .vfs archives.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new VfsFormat()];
}
public sealed class VfsFormat : IArchiveFormat
{
    public string Id => "polarbit.vfs";
    public string DisplayName => "Polarbit archive (.vfs)";
    public FileFilter Filter { get; } = new("Polarbit archive", "*.vfs");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        return VfsArchive.LooksLikeArchive(head, length);
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        VfsArchive archive = VfsArchive.Read(File.ReadAllBytes(path));
        int recovered = archive.HasNameTable ? 0 : NameRecovery.Apply(archive);
        int named = archive.Entries.Count(e => e.Name is not null);
        host.Log(archive.HasNameTable
            ? $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, names stored in the archive."
            : $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, {recovered} names recovered " +
              $"from their hashes, {archive.Entries.Count - named} unnamed.");
        return new VfsHandle(archive, path, host);
    }
}
internal sealed class VfsHandle : IArchive
{
    private readonly VfsArchive _archive;
    private readonly string _path;
    public VfsHandle(VfsArchive archive, string path, IHostServices host)
    {
        _archive = archive;
        _path = path;
        DisplayName = Path.GetFileName(path);
        Root = VfsTree.Build(new ArchiveContext(archive, host), DisplayName);
    }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _archive.IsDirty;
    public IReadOnlyList<string> BackupPaths => [_path];
    public void Save()
    {
        byte[] bytes = _archive.Build();
        List<(string Path, byte[] Rebuilt)> files = [(_path, bytes)];
        WriteGuard.Commit(DisplayName, files, temporaries =>
        {
            byte[] written = File.ReadAllBytes(temporaries[0]);
            VfsArchive check = VfsArchive.Read(written);
            return check.Entries.Count == _archive.Entries.Count
                && check.Entries.Count(e => e.Name is not null) == _archive.Entries.Count(e => e.Name is not null);
        });
        _archive.MarkSaved();
    }
    public void Dispose()
    {
    }
}
