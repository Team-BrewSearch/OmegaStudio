namespace Omega.Plugins;

/// <summary>
/// A file-picker filter: a label and the glob patterns it accepts.
/// </summary>
/// <example>
/// <code>
/// new FileFilter("Harugeki archive", "*.bin")
/// new FileFilter("glTF model", "*.gltf", "*.glb")
/// </code>
/// </example>
public sealed class FileFilter
{
    /// <param name="label">Shown in the picker's type dropdown.</param>
    /// <param name="patterns">Globs such as <c>*.bin</c>. Defaults to <c>*.*</c> if none are given.</param>
    public FileFilter(string label, params string[] patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(patterns);

        Label = label;
        Patterns = patterns.Length > 0 ? patterns : ["*.*"];
    }

    /// <summary>Shown in the picker's type dropdown, e.g. <c>Harugeki archive</c>.</summary>
    public string Label { get; }

    /// <summary>Glob patterns, e.g. <c>*.bin</c>. Never empty.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>
    /// The primary extension including the dot, e.g. <c>.bin</c>. Falls back to
    /// <c>.dat</c> for a wildcard pattern, so a suggested filename always has one.
    /// </summary>
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
