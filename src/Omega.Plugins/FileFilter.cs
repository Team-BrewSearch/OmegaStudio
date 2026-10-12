namespace Omega.Plugins;
public sealed class FileFilter
{
    public FileFilter(string label, params string[] patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(patterns);
        Label = label;
        Patterns = patterns.Length > 0 ? patterns : ["*.*"];
    }
    public string Label { get; }
    public IReadOnlyList<string> Patterns { get; }
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
    public override string ToString()
    {
        return $"{Label} ({string.Join(", ", Patterns)})";
    }
}
