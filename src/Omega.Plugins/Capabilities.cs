namespace Omega.Plugins;
public interface IBinaryAsset
{
    int Size { get; }
    ReadOnlySpan<byte> Data { get; }
    long TryGetLength() => -1;
    ReadOnlySpan<byte> Head => Data;
    string Extension { get; }
    bool CanReplaceBytes { get; }
    void ReplaceBytes(byte[] payload);
}
public interface ISceneAsset
{
    SceneGeometry? GetScene();
}
public interface IImageAsset
{
    ImageData Image { get; }
    bool CanReplaceImage { get; }
    void ReplaceImage(ImageData image);
}
public interface IAudioAsset
{
    ReadOnlySpan<byte> EncodedAudio { get; }
}
public interface IAnimatedAsset
{
    IReadOnlyList<AnimationClip> Clips { get; }
    bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals);
}
public interface IDescribable
{
    IEnumerable<PropertyRow> Describe();
}
public interface ICommandSource
{
    IEnumerable<AssetCommand> GetCommands();
}
