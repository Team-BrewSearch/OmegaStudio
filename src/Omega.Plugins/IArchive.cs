namespace Omega.Plugins;

/// <summary>
/// An open archive: the root of the tree the Outliner shows, and the ability to
/// write edits back.
///
/// <para>
/// The host owns the backup policy, not the plugin. Before the first write of a
/// session it copies each path in <see cref="BackupPaths"/> to <c>.bak</c>, once,
/// and only if no backup is already sitting there — so repeated saves can never
/// overwrite the pristine copy with an edited one. A plugin therefore names the
/// files it is about to overwrite and lets the host protect them.
/// </para>
/// </summary>
public interface IArchive : IDisposable
{
    /// <summary>Shown as the root row of the Outliner, usually the file name.</summary>
    string DisplayName { get; }

    /// <summary>The root of the asset tree. Its children are read lazily, on expand.</summary>
    IAssetNode Root { get; }

    /// <summary>Whether anything has been edited since the last successful save.</summary>
    bool IsDirty { get; }

    /// <summary>
    /// Every file <see cref="Save"/> will overwrite. Usually one; a format stored
    /// across several files — a data blob plus its index — names them all.
    /// </summary>
    IReadOnlyList<string> BackupPaths { get; }

    /// <summary>
    /// Writes every edit back to disk, and clears <see cref="IsDirty"/> on success.
    /// Called on a background thread, after backups have been taken.
    /// </summary>
    void Save();
}
