using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OmegaStudio.Views;

/// <summary>
/// The About box: what this app is, and which build is running.
///
/// <para>
/// Filled in from <see cref="AppInfo"/> in code rather than bound, because the text is
/// the assembly's own metadata rather than anything a view model owns — and a dialog
/// with a view model of its own would be two files to say one number.
/// </para>
/// </summary>
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
        if (target is not null)
        {
            target.Text = text;
        }
    }

    private void Ok_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
