using Kartz.Formats;
using Kartz.Formats.Nodes;
using Omega.Plugins;

[assembly: OmegaPlugin(typeof(KartzPlugin))]

namespace Kartz.Formats;

/// <summary>
/// Omega Studio's reader for <i>DreamWorks Super Star Kartz</i> and
/// <i>Madagascar Kartz</i> on Nintendo DS.
///
/// <para>
/// Both games ship on the same engine and use every format here unchanged, so one
/// plugin covers them. See <c>docs/formats/kartz.md</c> for the formats themselves.
/// </para>
/// </summary>
public sealed class KartzPlugin : IFormatPlugin
{
    /// <inheritdoc/>
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "kartz",
        DisplayName = "Madagascar Kartz / Super Star Kartz",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Tracks and textures from the Nintendo DS GDA/GDF/GDT archives.",
    };

    /// <inheritdoc/>
    public IEnumerable<IArchiveFormat> Archives { get; } = [new DwkArchiveFormat()];
}

/// <summary>
/// The GDA/GDF/GDT archive triple.
///
/// <para>
/// Three files travel together — the data blob, the path table and the offset table
/// — and the user picks the first. Finding the other two is the format's job, which
/// is exactly why <see cref="IArchiveFormat.Open"/> takes a path rather than a
/// stream: a container spread across several files is common enough that the
/// contract has to allow for it.
/// </para>
/// </summary>
public sealed class DwkArchiveFormat : IArchiveFormat
{
    /// <inheritdoc/>
    public string Id => "kartz.gda";

    /// <inheritdoc/>
    public string DisplayName => "Kartz archive (.gda)";

    /// <inheritdoc/>
    public FileFilter Filter { get; } = new("Kartz archive", "*.gda");

    /// <summary>
    /// Whether this is a <c>.gda</c> with its two siblings beside it.
    ///
    /// <para>
    /// The blob has no header of its own to check — it is raw concatenated data, and
    /// the structure lives entirely in the <c>.gdf</c> and <c>.gdt</c>. So the honest
    /// test is that the whole triple is present, which is also what makes the failure
    /// mode useful: a lone <c>.gda</c> is declined here and explained in
    /// <see cref="Open"/> rather than opening into an empty tree.
    /// </para>
    /// </summary>
    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        (string gdf, string gdt) = Siblings(path);
        return Path.GetExtension(path).Equals(".gda", StringComparison.OrdinalIgnoreCase)
            && File.Exists(gdf)
            && File.Exists(gdt);
    }

    /// <inheritdoc/>
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

        // The .rdf databases shipped beside the archive hold the authored texture
        // order each track's chunk headers index into. Without them tracks still open,
        // just untextured, so a folder that has none is not an error.
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

    /// <summary>
    /// The <c>.gdf</c> and <c>.gdt</c> beside a <c>.gda</c>.
    ///
    /// <para>
    /// Built by trimming four characters rather than with
    /// <see cref="Path.ChangeExtension"/>, which would also strip anything after an
    /// earlier dot in a name like <c>maintrack.v2.gda</c>.
    /// </para>
    /// </summary>
    private static (string Gdf, string Gdt) Siblings(string gdaPath)
    {
        string stem = gdaPath.Length >= 4 ? gdaPath[..^4] : gdaPath;
        return (stem + ".gdf", stem + ".gdt");
    }
}

/// <summary>An open archive triple, as the host holds it.</summary>
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

    /// <summary>
    /// Both files a save rewrites. The <c>.gdf</c> is untouched — entry paths never
    /// change — so it is not listed and not backed up.
    /// </summary>
    public IReadOnlyList<string> BackupPaths => [_gdaPath, _gdtPath];

    public void Save()
    {
        // The archive only offers an async write and the host already calls this on a
        // background thread, so blocking here costs nothing and keeps IArchive.Save
        // synchronous for every format that has no async path at all.
        _archive.SaveAsync(_gdaPath, _gdtPath).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
    }
}
