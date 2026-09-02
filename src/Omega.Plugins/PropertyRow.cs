namespace Omega.Plugins;
/// <summary>How the Properties pane presents a row's value.</summary>
public enum PropertyEditor
{
    /// <summary>Text the user can read but not change.</summary>
    ReadOnly,
    /// <summary>A single-line text box.</summary>
    Text,
    /// <summary>A check box.</summary>
    Toggle,
    /// <summary>A drop-down limited to <see cref="PropertyRow.Choices"/>.</summary>
    Choice,
}
/// <summary>One label-and-value line in the Properties pane, editable when the row carries an <see cref="Apply"/>.</summary>
/// <param name="Name">The label.</param>
/// <param name="Value">The value, already rendered as text.</param>
public sealed record PropertyRow(string Name, string Value)
{
    /// <summary>Which control the pane shows for the value.</summary>
    public PropertyEditor Editor { get; init; } = PropertyEditor.ReadOnly;
    /// <summary>The values a <see cref="PropertyEditor.Choice"/> row accepts, in display order.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];
    /// <summary>Stores an edited value, or null for a row the user cannot change. Throw to reject the input; the host logs the message and restores the old value.</summary>
    public Action<string>? Apply { get; init; }
    /// <summary>What an accepted edit invalidated, so the host refreshes only those views.</summary>
    public AssetChange Changed { get; init; } = AssetChange.Properties | AssetChange.Dirty;
    /// <summary>Whether the pane lets the user edit this row.</summary>
    public bool IsEditable => Apply is not null && Editor != PropertyEditor.ReadOnly;
    /// <summary>Builds a read-only row, rendering the value with its own <c>ToString</c>.</summary>
    /// <param name="name">The label.</param>
    /// <param name="value">The value; null becomes an empty string.</param>
    /// <returns>The row.</returns>
    public static PropertyRow Of(string name, object? value)
    {
        return new PropertyRow(name, value?.ToString() ?? string.Empty);
    }
    /// <summary>Builds a row the user edits in a text box.</summary>
    /// <param name="name">The label.</param>
    /// <param name="value">The value as it stands now.</param>
    /// <param name="apply">Stores the edited text, throwing when it will not do.</param>
    /// <returns>The row.</returns>
    public static PropertyRow Editable(string name, string value, Action<string> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new PropertyRow(name, value) { Editor = PropertyEditor.Text, Apply = apply };
    }
    /// <summary>Builds a row the user edits with a check box.</summary>
    /// <param name="name">The label.</param>
    /// <param name="value">The value as it stands now.</param>
    /// <param name="apply">Stores the edited value.</param>
    /// <returns>The row.</returns>
    public static PropertyRow Switch(string name, bool value, Action<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new PropertyRow(name, value ? bool.TrueString : bool.FalseString)
        {
            Editor = PropertyEditor.Toggle,
            Apply = text => apply(bool.Parse(text)),
        };
    }
    /// <summary>Builds a row the user edits with a drop-down.</summary>
    /// <param name="name">The label.</param>
    /// <param name="value">The value as it stands now; it should be one of <paramref name="choices"/>.</param>
    /// <param name="choices">The values the drop-down offers, in display order.</param>
    /// <param name="apply">Stores the chosen value, throwing when it will not do.</param>
    /// <returns>The row.</returns>
    public static PropertyRow Options(
        string name, string value, IReadOnlyList<string> choices, Action<string> apply)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(apply);
        return new PropertyRow(name, value)
        {
            Editor = PropertyEditor.Choice,
            Choices = choices,
            Apply = apply,
        };
    }
}
