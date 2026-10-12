using Omega.Plugins;
namespace OmegaStudio.ViewModels;
public abstract class PropertyItem
{
    private readonly Action<PropertyRow, string>? _commit;
    protected PropertyItem(PropertyRow row, Action<PropertyRow, string>? commit)
    {
        Row = row;
        _commit = commit;
    }
    public PropertyRow Row { get; }
    public string Name => Row.Name;
    protected void Commit(string value)
    {
        _commit?.Invoke(Row, value);
    }
    public static PropertyItem For(PropertyRow row, Action<PropertyRow, string>? commit)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsEditable || commit is null
            ? new ReadOnlyPropertyItem(row)
            : row.Editor switch
            {
                PropertyEditor.Text => new TextPropertyItem(row, commit),
                PropertyEditor.Toggle => new TogglePropertyItem(row, commit),
                PropertyEditor.Choice => new ChoicePropertyItem(row, commit),
                _ => new ReadOnlyPropertyItem(row),
            };
    }
}
public sealed class ReadOnlyPropertyItem : PropertyItem
{
    internal ReadOnlyPropertyItem(PropertyRow row)
        : base(row, null)
    {
    }
    public string Value => Row.Value;
}
public sealed class TextPropertyItem : PropertyItem
{
    private string _value;
    internal TextPropertyItem(PropertyRow row, Action<PropertyRow, string> commit)
        : base(row, commit)
    {
        _value = row.Value;
    }
    public string Value
    {
        get => _value;
        set
        {
            string text = value ?? string.Empty;
            if (_value == text)
            {
                return;
            }
            _value = text;
            Commit(text);
        }
    }
}
public sealed class TogglePropertyItem : PropertyItem
{
    private bool _isChecked;
    internal TogglePropertyItem(PropertyRow row, Action<PropertyRow, string> commit)
        : base(row, commit)
    {
        _isChecked = bool.TryParse(row.Value, out bool parsed) && parsed;
    }
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }
            _isChecked = value;
            Commit(value ? bool.TrueString : bool.FalseString);
        }
    }
}
public sealed class ChoicePropertyItem : PropertyItem
{
    private string _selected;
    internal ChoicePropertyItem(PropertyRow row, Action<PropertyRow, string> commit)
        : base(row, commit)
    {
        Choices = [.. row.Choices];
        _selected = row.Value;
    }
    public IReadOnlyList<string> Choices { get; }
    public string Selected
    {
        get => _selected;
        set
        {
            if (value is null || _selected == value)
            {
                return;
            }
            _selected = value;
            Commit(value);
        }
    }
}
