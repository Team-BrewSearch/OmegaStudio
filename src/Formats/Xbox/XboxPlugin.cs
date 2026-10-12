using Omega.Plugins;
using Xbox.Formats;
[assembly: OmegaPlugin(typeof(XboxPlugin))]
namespace Xbox.Formats;
public sealed class XboxPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "xbox",
        DisplayName = "Xbox 360",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "Xbox 360 game discs (XGD1, XGD2, XGD3, XISO): the game partition's "
            + "directory tree, walked as far as the volume's own tables.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new XgameDiscArchiveFormat()];
}
