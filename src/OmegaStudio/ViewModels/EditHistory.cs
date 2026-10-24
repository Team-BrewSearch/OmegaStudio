using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal abstract class AssetSnapshot
{
    public static AssetSnapshot? Capture(IAssetNode? node)
    {
        return Capture(node, out _);
    }
    public static AssetSnapshot? Capture(IAssetNode? node, out bool tooLarge)
    {
        tooLarge = false;
        if (node is null)
        {
            return null;
        }
        bool large = false;
        AssetSnapshot? snapshot = PluginGuard.Try(
            $"Reading '{NodeAccess.Name(node)}' for the edit history", () => Read(node, out large), null);
        tooLarge = large;
        return snapshot;
    }
    private static AssetSnapshot? Read(IAssetNode node, out bool tooLarge)
    {
        tooLarge = false;
        switch (node)
        {
            case IBinaryAsset { CanReplaceBytes: true } binary:
                long length = binary.TryGetLength();
                if ((length >= 0 ? length : binary.Size) > Limit)
                {
                    tooLarge = true;
                    return null;
                }
                return new BytesSnapshot(binary.Data.ToArray());
            case IImageAsset { CanReplaceImage: true } texture:
                ImageData image = texture.Image;
                if (image.Rgba.LongLength > Limit)
                {
                    tooLarge = true;
                    return null;
                }
                return new ImageSnapshot(ImageData.Create(
                    image.Name, image.Width, image.Height, (byte[])image.Rgba.Clone(), image.FormatLabel));
            default:
                return null;
        }
    }
    private static long Limit => EditHistory.Budget / 2;
    public static AssetSnapshot ForProperty(Action<string> apply, string value)
    {
        return new PropertySnapshot(apply, value);
    }
    public abstract long Bytes { get; }
    public virtual bool RestoresOnUiThread => false;
    public abstract bool SameAs(AssetSnapshot other);
    public abstract void Restore(IAssetNode node);
    private sealed class BytesSnapshot : AssetSnapshot
    {
        private readonly byte[] _bytes;
        public BytesSnapshot(byte[] bytes)
        {
            _bytes = bytes;
        }
        public override long Bytes => _bytes.LongLength;
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
        public override long Bytes => 2L * _value.Length;
        public override bool RestoresOnUiThread => true;
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
        public override long Bytes => _image.Rgba.LongLength;
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
        long needed = Cost(edit);
        if (needed > Capacity)
        {
            return false;
        }
        for (int i = _applied; i < _edits.Count; i++)
        {
            _bytes -= Cost(_edits[i]);
        }
        _edits.RemoveRange(_applied, _edits.Count - _applied);
        _edits.Add(edit);
        _bytes += needed;
        while (_bytes > Capacity && _edits.Count > 1)
        {
            _bytes -= Cost(_edits[0]);
            _edits.RemoveAt(0);
        }
        _applied = _edits.Count;
        return true;
    }
    public long Bytes => _bytes;
    public int Count => _edits.Count;
    public long Capacity { get; init; } = Budget;
    private static long Cost(AssetEdit edit) => edit.Before.Bytes + edit.After.Bytes;
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
