using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OmegaStudio.Diagnostics;
namespace OmegaStudio.ViewModels;
public sealed partial class ConsoleLog : ObservableObject
{
    private const int MaxLines = 500;
    private readonly Queue<string> _lines = new(MaxLines + 1);
    private bool _publishing;
    public string Text { get; private set; } = string.Empty;
    public void Append(string line)
    {
        FileLog.Write(line);
        _lines.Enqueue($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}{Environment.NewLine}");
        if (_lines.Count > MaxLines)
        {
            _ = _lines.Dequeue();
        }
        if (!_publishing)
        {
            _publishing = true;
            Dispatcher.UIThread.Post(Publish, DispatcherPriority.Background);
        }
    }
    private void Publish()
    {
        _publishing = false;
        Text = string.Concat(_lines);
        OnPropertyChanged(nameof(Text));
    }
}
