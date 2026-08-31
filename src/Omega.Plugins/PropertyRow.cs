namespace Omega.Plugins;

/// <summary>One name/value line in the Properties pane.</summary>
public sealed record PropertyRow(string Name, string Value)
{
    /// <summary>
    /// Formats <paramref name="value"/> with <see cref="object.ToString"/>, so a row
    /// can be written from a number or a count without ceremony at the call site.
    /// </summary>
    public static PropertyRow Of(string name, object? value)
    {
        return new PropertyRow(name, value?.ToString() ?? string.Empty);
    }
}
