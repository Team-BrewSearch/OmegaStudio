namespace Kartz.Formats;

/// <summary>What a blob in a DWK archive turned out to be.</summary>
public enum AssetKind
{
    Unknown,

    /// <summary>An NTTX/PALT/IMGE <c>5TX</c> texture.</summary>
    Texture,

    /// <summary>A DS display-list <c>TRK</c> track.</summary>
    Track,
}

/// <summary>
/// Content sniffing. The archive's path table gives every entry a name, but the
/// extensions in it are not reliable enough to classify by — several tracks and
/// textures share one — so what a blob is comes from its bytes.
/// </summary>
public static class AssetTypes
{
    public static AssetKind Detect(ReadOnlySpan<byte> blob)
    {
        return SskTexture.IsTexture(blob) ? AssetKind.Texture
            : SskTrack.IsTrack(blob) ? AssetKind.Track
            : AssetKind.Unknown;
    }
}
