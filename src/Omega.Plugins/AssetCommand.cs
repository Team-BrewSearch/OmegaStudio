namespace Omega.Plugins;

public enum AssetCommandKind
{
    Immediate,
    OpenFile,
    SaveFile,
}
[Flags]
public enum AssetChange
{
    None = 0,
    Bytes = 1,
    Scene = 2,
    Image = 4,
    Children = 8,
    Properties = 16,
    Dirty = 32,
}
public sealed class AssetCommandContext
{
    public AssetCommandContext(IAssetNode node, IHostServices host, string? path)
    {
        Node = node;
        Host = host;
        Path = path;
    }
    public IAssetNode Node { get; }
    public IHostServices Host { get; }
    public string? Path { get; }
    public AssetChange Changed { get; set; }
    public void Log(string message)
    {
        Host.Log(message);
    }
}
public sealed class AssetCommand
{
    public required string Title { get; init; }
    public AssetCommandKind Kind { get; init; } = AssetCommandKind.Immediate;
    public FileFilter? Filter { get; init; }
    public string SuggestedFileName { get; init; } = string.Empty;
    public required Action<AssetCommandContext> Execute { get; init; }
}
