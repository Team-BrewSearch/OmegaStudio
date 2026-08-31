using Omega.Plugins.Gltf;
namespace Omega.Plugins;
/// <summary>Image decoding and PNG writing, so no plugin carries a codec of its own.</summary>
public interface IImageCodec
{
    /// <summary>Decodes an image file from disk.</summary>
    /// <param name="path">Full path of the image.</param>
    /// <returns>The decoded image.</returns>
    ImageData Load(string path);
    /// <summary>Decodes an image already in memory.</summary>
    /// <param name="bytes">The encoded image.</param>
    /// <param name="name">Name to give the result, used to suggest an export filename.</param>
    /// <returns>The decoded image.</returns>
    ImageData Load(ReadOnlySpan<byte> bytes, string name = "");
    /// <summary>Writes an image to disk as a PNG.</summary>
    /// <param name="image">The image to write.</param>
    /// <param name="path">Destination path.</param>
    void SavePng(ImageData image, string path);
    /// <summary>Encodes an image as a PNG in memory.</summary>
    /// <param name="image">The image to encode.</param>
    /// <returns>The PNG bytes.</returns>
    byte[] EncodePng(ImageData image);
}
/// <summary>glTF reading and writing, shared by every format that imports or exports geometry.</summary>
public interface IGltfCodec
{
    /// <summary>Reads a glTF or GLB file into memory in full.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <returns>The parsed document.</returns>
    GltfDocument Read(string path);
    /// <summary>Opens a glTF or GLB file for selective reading, leaving its buffers unread until asked for.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <returns>A source the caller must dispose.</returns>
    IGltfSource Open(string path);
    /// <summary>Writes geometry out as glTF, or as GLB when the path says so.</summary>
    /// <param name="scene">The geometry to write.</param>
    /// <param name="path">Destination path.</param>
    void Write(SceneGeometry scene, string path);
}
/// <summary>Everything the host lends a plugin. Handed over on open and carried by every command.</summary>
public interface IHostServices
{
    /// <summary>Writes a line to the Console pane.</summary>
    /// <param name="message">The line to write.</param>
    void Log(string message);
    /// <summary>Image codec.</summary>
    IImageCodec Images { get; }
    /// <summary>glTF codec.</summary>
    IGltfCodec Gltf { get; }
    /// <summary>Folder this plugin was loaded from, for reading files shipped alongside it.</summary>
    string PluginDirectory { get; }
    /// <summary>Whether some plugin recognises these bytes as a payload in their own right, so a container walk should stop here rather than descend.</summary>
    /// <param name="data">The blob to test.</param>
    /// <returns>True when a recogniser claims it.</returns>
    bool IsKnownPayload(ReadOnlySpan<byte> data);
}
