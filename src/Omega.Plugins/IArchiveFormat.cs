namespace Omega.Plugins;

public interface IArchiveFormat
{
    string Id { get; }
    string DisplayName { get; }
    FileFilter Filter { get; }
    bool CanOpen(string path, ReadOnlySpan<byte> head);
    IArchive Open(string path, IHostServices host);
}
