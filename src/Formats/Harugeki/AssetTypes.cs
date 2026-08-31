namespace Harugeki.Formats;

/// <summary>What a blob in this game's archives turned out to be.</summary>
public enum AssetKind
{
    Unknown,
    Container,
    Texture,
    Model,
    Animation,
}

/// <summary>
/// Content sniffing for this game's own formats.
///
/// <para>
/// A recursive container walk has to stop at payloads it recognises: an animation blob
/// is shaped exactly like a table of contents, so without this the walk descends into
/// one and shreds it into its bone tracks.
/// </para>
///
/// <para>
/// Standard formats are deliberately absent. OGG and WAV used to be listed here too,
/// which meant this file carried a second copy of magic numbers the host already knew;
/// they are recognised by the Generic plugin now, and the walk asks
/// <see cref="Omega.Plugins.IHostServices.IsKnownPayload"/> about them instead.
/// </para>
/// </summary>
public static class AssetTypes
{
    /// <summary>
    /// Identifies a blob. Order matters: the cheap magic-number check comes before the
    /// structural ones, and those run most to least specific.
    /// </summary>
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return RingTexture.IsTexture(blob) ? AssetKind.Texture
            : RingModel.LooksLikeModel(blob) ? AssetKind.Model
            : RingAnimation.LooksLikeAnimation(blob) ? AssetKind.Animation
            : AssetKind.Unknown;
    }
}
