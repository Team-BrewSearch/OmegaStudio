using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

namespace OmegaStudio.ViewModels;

/// <summary>
/// The timestamped transcript shown in the Console pane, capped so a long session
/// cannot grow without bound.
/// </summary>
public sealed partial class ConsoleLog : ObservableObject
{
    private const int MaxLines = 500;

    private readonly Queue<string> _lines = new(MaxLines);

    /// <summary>The joined transcript, dropped whenever a line arrives.</summary>
    private string? _joined;

    /// <summary>
    /// The transcript, joined on demand and cached until the next line.
    ///
    /// <para>
    /// A queue of lines rather than one growing buffer. Trimming a StringBuilder meant removing
    /// from the front, which copies everything behind it, and the whole transcript was turned
    /// into a fresh string on every read — and it is read once per line, because that is what
    /// the binding does when the property changes.
    /// </para>
    /// </summary>
    public string Text => _joined ??= _lines.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, _lines) + Environment.NewLine;

    /// <summary>The most recent line, for the status bar.</summary>
    public string Last { get; private set; } = string.Empty;

    public void Append(string line)
    {
        _lines.Enqueue($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}");

        while (_lines.Count > MaxLines)
        {
            _ = _lines.Dequeue();
        }

        Last = line;
        _joined = null;

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }

    public void Clear()
    {
        _lines.Clear();
        _joined = null;
        Last = string.Empty;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Last));
    }
}
