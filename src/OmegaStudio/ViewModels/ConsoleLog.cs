using CommunityToolkit.Mvvm.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
namespace OmegaStudio.ViewModels;

public sealed partial class ConsoleLog : ObservableObject
{
    private const int MaxLines = 500;
    private readonly Queue<string> _lines = new(MaxLines);

    [AllowNull]
    public string Text
    {
        get => field ??= _lines.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, _lines) + Environment.NewLine; private set;
    }
    public string Last { get; private set; } = string.Empty;
    public void Append(string line)
    {
        _lines.Enqueue($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}");
        while (_lines.Count > MaxLines)
        {
            _ = _lines.Dequeue();
        }
        Last = line;
        Text = null;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }
    public void Clear()
    {
        _lines.Clear();
        Text = null;
        Last = string.Empty;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }
}
