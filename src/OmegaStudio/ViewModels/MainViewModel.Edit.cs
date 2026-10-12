using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
public partial class MainViewModel
{
    private const int MaxLabelLength = 34;
    private readonly EditHistory _history = new();
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public string UndoLabel => MenuLabel("_Undo", _history.UndoTitle);
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
            ClearHistory();
            Log($"{edit.Title} could not be reversed; the edit history was cleared.");
            return;
        }
        ApplyChanges(edit.Row, node, edit.Changed);
        SelectedItem = edit.Row;
        HistoryChanged();
        Log($"{verb} {edit.Title}. Use File ▸ Save to write it back.");
    }
    private void RecordEdit(string title, TreeItemViewModel row, AssetSnapshot? before, AssetChange changed)
    {
        if (!changed.HasFlag(AssetChange.Dirty))
        {
            return;
        }
        row = row.Source ?? row;
        AssetSnapshot? after = AssetSnapshot.Capture(row.Node);
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
        if (!_history.Push(new AssetEdit(Describe(title), row, before, after, changed)))
        {
            _console.Append($"{Describe(title)} is too large for the edit history; it cannot be undone.");
            return;
        }
        HistoryChanged();
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
            _console.Append($"{Describe($"Set {row.Name}")} is too large for the edit history; it cannot be undone.");
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
