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
            MainWindow window = new();
            MainViewModel vm = new(new WindowStorageProvider(window.StorageProvider));
            string? log = FileLog.Open(LogDirectory());
            PluginGuard.Log = line =>
            {
                vm.Log(line);
                FileLog.Write(line);
            };
            if (log is not null)
            {
                vm.Log($"Logging to {log}.");
            }
            vm.RequestClose += () => _ = window.CloseAsync(vm);
            vm.DiscardConfirmation = () => window.AskAboutUnsavedChangesAsync(vm);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            CrashLog.Install(line =>
            {
                FileLog.Write(line);
                vm.Log(line);
            });
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
    private static string LogDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(root)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.Combine(root, "OmegaStudio", "logs");
    }
}
