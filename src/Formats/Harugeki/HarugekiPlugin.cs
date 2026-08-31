using Harugeki.Formats;
using Harugeki.Formats.Nodes;
using Omega.Plugins;

[assembly: OmegaPlugin(typeof(HarugekiPlugin))]

namespace Harugeki.Formats;

/// <summary>
/// Omega Studio's reader for <i>Suzumiya Haruhi no Gekitou</i> (2007).
///
/// <para>
/// The whole plugin surface is this class, <see cref="RingArchiveFormat"/> and the
/// nodes in <c>Nodes/</c>. Everything else here is the format itself — the parsers
/// and writers, unchanged from when this was an application of its own.
/// </para>
/// </summary>
public sealed class HarugekiPlugin : IFormatPlugin
{
    /// <inheritdoc/>
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "harugeki",
        DisplayName = "Suzumiya Haruhi no Chourantou / Gekitou",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Models, textures and animation from the 2007 PC game's data/*.bin archives.",
    };

    /// <inheritdoc/>
    public IEnumerable<IArchiveFormat> Archives { get; } = [new RingArchiveFormat()];
}

/// <summary>The nested table-of-contents container every <c>data/*.bin</c> file uses.</summary>
public sealed class RingArchiveFormat : IArchiveFormat
{
    /// <inheritdoc/>
    public string Id => "harugeki.bin";

    /// <inheritdoc/>
    public string DisplayName => "Harugeki archive (.bin)";

    /// <inheritdoc/>
    public FileFilter Filter { get; } = new("Harugeki archive", "*.bin");

    /// <summary>
    /// Whether this looks like one of the game's archives.
    ///
    /// <para>
    /// The container has no magic number — it opens with a count and a table of
    /// offsets — so the only honest test is whether that table is self-consistent
    /// against the file's actual length, which is why the length is read here rather
    /// than inferred from the head. Claiming every <c>.bin</c> would make the
    /// extension a monopoly and shut out any other plugin that also uses it.
    /// </para>
    /// </summary>
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

    /// <inheritdoc/>
    public IArchive Open(string path, IHostServices host)
    {
        return new RingArchiveHandle(RingArchive.Load(path, host), path);
    }
}

/// <summary>An open <c>.bin</c> archive, as the host holds it.</summary>
internal sealed class RingArchiveHandle : IArchive
{
    private readonly RingArchive _archive;
    private readonly string _path;

    public RingArchiveHandle(RingArchive archive, string path)
    {
        _archive = archive;
        _path = path;
        DisplayName = Path.GetFileName(path);
        Root = new ArchiveRootNode(archive, DisplayName);
    }

    public string DisplayName { get; }

    public IAssetNode Root { get; }

    public bool IsDirty => _archive.IsDirty;

    /// <summary>The single file a save overwrites; the host backs it up first.</summary>
    public IReadOnlyList<string> BackupPaths => [_path];

    public void Save()
    {
        // Serialised fully before the file is touched: rebuilding recomputes every
        // offset in the archive, and a failure part-way through would otherwise leave
        // a truncated file where the original was.
        byte[] bytes = _archive.Save();
        File.WriteAllBytes(_path, bytes);
        _archive.MarkSaved();
    }

    /// <summary>Nothing to release: the archive is a byte array and its nodes are views into it.</summary>
    public void Dispose()
    {
    }
}

/// <summary>The archive's root row, labelled with the file name.</summary>
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
