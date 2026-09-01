using Kartz.Formats;
using Kartz.Formats.Nodes;
using Omega.Plugins;
[assembly: OmegaPlugin(typeof(KartzPlugin))]
namespace Kartz.Formats;

public sealed class KartzPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "kartz",
        DisplayName = "Madagascar Kartz / Super Star Kartz",
        Version = "1.0.1",
        Author = "Omega Studio",
        Description = "Tracks and textures from the Nintendo DS GDA/GDF/GDT archives.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new DwkArchiveFormat()];
}
public sealed class DwkArchiveFormat : IArchiveFormat
{
    public string Id => "kartz.gda";
    public string DisplayName => "Kartz archive (.gda)";
    public FileFilter Filter { get; } = new("Kartz archive", "*.gda");
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
        KartzResourceIndex? resources = folder is null
            ? null
            : KartzResourceIndex.Load(folder, archive.Entries.Count);
        host.Log(resources is null
            ? $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, no resource database found."
            : $"{Path.GetFileName(path)}: {archive.Entries.Count} entries, " +
              $"{resources.DatabaseCount} resource database(s).");
        return new DwkArchiveHandle(archive, resources, path, gdt);
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
    private readonly string _gdtPath;
    public DwkArchiveHandle(
        DwkArchive archive,
        KartzResourceIndex? resources,
        string gdaPath,
        string gdtPath)
    {
        _archive = archive;
        _gdaPath = gdaPath;
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
        _archive.SaveAsync(_gdaPath, _gdtPath).GetAwaiter().GetResult();
    }
    public void Dispose()
    {
    }
}
