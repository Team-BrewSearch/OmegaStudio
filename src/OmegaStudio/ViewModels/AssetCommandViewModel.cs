using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using System.Windows.Input;
namespace OmegaStudio.ViewModels;

public sealed class AssetCommandViewModel
{
    internal AssetCommandViewModel(AssetCommand command, MainViewModel owner)
    {
        Title = command.Title;
        Command = new AsyncRelayCommand(() => owner.RunCommandAsync(command));
    }
    public string Title { get; }
    public ICommand Command { get; }
}
