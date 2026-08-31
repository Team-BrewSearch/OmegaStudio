using Omega.Plugins;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// One node's state, and the ability to put it back.
///
/// <para>
/// Undo is built on snapshots rather than on inverse operations because the host does
/// not know what any operation <i>is</i>. Every edit in the app arrives as an
/// <see cref="AssetCommand"/> — the host's own replace commands and a plugin's alike —
/// and all the host can see is the node it ran on and the state either side of it. A
/// snapshot of that state is therefore the only reversal that works for a command
/// nobody has written yet.
/// </para>
/// </summary>
internal abstract class AssetSnapshot
{
    /// <summary>
    /// Captures what <paramref name="node"/> holds now, or <see langword="null"/> when
    /// nothing about it can be put back — which is what makes an edit to it
    /// irreversible. See <see cref="EditHistory"/> for what the history does about that.
    /// </summary>
    public static AssetSnapshot? Capture(IAssetNode? node)
    {
        return node is null
            ? null
            : PluginGuard.Try($"Reading '{node.Name}' for the edit history", () => Read(node), null);
    }

    /// <summary>
    /// Bytes first, because they are exact: restoring them puts back the payload the
    /// archive had, byte for byte. The image route is the fallback for a node whose
    /// bytes the format refuses to swap wholesale — an embedded texture spliced into a
    /// model, say — where re-encoding the old pixels is the only way back.
    /// </summary>
    private static AssetSnapshot? Read(IAssetNode node)
    {
        return node switch
        {
            IBinaryAsset { CanReplaceBytes: true } binary => new BytesSnapshot(binary.Data.ToArray()),
            IImageAsset { CanReplaceImage: true } image => new ImageSnapshot(image.Image),
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="other"/> holds the same state, so a command that
    /// reported an edit but changed nothing leaves no entry in the history.
    /// </summary>
    public abstract bool SameAs(AssetSnapshot other);

    /// <summary>
    /// Writes this state back into <paramref name="node"/>. Throws exactly as the
    /// original edit would have; the caller treats a failure as the history no longer
    /// describing the archive.
    /// </summary>
    public abstract void Restore(IAssetNode node);

    /// <summary>A node's payload, as <see cref="IBinaryAsset.ReplaceBytes"/> takes it.</summary>
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
            // A copy per restore: the node takes ownership of what it is handed, and a
            // later edit that rewrites the payload in place would otherwise rewrite the
            // history's own copy of it.
            ((IBinaryAsset)node).ReplaceBytes([.. _bytes]);
        }
    }

    /// <summary>A node's decoded image, for a format that only swaps images.</summary>
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

/// <summary>
/// One reversible edit: the row it happened to, the state either side of it, and what
/// the command said it invalidated so a reversal refreshes exactly the same views.
/// </summary>
/// <param name="Title">The command's title, as the Edit menu names it.</param>
/// <param name="Row">The Outliner row the edit was made on.</param>
/// <param name="Before">The state to restore on undo.</param>
/// <param name="After">The state to restore on redo.</param>
/// <param name="Changed">What the command reported changing.</param>
internal sealed record AssetEdit(
    string Title,
    TreeItemViewModel Row,
    AssetSnapshot Before,
    AssetSnapshot After,
    AssetChange Changed);

/// <summary>
/// The undo and redo stacks, as one list with a cursor: everything before the cursor
/// has been applied, everything after it has been undone and can be redone.
///
/// <para>
/// The history describes one open archive and is dropped whenever that archive is.
/// It is also dropped whenever an edit lands that it cannot express — a command on a
/// node with neither restorable bytes nor a replaceable image — because undoing
/// something older would then restore a state from before a change the history knows
/// nothing about. Clearing is the honest answer; a greyed-out Undo is better than one
/// that quietly reverts the wrong thing.
/// </para>
///
/// <para>
/// The same limit applies to a command that edits a node other than the one it was
/// invoked on — Kartz's track import also rewrites the track's paired <c>.bpvs</c>.
/// The snapshot covers the node the command ran on, which is all the host can see; the
/// sibling stays as the import left it.
/// </para>
/// </summary>
internal sealed class EditHistory
{
    /// <summary>
    /// How many edits are kept. Each holds a whole payload — a replaced model blob is
    /// megabytes — so the depth is bounded rather than letting a long session pin every
    /// version of everything in memory.
    /// </summary>
    private const int Capacity = 24;

    private readonly List<AssetEdit> _edits = [];

    /// <summary>How many of <see cref="_edits"/> are currently applied.</summary>
    private int _applied;

    public bool CanUndo => _applied > 0;

    public bool CanRedo => _applied < _edits.Count;

    /// <summary>What Undo would reverse, or null when there is nothing to reverse.</summary>
    public string? UndoTitle => CanUndo ? _edits[_applied - 1].Title : null;

    /// <summary>What Redo would reapply, or null.</summary>
    public string? RedoTitle => CanRedo ? _edits[_applied].Title : null;

    /// <summary>
    /// Records an edit that has just been applied. Anything that had been undone is
    /// discarded: the new edit is built on the current state, not on the branch the
    /// user stepped off.
    /// </summary>
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

    /// <summary>Steps back one edit and returns it. Check <see cref="CanUndo"/> first.</summary>
    public AssetEdit Undo()
    {
        return CanUndo
            ? _edits[--_applied]
            : throw new InvalidOperationException("there is nothing to undo");
    }

    /// <summary>Steps forward one edit and returns it. Check <see cref="CanRedo"/> first.</summary>
    public AssetEdit Redo()
    {
        return CanRedo
            ? _edits[_applied++]
            : throw new InvalidOperationException("there is nothing to redo");
    }

    /// <summary>Forgets everything, releasing the payloads the entries hold.</summary>
    public void Clear()
    {
        _edits.Clear();
        _applied = 0;
    }
}
