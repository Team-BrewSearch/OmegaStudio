namespace Omega.Plugins;
/// <summary>A node whose payload is a byte range the host can extract, and sometimes replace.</summary>
public interface IBinaryAsset
{
    /// <summary>Length of the payload in bytes.</summary>
    int Size { get; }
    /// <summary>The payload as it stands now.</summary>
    ReadOnlySpan<byte> Data { get; }
    /// <summary>Extension to suggest when extracting, leading dot included.</summary>
    string Extension { get; }
    /// <summary>Whether the format accepts a wholesale byte replacement at this node.</summary>
    bool CanReplaceBytes { get; }
    /// <summary>Replaces the payload.</summary>
    /// <param name="payload">The new bytes; the node takes ownership of the array.</param>
    void ReplaceBytes(byte[] payload);
}
/// <summary>A node that can produce geometry for the viewport and for glTF export.</summary>
public interface ISceneAsset
{
    /// <summary>Builds the geometry this node stands for.</summary>
    /// <returns>The scene, or null when it cannot be assembled.</returns>
    SceneGeometry? GetScene();
}
/// <summary>A node that decodes to a picture the Texture pane can show.</summary>
public interface IImageAsset
{
    /// <summary>The decoded image.</summary>
    ImageData Image { get; }
    /// <summary>Whether the format can re-encode a replacement image here.</summary>
    bool CanReplaceImage { get; }
    /// <summary>Re-encodes the node from a new image, which may differ in size from the old one.</summary>
    /// <param name="image">The replacement pixels.</param>
    void ReplaceImage(ImageData image);
}
/// <summary>A node holding an encoded sound clip the transport can play.</summary>
public interface IAudioAsset
{
    /// <summary>The clip exactly as stored, still encoded.</summary>
    ReadOnlySpan<byte> EncodedAudio { get; }
}
/// <summary>A node carrying animation the transport can play against a scene.</summary>
public interface IAnimatedAsset
{
    /// <summary>Clips this node offers, in the order the picker lists them.</summary>
    IReadOnlyList<AnimationClip> Clips { get; }
    /// <summary>Samples a clip and writes the posed vertices.</summary>
    /// <param name="clip">One of <see cref="Clips"/>.</param>
    /// <param name="seconds">Time within the clip.</param>
    /// <param name="positions">Receives the posed positions, three floats per vertex.</param>
    /// <param name="normals">Receives the posed normals, three floats per vertex.</param>
    /// <returns>True when a pose was produced.</returns>
    bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals);
}
/// <summary>A node with rows for the Properties pane.</summary>
public interface IDescribable
{
    /// <summary>The rows to show for this node.</summary>
    /// <returns>The rows, in display order.</returns>
    IEnumerable<PropertyRow> Describe();
}
/// <summary>A node that contributes entries to its own context menu.</summary>
public interface ICommandSource
{
    /// <summary>The commands this node offers.</summary>
    /// <returns>The commands, in menu order.</returns>
    IEnumerable<AssetCommand> GetCommands();
}
