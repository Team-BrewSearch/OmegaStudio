namespace Omega.Plugins;

/// <summary>
/// What kind of thing a node is, which decides the colour of its row's dot.
///
/// <para>
/// A kind is a plain string rather than an enum so a plugin can invent one the host
/// has never heard of; it simply gets the neutral colour. These constants exist so
/// the common cases agree across plugins, and a texture is the same colour whichever
/// game it came from.
/// </para>
/// </summary>
public static class AssetKinds
{
    /// <summary>
    /// Leave the kind to <see cref="Resolve"/>, which reads it off the node's
    /// capabilities. The default, and the right answer for anything whose kind is
    /// already implied by what it can do.
    /// </summary>
    public const string Auto = "";

    /// <summary>A folder, or anything else whose job is to hold other rows.</summary>
    public const string Container = "Container";

    /// <summary>Something drawable. Derived automatically for an <see cref="ISceneAsset"/>.</summary>
    public const string Model = "Model";

    /// <summary>One mesh of a model, shown as a part of it.</summary>
    public const string Mesh = "Mesh";

    /// <summary>A material record: a name, a texture reference, a colour.</summary>
    public const string Material = "Material";

    /// <summary>One bone of a skeleton.</summary>
    public const string Bone = "Bone";

    /// <summary>An image. Derived automatically for an <see cref="IImageAsset"/>.</summary>
    public const string Texture = "Texture";

    /// <summary>An animation clip.</summary>
    public const string Animation = "Animation";

    /// <summary>A sound. Derived automatically for an <see cref="IAudioAsset"/>.</summary>
    public const string Audio = "Audio";

    /// <summary>Readable text.</summary>
    public const string Text = "Text";

    /// <summary>Something nothing recognised. The fallback, and a perfectly normal answer.</summary>
    public const string Unknown = "Unknown";

    /// <summary>
    /// A node's kind: its own, or one derived from what it can do.
    ///
    /// <para>
    /// Derivation is what makes "a node detected as a model draws blue" true without
    /// every plugin remembering to say so. A plugin only names a kind for something no
    /// capability implies — a bone, a material, an animation clip.
    /// </para>
    /// </summary>
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
