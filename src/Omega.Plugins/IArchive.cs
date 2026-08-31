namespace Omega.Plugins;
/// <summary>An archive the host has open, and the way into its contents.</summary>
public interface IArchive : IDisposable
{
    /// <summary>Label for the window title and the tree&apos;s root row.</summary>
    string DisplayName { get; }
    /// <summary>Top of the asset tree.</summary>
    IAssetNode Root { get; }
    /// <summary>Whether anything has been edited since the archive was opened or last saved.</summary>
    bool IsDirty { get; }
    /// <summary>Every file <see cref="Save"/> overwrites, so the host can copy them first.</summary>
    IReadOnlyList<string> BackupPaths { get; }
    /// <summary>Writes the current contents back over the files named by <see cref="BackupPaths"/>.</summary>
    void Save();
}
