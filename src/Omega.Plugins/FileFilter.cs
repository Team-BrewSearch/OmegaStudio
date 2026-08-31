namespace Omega.Plugins;
/// <summary>A named set of filename patterns, as a file dialog presents it.</summary>
public sealed class FileFilter
{
    /// <summary>Creates a filter.</summary>
    /// <param name="label">Text shown for this entry in the dialog&apos;s type list.</param>
    /// <param name="patterns">Glob patterns such as <c>*.bin</c>. A wildcard is used when none are given.</param>
    public FileFilter(string label, params string[] patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(patterns);
        Label = label;
        Patterns = patterns.Length > 0 ? patterns : ["*.*"];
    }
    /// <summary>Text shown for this entry in the dialog&apos;s type list.</summary>
    public string Label { get; }
    /// <summary>Patterns this filter accepts. Never empty.</summary>
    public IReadOnlyList<string> Patterns { get; }
    /// <summary>Extension to suggest when saving, taken from the first pattern. A wildcard pattern yields <c>.dat</c>.</summary>
    public string PrimaryExtension
    {
        get
        {
            string first = Patterns[0];
            int dot = first.LastIndexOf('.');
            if (dot < 0 || dot + 1 >= first.Length)
            {
                return ".dat";
            }
            string extension = first[dot..];
            return extension == ".*" ? ".dat" : extension;
        }
    }
    /// <summary>The label and its patterns, for diagnostics.</summary>
    public override string ToString()
    {
        return $"{Label} ({string.Join(", ", Patterns)})";
    }
}
