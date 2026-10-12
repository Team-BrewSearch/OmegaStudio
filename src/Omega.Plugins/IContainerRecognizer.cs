namespace Omega.Plugins;
public interface IContainerRecognizer
{
    string Id { get; }
    IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host);
}
