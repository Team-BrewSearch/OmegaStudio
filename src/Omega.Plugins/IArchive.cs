namespace Omega.Plugins;

public interface IArchive : IDisposable
{
    string DisplayName { get; }
    IAssetNode Root { get; }
    bool IsDirty { get; }
    IReadOnlyList<string> BackupPaths { get; }
    void Save();
}
