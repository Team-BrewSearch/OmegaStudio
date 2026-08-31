using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// The Edit menu: undo and redo.
///
/// <para>
/// Every edit in the app goes through <see cref="RunCommandAsync"/>, so that is the
/// one place the history is written from — a command does not know it is being
/// recorded, and a plugin's own commands are undoable without their author doing
/// anything. What can be reversed and what cannot is <see cref="AssetSnapshot"/>'s
/// business; what happens to a reversal afterwards is
/// <see cref="ApplyChanges(TreeItemViewModel, IAssetNode, AssetChange)"/>'s, the same
/// refresh the original edit went through.
/// </para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>Longest command title the Edit menu spells out before eliding it.</summary>
    private const int MaxLabelLength = 34;

    private readonly EditHistory _history = new();

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>The Undo item's text, naming what it would reverse.</summary>
    public string UndoLabel => MenuLabel("_Undo", _history.UndoTitle);

    /// <summary>The Redo item's text, naming what it would reapply.</summary>
    public string RedoLabel => MenuLabel("_Redo", _history.RedoTitle);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_history.CanUndo)
        {
            AssetEdit edit = _history.Undo();
            Reverse(edit, edit.Before, "Undid");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_history.CanRedo)
        {
            AssetEdit edit = _history.Redo();
            Reverse(edit, edit.After, "Redid");
        }
    }

    /// <summary>
    /// Puts <paramref name="state"/> back, refreshes what the original command
    /// invalidated, and selects the row it happened to — an undo the user cannot see
    /// is indistinguishable from one that did nothing.
    /// </summary>
    private void Reverse(AssetEdit edit, AssetSnapshot state, string verb)
    {
        if (edit.Row.Node is not { } node)
        {
            ClearHistory();
            return;
        }

        bool restored = PluginGuard.Try(
            $"Reversing {edit.Title}",
            () => { state.Restore(node); return true; },
            false);

        if (!restored)
        {
            // The archive is no longer the one the rest of the history describes, so
            // none of it can be trusted either.
            ClearHistory();
            Log($"{edit.Title} could not be reversed; the edit history was cleared.");
            return;
        }

        ApplyChanges(edit.Row, node, edit.Changed);
        SelectedItem = edit.Row;

        HistoryChanged();
        Log($"{verb} {edit.Title}. Use File ▸ Save to write it back.");
    }

    /// <summary>
    /// Records an edit that has just been applied to <paramref name="row"/>.
    ///
    /// <para>
    /// Driven by the same <see cref="AssetChange"/> the refresh is: a command that
    /// reports no <see cref="AssetChange.Dirty"/> — every export — changed nothing to
    /// undo, and one that reports it on a node no snapshot can describe invalidates the
    /// whole history rather than leaving an Undo that would step over it.
    /// </para>
    /// </summary>
    /// <param name="title">The command's title, as the Edit menu will name it.</param>
    /// <param name="row">The row the command ran on.</param>
    /// <param name="before">The state captured before the command ran, if any.</param>
    /// <param name="changed">What the command reported changing.</param>
    private void RecordEdit(string title, TreeItemViewModel row, AssetSnapshot? before, AssetChange changed)
    {
        if (!changed.HasFlag(AssetChange.Dirty))
        {
            return;
        }

        // The history outlives the search filter, so it records the row in the tree rather than
        // the stand-in the results happened to be showing.
        row = row.Source ?? row;

        AssetSnapshot? after = AssetSnapshot.Capture(row.Node);

        // A snapshot of a different kind either side means the node changed what it can
        // restore — the bytes it would take back are not the ones it would give.
        if (before is null || after is null || before.GetType() != after.GetType())
        {
            ClearHistory();
            _console.Append($"{Describe(title)} cannot be undone; the edit history was cleared.");
            return;
        }

        if (before.SameAs(after))
        {
            return;
        }

        _history.Push(new AssetEdit(Describe(title), row, before, after, changed));
        HistoryChanged();
    }

    /// <summary>Drops the history, because it no longer describes what is open.</summary>
    private void ClearHistory()
    {
        _history.Clear();
        HistoryChanged();
    }

    /// <summary>
    /// Raises everything the Edit menu reads. The two commands and the four properties
    /// all answer from the same history, so they always change together.
    /// </summary>
    private void HistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }

    /// <summary>
    /// A command's title as a sentence refers to it: no trailing ellipsis, and without
    /// the parenthetical a replace command carries its accepted sizes in.
    /// </summary>
    private static string Describe(string title)
    {
        string text = title.Trim();

        int detail = text.IndexOf(" (", StringComparison.Ordinal);
        if (detail > 0)
        {
            text = text[..detail];
        }

        text = text.TrimEnd('…', '.', ' ');

        return text.Length <= MaxLabelLength ? text : text[..MaxLabelLength].TrimEnd() + "…";
    }

    /// <summary>
    /// One Edit menu item's text. The underscores in the title are doubled because a
    /// menu header reads a single one as the mnemonic marker, and a command called
    /// <c>Replace_Raw</c> would otherwise lose the underscore and claim the R key.
    /// </summary>
    private static string MenuLabel(string verb, string? title)
    {
        return title is null ? verb : $"{verb} {title.Replace("_", "__", StringComparison.Ordinal)}";
    }
}
