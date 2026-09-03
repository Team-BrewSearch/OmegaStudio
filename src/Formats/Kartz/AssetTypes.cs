namespace Kartz.Formats;

public enum AssetKind
{
    Unknown,
    Texture,
    Track,
    Zone,
    Collision,
    Visibility,
}
public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return SskTexture.IsTexture(blob) ? AssetKind.Texture
            : SskTrack.IsTrack(blob) ? AssetKind.Track
            : ZonModel.IsZone(blob) ? AssetKind.Zone
            : ColModel.IsCollision(blob) ? AssetKind.Collision
            : BpvsModel.IsVisibility(blob) ? AssetKind.Visibility
            : AssetKind.Unknown;
    }
}
