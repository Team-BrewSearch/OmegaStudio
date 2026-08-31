using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;

internal abstract class AssetSnapshot
{
    public static AssetSnapshot? Capture(IAssetNode? node)
    {
        return node is null
            ? null
            : PluginGuard.Try($"Reading '{node.Name}' for the edit history", () => Read(node), null);
    }
    private static AssetSnapshot? Read(IAssetNode node)
    {
        return node switch
        {
            IBinaryAsset { CanReplaceBytes: true } binary => new BytesSnapshot(binary.Data.ToArray()),
            IImageAsset { CanReplaceImage: true } image => new ImageSnapshot(image.Image),
            _ => null,
        };
    }
    public abstract bool SameAs(AssetSnapshot other);
    public abstract void Restore(IAssetNode node);
    private sealed class BytesSnapshot : AssetSnapshot
    {
        private readonly byte[] _bytes;
        public BytesSnapshot(byte[] bytes)
        {
            _bytes = bytes;
        }
        public override bool SameAs(AssetSnapshot other)
        {
            return other is BytesSnapshot snapshot && _bytes.AsSpan().SequenceEqual(snapshot._bytes);
        }
        public override void Restore(IAssetNode node)
        {
            ((IBinaryAsset)node).ReplaceBytes([.. _bytes]);
        }
    }
    private sealed class ImageSnapshot : AssetSnapshot
    {
        private readonly ImageData _image;
        public ImageSnapshot(ImageData image)
        {
            _image = image;
        }
        public override bool SameAs(AssetSnapshot other)
        {
            return other is ImageSnapshot snapshot
                && _image.Width == snapshot._image.Width
                && _image.Height == snapshot._image.Height
                && _image.Rgba.AsSpan().SequenceEqual(snapshot._image.Rgba);
        }
        public override void Restore(IAssetNode node)
        {
            ((IImageAsset)node).ReplaceImage(ImageData.Create(
                _image.Name, _image.Width, _image.Height, [.. _image.Rgba], _image.FormatLabel));
        }
    }
}
internal sealed record AssetEdit(
    string Title,
    TreeItemViewModel Row,
    AssetSnapshot Before,
    AssetSnapshot After,
    AssetChange Changed);
internal sealed class EditHistory
{
    private const int Capacity = 24;
    private readonly List<AssetEdit> _edits = [];
    private int _applied;
    public bool CanUndo => _applied > 0;
    public bool CanRedo => _applied < _edits.Count;
    public string? UndoTitle => CanUndo ? _edits[_applied - 1].Title : null;
    public string? RedoTitle => CanRedo ? _edits[_applied].Title : null;
    public void Push(AssetEdit edit)
    {
        _edits.RemoveRange(_applied, _edits.Count - _applied);
        _edits.Add(edit);
        if (_edits.Count > Capacity)
        {
            _edits.RemoveAt(0);
        }
        _applied = _edits.Count;
    }
    public AssetEdit Undo()
    {
        return CanUndo
            ? _edits[--_applied]
            : throw new InvalidOperationException("there is nothing to undo");
    }
    public AssetEdit Redo()
    {
        return CanRedo
            ? _edits[_applied++]
            : throw new InvalidOperationException("there is nothing to redo");
    }
    public void Clear()
    {
        _edits.Clear();
        _applied = 0;
    }
}
