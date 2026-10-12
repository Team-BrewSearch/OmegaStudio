using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace OmegaStudio.Views;
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = $"About {AppInfo.Name}";
        Set("ProductText", AppInfo.Name);
        Set("SummaryText", AppInfo.Summary);
        Set("VersionText", $"Version {AppInfo.Version}");
    }
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    private void Set(string name, string text)
    {
        TextBlock? target = this.FindControl<TextBlock>(name);
        target?.Text = text;
    }
    private void Ok_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
