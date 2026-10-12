using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
namespace OmegaStudio.Views;
public enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save,
}
public partial class UnsavedChangesWindow : Window
{
    private UnsavedChangesChoice _choice = UnsavedChangesChoice.Cancel;
    public UnsavedChangesWindow()
    {
        InitializeComponent();
    }
    public UnsavedChangesChoice Choice => _choice;
    public async Task<UnsavedChangesChoice> AskAsync(Window owner, string archive)
    {
        TextBlock? prompt = this.FindControl<TextBlock>("PromptText");
        if (prompt is not null)
        {
            prompt.Text = $"{archive} has changes that have not been written back. Save them first?";
        }
        _ = await ShowDialog<bool>(owner);
        return _choice;
    }
    private void Save_OnClick(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.Save);
    private void Discard_OnClick(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.Discard);
    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Choose(UnsavedChangesChoice.Cancel);
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
    private void Choose(UnsavedChangesChoice choice)
    {
        _choice = choice;
        Close();
    }
}
