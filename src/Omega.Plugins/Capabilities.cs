namespace Omega.Plugins;

/// <summary>
/// A node backed by a run of bytes.
///
/// <para>
/// Earns <b>Extract Raw…</b>, <b>Replace Raw…</b> and a size row — which, with
/// <see cref="IAssetNode"/>, is already a working archive browser. It is also what
/// makes a node a candidate for the recognisers, so a standard payload sitting in one
/// gets its own pane for free.
/// </para>
///
/// <para>Derive from <see cref="BinaryAssetNode"/> rather than implementing this.</para>
/// </summary>
public interface IBinaryAsset
{
    /// <summary>The payload's current size, which a replacement is free to change.</summary>
    int Size { get; }

    /// <summary>The bytes, without copying. Valid until the node is replaced.</summary>
    ReadOnlySpan<byte> Data { get; }

    /// <summary>
    /// The extension a raw extract should suggest, including the dot — <c>.bin</c>
    /// unless the payload is a known standalone file type.
    /// </summary>
    string Extension { get; }

    /// <summary>
    /// Whether <see cref="ReplaceBytes"/> is allowed. False for something the format
    /// cannot rewrite safely — a computed index, say.
    /// </summary>
    bool CanReplaceBytes { get; }

    /// <summary>
    /// Swaps in new bytes, of any length. Marks the archive dirty; the write happens
    /// on save.
    /// </summary>
    void ReplaceBytes(byte[] payload);
}

/// <summary>
/// A node that can be drawn in the 3D viewport. Earns the Viewport tab, the shading
/// modes, the camera, and <b>Export glTF…</b>.
/// </summary>
public interface ISceneAsset
{
    /// <summary>
    /// Builds the renderable geometry, or null if there is nothing to draw — which is
    /// a real answer, not a failure, for a node whose signature matched but which holds
    /// no geometry.
    ///
    /// <para>
    /// Called on selection and after an edit, not per frame, so a format that decodes
    /// lazily should decode here. The host validates the result before it reaches the
    /// GPU; see <see cref="SceneGeometry.Validate"/>.
    /// </para>
    /// </summary>
    SceneGeometry? GetScene();
}

/// <summary>
/// A node holding a single image. Earns the Texture tab — with zoom, pan and
/// nearest-neighbour magnifying — plus <b>Export PNG…</b> and, when
/// <see cref="CanReplaceImage"/> is set, <b>Replace from PNG…</b>.
/// </summary>
public interface IImageAsset
{
    /// <summary>The decoded image, RGBA8.</summary>
    ImageData Image { get; }

    /// <summary>Whether this image can be written back to the archive.</summary>
    bool CanReplaceImage { get; }

    /// <summary>
    /// Re-encodes <paramref name="image"/> into the archive, at whatever size it
    /// happens to be, and updates <see cref="Image"/> to match.
    ///
    /// <para>
    /// The incoming image is never resampled for you. A format that cannot store
    /// arbitrary dimensions should throw, with a message naming the sizes it accepts.
    /// </para>
    /// </summary>
    void ReplaceImage(ImageData image);
}

/// <summary>
/// A node holding an encoded audio clip. Earns the Audio tab — transport, seeking,
/// duration and format readout — and <b>Extract Audio…</b>.
///
/// <para>
/// The host decodes OGG Vorbis and WAV; the plugin only hands over the bytes. In
/// practice a game plugin rarely implements this at all, because the Generic plugin
/// recognises a clip wherever it is found.
/// </para>
/// </summary>
public interface IAudioAsset
{
    /// <summary>The clip exactly as stored: a complete .ogg or .wav file.</summary>
    ReadOnlySpan<byte> EncodedAudio { get; }
}

/// <summary>
/// A node that can be posed over time. Earns the Animation tab: clip picker,
/// transport and dope sheet.
///
/// <para>
/// Only meaningful together with <see cref="ISceneAsset"/> — the host poses the scene
/// that node produced. The host owns the clock and the transport; the plugin owns the
/// skinning, because how a vertex follows a bone is format knowledge.
/// </para>
/// </summary>
public interface IAnimatedAsset
{
    /// <summary>Clips that can drive this asset. Empty means the tab stays hidden.</summary>
    IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>
    /// Evaluates <paramref name="clip"/> at <paramref name="seconds"/> into posed
    /// vertex arrays.
    ///
    /// <para>
    /// Both hold three floats per vertex of the scene from
    /// <see cref="ISceneAsset.GetScene"/>, in the same order — meshes in sequence,
    /// vertices within each. Return false for a frame that cannot be posed; the
    /// viewport then shows the rest pose rather than something wrong.
    /// </para>
    /// </summary>
    bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals);
}

/// <summary>
/// A node with something to say in the Properties pane. The host adds rows of its own
/// — size, location — around these.
/// </summary>
public interface IDescribable
{
    /// <summary>Rows for the Properties pane. The host adds its own around them.</summary>
    IEnumerable<PropertyRow> Describe();
}

/// <summary>
/// A node offering commands beyond the ones its capabilities already imply.
///
/// <para>
/// For genuinely format-specific work — importing geometry into a game's own model
/// blob, say. Extract, replace, and the glTF and PNG exports all come free from the
/// capabilities above; duplicating them here just puts two of each in the menu.
/// </para>
/// </summary>
public interface ICommandSource
{
    /// <summary>Extra context-menu entries for this node.</summary>
    IEnumerable<AssetCommand> GetCommands();
}
