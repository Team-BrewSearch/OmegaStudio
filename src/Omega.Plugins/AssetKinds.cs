namespace Omega.Plugins;
public static class AssetKinds
{
    public const string Auto = "";
    public const string Container = "Container";
    public const string Model = "Model";
    public const string Mesh = "Mesh";
    public const string Material = "Material";
    public const string Bone = "Bone";
    public const string Texture = "Texture";
    public const string Animation = "Animation";
    public const string Audio = "Audio";
    public const string Text = "Text";
    public const string Unknown = "Unknown";
    public static string Resolve(IAssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return !string.IsNullOrEmpty(node.Kind)
            ? node.Kind
            : node switch
            {
                ISceneAsset => Model,
                IImageAsset => Texture,
                IAudioAsset => Audio,
                { HasChildren: true } => Container,
                _ => Unknown,
            };
    }
}
