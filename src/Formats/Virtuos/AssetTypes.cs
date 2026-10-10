namespace Virtuos.Formats;

public enum AssetKind
{
    Unknown,
    Texture,
    Track,
    Zone,
    Collision,
    Visibility,
    Car,
    SkinnedModel,
    Skeleton,
    Animation,
}
public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return SskTexture.IsTexture(blob) ? AssetKind.Texture
            : SkeModel.IsSkeleton(blob) ? AssetKind.Skeleton
            : AniModel.IsAnimation(blob) ? AssetKind.Animation
            : SskTrack.IsTrack(blob) ? AssetKind.Track
            : ZonModel.IsZone(blob) ? AssetKind.Zone
            : ColModel.IsCollision(blob) ? AssetKind.Collision
            : BpvsModel.IsVisibility(blob) ? AssetKind.Visibility
            : CarModel.IsCar(blob) ? AssetKind.Car
            : AmdModel.IsSkinnedModel(blob) ? AssetKind.SkinnedModel
            : AssetKind.Unknown;
    }
}
