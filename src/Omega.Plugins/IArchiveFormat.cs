namespace Omega.Plugins;

/// <summary>
/// One container format a plugin can open. Each contributes its <see cref="Filter"/>
/// to the Open dialog, so a new format appears there the moment its plugin is dropped
/// into <c>formats/</c>.
/// </summary>
public interface IArchiveFormat
{
    /// <summary>Stable machine identifier, unique within the plugin.</summary>
    string Id { get; }

    /// <summary>Human name, e.g. <c>Harugeki archive (.bin)</c>.</summary>
    string DisplayName { get; }

    /// <summary>What the Open dialog offers for this format.</summary>
    FileFilter Filter { get; }

    /// <summary>
    /// Whether this format believes it can open the file. The first to say yes gets it.
    ///
    /// <para>
    /// Be honest: a format that claims every file with a given extension makes every
    /// other plugin using that extension unreachable.
    /// </para>
    /// </summary>
    /// <param name="path">Full path, for formats identified by name or by a sibling file.</param>
    /// <param name="head">The opening bytes of the file, up to 4 KB.</param>
    bool CanOpen(string path, ReadOnlySpan<byte> head);

    /// <summary>
    /// Opens the archive. Throwing here is fine and expected for a malformed file:
    /// the host catches it, logs the message, and leaves the workspace empty.
    /// </summary>
    /// <param name="path">The file the user picked.</param>
    /// <param name="host">Services — logging, PNG, glTF — for the archive's lifetime.</param>
    IArchive Open(string path, IHostServices host);
}
