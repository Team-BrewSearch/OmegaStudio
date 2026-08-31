using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            vm.RequestClose += window.Close;
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            if (Array.Find(desktop.Args ?? [], File.Exists) is { } path)
            {
                _ = vm.OpenPathAsync(path);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
