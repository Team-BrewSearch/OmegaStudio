namespace Omega.Plugins;
/// <summary>One label-and-value line in the Properties pane.</summary>
/// <param name="Name">The label.</param>
/// <param name="Value">The value, already rendered as text.</param>
public sealed record PropertyRow(string Name, string Value)
{
    /// <summary>Builds a row, rendering the value with its own <c>ToString</c>.</summary>
    /// <param name="name">The label.</param>
    /// <param name="value">The value; null becomes an empty string.</param>
    /// <returns>The row.</returns>
    public static PropertyRow Of(string name, object? value)
    {
        return new PropertyRow(name, value?.ToString() ?? string.Empty);
    }
}
