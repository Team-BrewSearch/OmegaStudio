namespace Harugeki.Formats;

public enum AssetKind
{
    Unknown,
    Container,
    Texture,
    Model,
    Animation,
}
public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return RingTexture.IsTexture(blob) ? AssetKind.Texture
            : RingModel.LooksLikeModel(blob) ? AssetKind.Model
            : RingAnimation.LooksLikeAnimation(blob) ? AssetKind.Animation
            : AssetKind.Unknown;
    }
}
