namespace Omega.Plugins;
/// <summary>The kind strings a node may report. The kind picks the row&apos;s icon and which panes light up.</summary>
public static class AssetKinds
{
    /// <summary>Empty: let the host infer the kind from the capabilities the node implements.</summary>
    public const string Auto = "";
    /// <summary>A node that exists to hold other nodes.</summary>
    public const string Container = "Container";
    /// <summary>A whole model: geometry, materials and skeleton together.</summary>
    public const string Model = "Model";
    /// <summary>One drawable piece of geometry.</summary>
    public const string Mesh = "Mesh";
    /// <summary>A surface description referencing a texture.</summary>
    public const string Material = "Material";
    /// <summary>One joint of a skeleton.</summary>
    public const string Bone = "Bone";
    /// <summary>An image.</summary>
    public const string Texture = "Texture";
    /// <summary>A set of keyed transforms.</summary>
    public const string Animation = "Animation";
    /// <summary>A sound clip.</summary>
    public const string Audio = "Audio";
    /// <summary>Human-readable text.</summary>
    public const string Text = "Text";
    /// <summary>Recognised as nothing in particular.</summary>
    public const string Unknown = "Unknown";
    /// <summary>The kind to display for a node: what it declares, or what its capabilities imply.</summary>
    /// <param name="node">The node to classify.</param>
    /// <returns>One of the constants on this class.</returns>
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
