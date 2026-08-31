namespace Omega.Plugins;
/// <summary>One container format a plugin can identify and open.</summary>
public interface IArchiveFormat
{
    /// <summary>Machine identifier for this format.</summary>
    string Id { get; }
    /// <summary>Name shown in the Open dialog and in log lines.</summary>
    string DisplayName { get; }
    /// <summary>Extensions the Open dialog offers for this format.</summary>
    FileFilter Filter { get; }
    /// <summary>Whether this format claims the file. Decide from the head alone, and answer no rather than throwing when unsure.</summary>
    /// <param name="path">Full path of the candidate file.</param>
    /// <param name="head">Its opening bytes, a few kilobytes at most.</param>
    /// <returns>True to claim the file.</returns>
    bool CanOpen(string path, ReadOnlySpan<byte> head);
    /// <summary>Opens a file this format has claimed.</summary>
    /// <param name="path">Full path of the file to open.</param>
    /// <param name="host">Services the archive and its nodes may call back into.</param>
    /// <returns>The open archive.</returns>
    IArchive Open(string path, IHostServices host);
}
