using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OmegaStudio.Diagnostics;
using OmegaStudio.Plugins;
using OmegaStudio.Services;
using OmegaStudio.ViewModels;
using OmegaStudio.Views;
namespace OmegaStudio;
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string? log = FileLog.Open(LogDirectory());
            MainWindow window = new();
            MainViewModel vm = new(new WindowStorageProvider(window.StorageProvider));
            if (log is not null)
            {
                vm.Log($"Logging to {log}.");
            }
            vm.RequestClose += window.Close;
            vm.DiscardConfirmation = () => window.AskAboutUnsavedChangesAsync(vm);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => vm.Dispose();
            CrashLog.Install(vm.Log, () => Context(vm));
            if (desktop.Args is { Length: > 0 } args)
            {
                string? named = Array.Find(args, File.Exists);
                if (named is null)
                {
                    string first = Array.Find(args, arg => arg.Length == 0 || arg[0] != '-') ?? args[0];
                    vm.Log($"'{first}' is not a file, so no archive was opened.");
                }
                else
                {
                    _ = vm.OpenPathAsync(named);
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static string Context(MainViewModel vm)
    {
        string archive = vm.Roots.Count == 0 ? "none" : $"'{vm.OpenArchiveName}'";
        string changes = vm.HasUnsavedChanges ? " with unsaved changes" : string.Empty;
        string selected = vm.SelectedItem?.Node is { } node ? $"'{NodeAccess.PathText(node)}'" : "none";
        return $"Open archive: {archive}{changes}; selected row: {selected}.";
    }
    private static string LogDirectory()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return string.IsNullOrEmpty(root)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.Combine(root, "OmegaStudio", "logs");
    }
}
