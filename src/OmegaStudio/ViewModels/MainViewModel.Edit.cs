using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private const int MaxLabelLength = 34;
    private const AssetChange Everything = AssetChange.Bytes | AssetChange.Scene | AssetChange.Image
        | AssetChange.Children | AssetChange.Properties | AssetChange.Dirty;
    private readonly EditHistory _history = new();
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public string UndoLabel => MenuLabel("_Undo", _history.UndoTitle);
    public string RedoLabel => MenuLabel("_Redo", _history.RedoTitle);
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_history.CanUndo)
        {
            AssetEdit edit = _history.Undo();
            await ReverseAsync(edit, edit.Before, "Undid");
        }
    }
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (_history.CanRedo)
        {
            AssetEdit edit = _history.Redo();
            await ReverseAsync(edit, edit.After, "Redid");
        }
    }
    private async Task ReverseAsync(AssetEdit edit, AssetSnapshot state, string verb)
    {
        if (edit.Row.Node is not { } node)
        {
            ClearHistory();
            return;
        }
        HistoryChanged();
        bool Restore() => PluginGuard.Try($"Reversing {edit.Title}", () => { state.Restore(node); return true; }, false);
        bool restored;
        if (state.RestoresOnUiThread)
        {
            restored = !_session.IsBusy && Restore();
        }
        else
        {
            try
            {
                restored = await _session.RunAsync(_session.Generation, _ => Restore());
            }
            catch (ArchiveClosedException)
            {
                return;
            }
        }
        if (!restored)
        {
            ClearHistory();
            Log($"{edit.Title} could not be reversed; the edit history was cleared.");
            return;
        }
        ApplyChanges(edit.Row, node, edit.Changed);
        SelectedItem = edit.Row;
        HistoryChanged();
        Log($"{verb} {edit.Title}. Use File ▸ Save to write it back.");
    }
    private void PublishEditOutcome(
        string title,
        string fullTitle,
        TreeItemViewModel row,
        TreeItemViewModel target,
        IAssetNode node,
        AssetChange changed,
        EditOutcome outcome)
    {
        if (outcome.TooLarge)
        {
            Log($"{title} was refused: {NodeAccess.Name(node)} is too large to snapshot, so the edit could not be undone. Nothing was changed.");
            return;
        }
        if (outcome.Error is { } error)
        {
            Log($"{title} failed: {error.Message}");
            if (outcome.ReadOnly || outcome.Unchanged)
            {
                return;
            }
            ApplyChanges(row, node, Everything);
            if (outcome.Restored)
            {
                Log($"Rolled back {Describe(fullTitle)}, so {NodeAccess.Name(node)} is as it was before.");
                return;
            }
            ClearHistory();
            Log($"{Describe(fullTitle)} may have changed {NodeAccess.Name(node)} before it failed and cannot be undone; the edit history was cleared.");
            return;
        }
        ApplyChanges(row, node, changed);
        if (outcome.ReadOnly || !changed.HasFlag(AssetChange.Dirty) || outcome.Unchanged)
        {
            return;
        }
        if (outcome.Before is not { } before || outcome.After is not { } after
            || !_history.Push(new AssetEdit(Describe(fullTitle), target, before, after, changed)))
        {
            ClearHistory();
            _console.Append($"{Describe(fullTitle)} cannot be undone; the edit history was cleared.");
            return;
        }
        HistoryChanged();
    }
    private sealed record EditOutcome(
        Exception? Error,
        AssetSnapshot? Before,
        AssetSnapshot? After,
        bool ReadOnly,
        bool TooLarge,
        bool Restored,
        bool Unchanged)
    {
        public static EditOutcome Run(AssetCommand command, AssetCommandContext context, PendingFlushes? pending, bool readOnly)
        {
            PendingFlushes.Use(pending);
            IAssetNode node = context.Node;
            AssetSnapshot? before = null;
            if (!readOnly)
            {
                before = AssetSnapshot.Capture(node, out bool tooLarge);
                if (tooLarge)
                {
                    return new EditOutcome(null, null, null, readOnly, true, false, true);
                }
            }
            try
            {
                command.Execute(context);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (readOnly)
                {
                    return new EditOutcome(ex, null, null, readOnly, false, false, true);
                }
                if (before is not null && AssetSnapshot.Capture(node) is { } now && before.SameAs(now))
                {
                    return new EditOutcome(ex, before, null, readOnly, false, true, true);
                }
                bool restored = before is not null && PluginGuard.Try(
                    $"Rolling back {Describe(command.Title)}",
                    () => { before.Restore(node); return true; },
                    false);
                return new EditOutcome(ex, before, null, readOnly, false, restored, false);
            }
            if (readOnly || !context.Changed.HasFlag(AssetChange.Dirty))
            {
                return new EditOutcome(null, before, null, readOnly, false, false, false);
            }
            AssetSnapshot? after = AssetSnapshot.Capture(node);
            bool comparable = before is not null && after is not null && before.GetType() == after.GetType();
            bool unchanged = comparable && before!.SameAs(after!);
            return new EditOutcome(null, comparable ? before : null, comparable ? after : null, readOnly, false, false, unchanged);
        }
    }
    private void RecordPropertyEdit(PropertyRow row, TreeItemViewModel item, string before, string after)
    {
        if (row.Apply is not { } apply || !row.Changed.HasFlag(AssetChange.Dirty) || before == after)
        {
            return;
        }
        if (!_history.Push(new AssetEdit(
            Describe($"Set {row.Name}"),
            item.Source ?? item,
            AssetSnapshot.ForProperty(apply, before),
            AssetSnapshot.ForProperty(apply, after),
            row.Changed | AssetChange.Properties)))
        {
            ClearHistory();
            _console.Append($"{Describe($"Set {row.Name}")} cannot be undone; the edit history was cleared.");
            return;
        }
        HistoryChanged();
    }
    private void ClearHistory()
    {
        _history.Clear();
        HistoryChanged();
    }
    private void HistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
    }
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
    private static string MenuLabel(string verb, string? title)
    {
        return title is null ? verb : $"{verb} {title.Replace("_", "__", StringComparison.Ordinal)}";
    }
}
