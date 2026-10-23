using Omega.Plugins;
using Virtuos.Formats;
using Virtuos.Formats.Nodes;
[assembly: OmegaPlugin(typeof(VirtuosPlugin))]
namespace Virtuos.Formats;
public sealed class VirtuosPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "virtuos",
        DisplayName = "Virtuos DS Racing Games",
        Version = "1.1.0",
        Author = "Omega Studio",
        Description = "Tracks and textures from GDA/GDF/GDT archives, on disk or inside a DS ROM.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new DwkArchiveFormat()];
    public IEnumerable<IContainerRecognizer> Containers { get; } = [new DwkContainerRecognizer()];
}
public sealed class DwkArchiveFormat : IArchiveFormat
{
    public string Id => "virtuos.gda";
    public string DisplayName => "Virtuos archive (.gda)";
    public FileFilter Filter { get; } = new("Virtuos archive", "*.gda");
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        (string gdf, string gdt) = Siblings(path);
        return Path.GetExtension(path).Equals(".gda", StringComparison.OrdinalIgnoreCase)
            && File.Exists(gdf)
            && File.Exists(gdt);
    }
    public IArchive Open(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        (string gdf, string gdt) = Siblings(path);
        if (!File.Exists(gdf) || !File.Exists(gdt))
        {
            throw new FileNotFoundException(
                $"{Path.GetFileName(path)} needs {Path.GetFileName(gdf)} and " +
                $"{Path.GetFileName(gdt)} beside it; one of them is missing.");
        }
        DwkArchive archive = DwkArchive.Load(path, gdf, gdt);
        string? folder = Path.GetDirectoryName(path);
        VirtuosResourceIndex? resources = folder is null
            ? null
            : VirtuosResourceIndex.Load(folder, archive.Entries.Count);
        host.Log(resources is null
            ? $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, no resource database found."
            : $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, " +
              $"{resources.DatabaseCount} resource database(s).");
        return new DwkArchiveHandle(archive, resources, path, gdt, gdf);
    }
    private static (string Gdf, string Gdt) Siblings(string gdaPath)
    {
        string stem = gdaPath.Length >= 4 ? gdaPath[..^4] : gdaPath;
        return (stem + ".gdf", stem + ".gdt");
    }
}
internal sealed class DwkArchiveHandle : IArchive
{
    private readonly DwkArchive _archive;
    private readonly string _gdaPath;
    private readonly string _gdfPath;
    private readonly string _gdtPath;
    public DwkArchiveHandle(
        DwkArchive archive,
        VirtuosResourceIndex? resources,
        string gdaPath,
        string gdtPath,
        string gdfPath)
    {
        _archive = archive;
        _gdaPath = gdaPath;
        _gdfPath = gdfPath;
        _gdtPath = gdtPath;
        DisplayName = Path.GetFileName(gdaPath);
        Root = DwkTree.Build(new TrackContext(archive, resources), DisplayName);
    }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _archive.HasChanges;
    public IReadOnlyList<string> BackupPaths => [_gdaPath, _gdtPath];
    public void Save()
    {
        (byte[] gda, byte[] gdt) = _archive.Pack();
        List<(string Path, byte[] Rebuilt)> files = [(_gdaPath, gda), (_gdtPath, gdt)];
        WriteGuard.Commit($"the Virtuos archive {DisplayName}", files, temporaries =>
        {
            byte[] gdaWritten = File.ReadAllBytes(temporaries[0]);
            byte[] gdtWritten = File.ReadAllBytes(temporaries[1]);
            byte[] gdfData = File.ReadAllBytes(_gdfPath);
            DwkArchive reread = DwkArchive.Load(gdaWritten, gdfData, gdtWritten);
            reread.Pack();
            return reread.Entries.Count == _archive.Entries.Count;
        });
        _archive.MarkSaved();
    }
    public void Dispose()
    {
    }
}
