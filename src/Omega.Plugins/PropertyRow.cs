namespace Omega.Plugins;
public enum PropertyEditor
{
    ReadOnly,
    Text,
    Toggle,
    Choice,
}
public sealed record PropertyRow(string Name, string Value)
{
    public PropertyEditor Editor { get; init; } = PropertyEditor.ReadOnly;
    public IReadOnlyList<string> Choices { get; init; } = [];
    public Action<string>? Apply { get; init; }
    public AssetChange Changed { get; init; } = AssetChange.Properties | AssetChange.Dirty;
    public bool IsEditable => Apply is not null && Editor != PropertyEditor.ReadOnly;
    public static PropertyRow Of(string name, object? value)
    {
        return new PropertyRow(name, value?.ToString() ?? string.Empty);
    }
    public static PropertyRow Editable(string name, string value, Action<string> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new PropertyRow(name, value) { Editor = PropertyEditor.Text, Apply = apply };
    }
    public static PropertyRow Switch(string name, bool value, Action<bool> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        return new PropertyRow(name, value ? bool.TrueString : bool.FalseString)
        {
            Editor = PropertyEditor.Toggle,
            Apply = text => apply(bool.Parse(text)),
        };
    }
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
