namespace Omega.Plugins;
/// <summary>What the host must do before it can run a command.</summary>
public enum AssetCommandKind
{
    /// <summary>Runs straight away, with no dialog.</summary>
    Immediate,
    /// <summary>Prompts for an existing file first.</summary>
    OpenFile,
    /// <summary>Prompts for a destination path first.</summary>
    SaveFile,
}
/// <summary>Which views a command invalidated, so the host refreshes only those.</summary>
[Flags]
public enum AssetChange
{
    /// <summary>Nothing changed; the host records no edit.</summary>
    None = 0,
    /// <summary>The payload was rewritten.</summary>
    Bytes = 1,
    /// <summary>The geometry changed, so the viewport must rebuild.</summary>
    Scene = 2,
    /// <summary>The pixels changed, so the Texture pane must reload.</summary>
    Image = 4,
    /// <summary>The child rows changed, so the tree must rebuild them.</summary>
    Children = 8,
    /// <summary>The Properties rows changed.</summary>
    Properties = 16,
    /// <summary>The archive now has unsaved changes.</summary>
    Dirty = 32,
}
/// <summary>What a command is handed when it runs, and the channel it reports back through.</summary>
public sealed class AssetCommandContext
{
    /// <summary>Builds the context for one invocation.</summary>
    /// <param name="node">The node the command was invoked on.</param>
    /// <param name="host">Services the command may call.</param>
    /// <param name="path">The file the user chose, or null for an immediate command.</param>
    public AssetCommandContext(IAssetNode node, IHostServices host, string? path)
    {
        Node = node;
        Host = host;
        Path = path;
    }
    /// <summary>The node the command was invoked on.</summary>
    public IAssetNode Node { get; }
    /// <summary>Services the command may call.</summary>
    public IHostServices Host { get; }
    /// <summary>The file the user chose, or null for an <see cref="AssetCommandKind.Immediate"/> command.</summary>
    public string? Path { get; }
    /// <summary>Set by the command to say what it invalidated. Left at its default, the edit is not recorded.</summary>
    public AssetChange Changed { get; set; }
    /// <summary>Writes a line to the Console pane.</summary>
    /// <param name="message">The line to write.</param>
    public void Log(string message)
    {
        Host.Log(message);
    }
}
/// <summary>One entry a node adds to its own context menu.</summary>
public sealed class AssetCommand
{
    /// <summary>Menu text.</summary>
    public required string Title { get; init; }
    /// <summary>Whether the host prompts for a file before running.</summary>
    public AssetCommandKind Kind { get; init; } = AssetCommandKind.Immediate;
    /// <summary>File types the prompt offers. Unused by an immediate command.</summary>
    public FileFilter? Filter { get; init; }
    /// <summary>Name to pre-fill in a save prompt.</summary>
    public string SuggestedFileName { get; init; } = string.Empty;
    /// <summary>The work itself. Runs off the UI thread; report what changed through the context.</summary>
    public required Action<AssetCommandContext> Execute { get; init; }
}
