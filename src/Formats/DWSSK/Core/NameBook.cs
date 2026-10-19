using System.Text;
namespace DWSSK.Formats.Core;
internal static class NameBook
{
    private static readonly Lazy<Dictionary<string, List<string>>> s_sections = new(Load);
    public static IReadOnlyList<string> Section(string name)
    {
        return s_sections.Value.TryGetValue(name, out List<string>? list) ? list : [];
    }
    private static Dictionary<string, List<string>> Load()
    {
        Dictionary<string, List<string>> sections = [];
        using Stream? stream = typeof(NameBook).Assembly.GetManifestResourceStream("DWSSK.Names.txt");
        if (stream is null)
        {
            return sections;
        }
        using StreamReader reader = new(stream, Encoding.Latin1);
        List<string> current = [];
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sections[line[1..^1]] = current = [];
            }
            else if (line.Length > 0)
            {
                current.Add(line);
            }
        }
        return sections;
    }
}
