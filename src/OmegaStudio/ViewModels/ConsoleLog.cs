using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
namespace OmegaStudio.ViewModels;
public sealed partial class ConsoleLog : ObservableObject
{
    private const int MaxLines = 500;
    private readonly StringBuilder _joined = new();
    private readonly Queue<string> _lines = new(MaxLines);
    private readonly Queue<int> _lengths = new(MaxLines);
    private string _text = string.Empty;
    [AllowNull]
    public string Text => _text;
    public string Last { get; private set; } = string.Empty;
    public IReadOnlyList<string> Lines()
    {
        return [.. _lines];
    }
    public void Append(string line)
    {
        string stamped =
            $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}{Environment.NewLine}";
        _joined.Append(stamped);
        _lines.Enqueue(Stamp(stamped));
        _lengths.Enqueue(stamped.Length);
        while (_lines.Count > MaxLines)
        {
            _ = _lines.Dequeue();
            int dropped = _lengths.Dequeue();
            _joined.Remove(0, Math.Min(dropped, _joined.Length));
        }
        Last = line;
        _text = _joined.ToString();
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }
    private static string Stamp(string stamped)
    {
        int end = stamped.LastIndexOf(Environment.NewLine, StringComparison.Ordinal);
        return end < 0 ? stamped : stamped[..end];
    }
    public void Clear()
    {
        _lines.Clear();
        _lengths.Clear();
        _joined.Clear();
        Last = string.Empty;
        _text = string.Empty;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }
}
