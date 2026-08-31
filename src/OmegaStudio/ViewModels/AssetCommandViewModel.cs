using CommunityToolkit.Mvvm.Input;
using Omega.Plugins;
using System.Windows.Input;

namespace OmegaStudio.ViewModels;

/// <summary>
/// One entry in the Outliner's context menu.
///
/// <para>
/// A thin wrapper: the menu binds to a list of these and the work stays in
/// <see cref="MainViewModel.RunCommandAsync"/>, which owns the file picker, the
/// background thread and the refresh that follows. The alternative — a named
/// <c>[RelayCommand]</c> per operation — is what made adding an operation a
/// three-file change.
/// </para>
/// </summary>
public sealed class AssetCommandViewModel
{
    internal AssetCommandViewModel(AssetCommand command, MainViewModel owner)
    {
        Title = command.Title;
        Command = new AsyncRelayCommand(() => owner.RunCommandAsync(command));
    }

    /// <summary>The menu text.</summary>
    public string Title { get; }

    public ICommand Command { get; }
}
