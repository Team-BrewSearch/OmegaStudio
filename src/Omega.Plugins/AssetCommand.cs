namespace Omega.Plugins;

/// <summary>What the host must do before running a command.</summary>
public enum AssetCommandKind
{
    /// <summary>Run it. <see cref="AssetCommandContext.Path"/> is null.</summary>
    Immediate,

    /// <summary>Ask for an existing file first — an import.</summary>
    OpenFile,

    /// <summary>Ask where to write first — an export.</summary>
    SaveFile,
}

/// <summary>
/// What a command changed, so the host refreshes exactly those views. Reported rather
/// than inferred: only the plugin knows whether rewriting a blob invalidated the parsed
/// children, and re-reading everything would throw away the lazy tree.
/// </summary>
[Flags]
public enum AssetChange
{
    /// <summary>Nothing changed; the host refreshes none of its views.</summary>
    None = 0,

    /// <summary>The node's bytes changed; its size label is stale.</summary>
    Bytes = 1,

    /// <summary>The 3D geometry changed and must be rebuilt.</summary>
    Scene = 2,

    /// <summary>The image changed and the preview must be re-read.</summary>
    Image = 4,

    /// <summary>The child rows are stale and must be rebuilt.</summary>
    Children = 8,

    /// <summary>The Properties rows are stale.</summary>
    Properties = 16,

    /// <summary>The archive now has unsaved edits.</summary>
    Dirty = 32,
}

/// <summary>What a running command is given.</summary>
public sealed class AssetCommandContext
{
    /// <param name="node">The node the command was invoked on.</param>
    /// <param name="host">Host services for the duration of the call.</param>
    /// <param name="path">The file the user picked, per the command's kind.</param>
    public AssetCommandContext(IAssetNode node, IHostServices host, string? path)
    {
        Node = node;
        Host = host;
        Path = path;
    }

    /// <summary>The node the command was invoked on.</summary>
    public IAssetNode Node { get; }

    /// <summary>Services for the duration of the call — logging, image and glTF codecs.</summary>
    public IHostServices Host { get; }

    /// <summary>
    /// The chosen file for an <see cref="AssetCommandKind.OpenFile"/> or
    /// <see cref="AssetCommandKind.SaveFile"/> command; null for an immediate one.
    /// Never null when the command actually runs — a cancelled picker means it does not.
    /// </summary>
    public string? Path { get; }

    /// <summary>Set before returning, to tell the host what to refresh.</summary>
    public AssetChange Changed { get; set; }

    /// <summary>Writes a line to the console, the same way the host does.</summary>
    public void Log(string message)
    {
        Host.Log(message);
    }
}

/// <summary>
/// A context-menu entry a node contributes through <see cref="ICommandSource"/>.
///
/// <para>
/// The host owns the file picker: state the <see cref="Kind"/> and the
/// <see cref="Filter"/>, and the chosen path arrives on the context already
/// null-checked, with a cancelled dialog meaning <see cref="Execute"/> never runs.
/// </para>
/// </summary>
public sealed class AssetCommand
{
    /// <summary>Menu text, e.g. <c>Replace Model from glTF…</c>.</summary>
    public required string Title { get; init; }

    /// <summary>What the host must do before running it — usually show a file picker.</summary>
    public AssetCommandKind Kind { get; init; } = AssetCommandKind.Immediate;

    /// <summary>The picker's filter. Required for anything but an immediate command.</summary>
    public FileFilter? Filter { get; init; }

    /// <summary>Filename offered by a save picker. Ignored otherwise.</summary>
    public string SuggestedFileName { get; init; } = string.Empty;

    /// <summary>
    /// The work. Runs on a background thread, so it may block; throwing is contained
    /// and the message is logged.
    /// </summary>
    public required Action<AssetCommandContext> Execute { get; init; }
}
