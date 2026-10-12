using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
namespace OmegaStudio.ViewModels;
public sealed class AssetCommandViewModel
{
    internal AssetCommandViewModel(AssetCommand command, ICommandRunner owner)
    {
        Title = command.Title;
        Command = new AsyncRelayCommand(() => owner.RunCommandAsync(command));
    }
    public string Title { get; }
    public ICommand Command { get; }
}
