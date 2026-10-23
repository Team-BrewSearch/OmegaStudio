using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
namespace OmegaStudio.Views;
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Highlight));
    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }
    protected override Type StyleKeyOverride => typeof(TextBlock);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HighlightProperty || (change.Property == TextProperty && Highlight is not null))
        {
            Inlines = Mark(Text, Highlight);
        }
    }
    private static InlineCollection? Mark(string? text, string? highlight)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(highlight))
        {
            return null;
        }
        InlineCollection runs = [];
        int start = 0;
        int match;
        while ((match = text.IndexOf(highlight, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (match > start)
            {
                runs.Add(new Run(text[start..match]));
            }
            runs.Add(new Run(text.Substring(match, highlight.Length)) { Classes = { "hit" } });
            start = match + highlight.Length;
        }
        if (runs.Count == 0)
        {
            return null;
        }
        if (start < text.Length)
        {
            runs.Add(new Run(text[start..]));
        }
        return runs;
    }
}
