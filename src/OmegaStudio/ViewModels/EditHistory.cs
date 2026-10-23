using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal abstract class AssetSnapshot
{
    public static AssetSnapshot? Capture(IAssetNode? node)
    {
        return node is null
            ? null
            : PluginGuard.Try($"Reading '{NodeAccess.Name(node)}' for the edit history", () => Read(node), null);
    }
    private static AssetSnapshot? Read(IAssetNode node)
    {
        return node switch
        {
            IBinaryAsset { CanReplaceBytes: true } binary => Fits(binary) ? new BytesSnapshot(binary.Data.ToArray()) : null,
            IImageAsset { CanReplaceImage: true } image => new ImageSnapshot(image.Image),
            _ => null,
        };
    }
    private static bool Fits(IBinaryAsset binary)
    {
        long length = binary.TryGetLength();
        return (length >= 0 ? length : binary.Size) <= EditHistory.Budget / 2;
    }
    public static AssetSnapshot ForProperty(Action<string> apply, string value)
    {
        return new PropertySnapshot(apply, value);
    }
    public abstract int Bytes { get; }
    public abstract bool SameAs(AssetSnapshot other);
    public abstract void Restore(IAssetNode node);
    private sealed class BytesSnapshot : AssetSnapshot
    {
        private readonly byte[] _bytes;
        public BytesSnapshot(byte[] bytes)
        {
            _bytes = bytes;
        }
        public override int Bytes => _bytes.Length;
        public override bool SameAs(AssetSnapshot other)
        {
            return other is BytesSnapshot snapshot && _bytes.AsSpan().SequenceEqual(snapshot._bytes);
        }
        public override void Restore(IAssetNode node)
        {
            ((IBinaryAsset)node).ReplaceBytes([.. _bytes]);
        }
    }
    private sealed class PropertySnapshot : AssetSnapshot
    {
        private readonly Action<string> _apply;
        private readonly string _value;
        public PropertySnapshot(Action<string> apply, string value)
        {
            _apply = apply;
            _value = value;
        }
        public override int Bytes => _value.Length;
        public override bool SameAs(AssetSnapshot other)
        {
            return other is PropertySnapshot snapshot && _value == snapshot._value;
        }
        public override void Restore(IAssetNode node)
        {
            _apply(_value);
        }
    }
    private sealed class ImageSnapshot : AssetSnapshot
    {
        private readonly ImageData _image;
        public ImageSnapshot(ImageData image)
        {
            _image = image;
        }
        public override int Bytes => _image.Rgba.Length;
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
    public static long Budget => MemoryBudget.Undo;
    private readonly List<AssetEdit> _edits = [];
    private int _applied;
    private long _bytes;
    public bool CanUndo => _applied > 0;
    public bool CanRedo => _applied < _edits.Count;
    public string? UndoTitle => CanUndo ? _edits[_applied - 1].Title : null;
    public string? RedoTitle => CanRedo ? _edits[_applied].Title : null;
    public bool Push(AssetEdit edit)
    {
        long needed = edit.Before.Bytes + edit.After.Bytes;
        if (needed > Budget)
        {
            return false;
        }
        _edits.RemoveRange(_applied, _edits.Count - _applied);
        _edits.Add(edit);
        _bytes += needed;
        while (_bytes > Budget && _edits.Count > 1)
        {
            AssetEdit oldest = _edits[0];
            _edits.RemoveAt(0);
            _bytes -= oldest.Before.Bytes + oldest.After.Bytes;
            _applied--;
        }
        _applied = _edits.Count;
        return true;
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
        _bytes = 0;
    }
}
