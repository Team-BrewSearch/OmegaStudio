namespace Kartz.Formats;

public enum AssetKind
{
    Unknown,
    Texture,
    Track,
}
public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return SskTexture.IsTexture(blob) ? AssetKind.Texture
            : SskTrack.IsTrack(blob) ? AssetKind.Track
            : AssetKind.Unknown;
    }
}
