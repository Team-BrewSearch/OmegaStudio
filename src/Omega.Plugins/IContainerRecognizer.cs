namespace Omega.Plugins;
/// <summary>Claims sibling rows that together make up one container, wherever they turn up.</summary>
/// <remarks>
/// A format whose archive is split across several files — a table, a name list and the data —
/// cannot be recognised from any one of them. The host offers every folder&apos;s rows to each
/// recogniser, whichever plugin produced them, so the same format opens from a folder on disk
/// and from a folder inside somebody else&apos;s archive.
/// </remarks>
public interface IContainerRecognizer
{
    /// <summary>Machine identifier for this recogniser.</summary>
    string Id { get; }
    /// <summary>Offers the rows of one folder.</summary>
    /// <param name="children">The folder&apos;s rows, in display order.</param>
    /// <param name="host">Services the recogniser and the rows it builds may call.</param>
    /// <returns>The rows to show in place of <paramref name="children"/>, or null to leave the folder alone.</returns>
    IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host);
}
