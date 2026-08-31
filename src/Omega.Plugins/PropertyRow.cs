namespace Omega.Plugins;

public sealed record PropertyRow(string Name, string Value)
{
    public static PropertyRow Of(string name, object? value)
    {
        return new PropertyRow(name, value?.ToString() ?? string.Empty);
    }
}
