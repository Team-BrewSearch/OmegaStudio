using Omega.Plugins.Gltf;

namespace Omega.Plugins;

/// <summary>
/// PNG in and out. The host owns an image library so no plugin has to reference one.
/// </summary>
public interface IImageCodec
{
    /// <summary>Loads an image file as RGBA8, at whatever size it happens to be.</summary>
    ImageData Load(string path);

    /// <summary>As <see cref="Load(string)"/>, from bytes already in memory.</summary>
    ImageData Load(ReadOnlySpan<byte> bytes, string name = "");

    /// <summary>Writes a PNG, creating the directory if needed.</summary>
    void SavePng(ImageData image, string path);

    /// <summary>
    /// Encodes an image as PNG bytes, for a format that stores whole image files
    /// rather than raw pixels.
    /// </summary>
    byte[] EncodePng(ImageData image);
}

/// <summary>glTF in and out, so no plugin writes either.</summary>
public interface IGltfCodec
{
    /// <summary>The neutral document. What an importer should reach for first.</summary>
    GltfDocument Read(string path);

    /// <summary>
    /// The same file at the container level, for an importer that needs the detail
    /// <see cref="Read"/> drops — unmerged vertex blocks, or <c>extras</c>.
    /// </summary>
    IGltfSource Open(string path);

    /// <summary>
    /// Writes a scene as glTF (<c>.gltf</c>) or GLB (<c>.glb</c>), chosen by the
    /// extension, with textures embedded as PNG.
    /// </summary>
    void Write(SceneGeometry scene, string path);
}

/// <summary>
/// What the host lends a plugin, and the reason a plugin references one assembly and
/// no packages: anything genuinely generic belongs to the host and arrives here, so
/// adding a format never means re-solving a problem another format already solved.
/// </summary>
public interface IHostServices
{
    /// <summary>
    /// Appends a line to the console, from any thread. The host tags it with your
    /// plugin's name. Report what actually happened, partial success included — a
    /// format that dropped four unreadable nodes should say so.
    /// </summary>
    void Log(string message);

    /// <summary>PNG in and out, so a plugin needs no image library.</summary>
    IImageCodec Images { get; }

    /// <summary>glTF in and out, so a plugin needs no glTF library.</summary>
    IGltfCodec Gltf { get; }

    /// <summary>
    /// The folder this plugin was loaded from, for reading data files it ships
    /// alongside itself.
    /// </summary>
    string PluginDirectory { get; }

    /// <summary>
    /// Whether any loaded <see cref="IAssetRecognizer"/> calls these bytes a standard
    /// payload. For a container walk that must stop at one rather than descend into it,
    /// without carrying its own copy of every magic number.
    ///
    /// <para>
    /// Answers false when no recogniser is loaded, so a walk must still be safe on its
    /// own terms — a refinement, not a guarantee.
    /// </para>
    /// </summary>
    bool IsKnownPayload(ReadOnlySpan<byte> data);
}
