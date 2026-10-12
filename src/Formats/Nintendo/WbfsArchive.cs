using Omega.Disc.Formats.Nintendo;
using Omega.Disc.Streams;
using Omega.Plugins;
namespace Nintendo.Formats;
public sealed class WbfsArchive : IArchive
{
    private readonly SharedStream _shared;
    private WbfsArchive(string path, IHostServices host, SharedStream shared, WbfsContainer container)
    {
        _shared = shared;
        Host = host;
        Container = container;
        DisplayName = Path.GetFileName(path);
        Root = new Nodes.WbfsRootNode(this, DisplayName);
    }
    public WbfsContainer Container { get; }
    public IHostServices Host { get; }
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty { get; private set; }
    public IReadOnlyList<string> BackupPaths =>
        [_shared.Stream is System.IO.FileStream file ? file.Name : DisplayName];
    public void MarkDirty()
    {
        IsDirty = true;
    }
    public static WbfsArchive OpenReadWrite(string path, IHostServices host)
    {
        ArgumentNullException.ThrowIfNull(host);
        SharedStream shared = SharedStream.Open(path, readOnly: false);
        try
        {
            WbfsContainer container = WbfsContainer.Open(shared);
            return new WbfsArchive(path, host, shared, container);
        }
        catch
        {
            shared.Stream.Dispose();
            throw;
        }
    }
    public void Save()
    {
        Container.VerifyTables();
        Container.CommitWrites();
        IsDirty = false;
        Host.Log($"Saved {DisplayName}.");
    }
    public void DiscardEdits()
    {
        Container.DiscardWrites();
        IsDirty = false;
    }
    public int PendingEdits => Container.DeferredBytes;
    public void Dispose()
    {
        int waiting = Container.DeferredClusters;
        Container.DiscardWrites();
        if (waiting > 0)
        {
            Host.Log($"Discarded {waiting} pending edit(s) to {DisplayName}.");
        }
        IsDirty = false;
        _shared.Stream.Dispose();
    }
}
