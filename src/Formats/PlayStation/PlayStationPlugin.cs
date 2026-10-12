using Omega.Plugins;
using PlayStation.Formats;
[assembly: OmegaPlugin(typeof(PlayStationPlugin))]
namespace PlayStation.Formats;
public sealed class PlayStationPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "playstation",
        DisplayName = "PlayStation 3",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "PlayStation 3 game discs: the disc's ISO 9660 filesystem, read in place, "
            + "decrypted sector by sector when the image is keyed.",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } = [new Ps3DiscArchiveFormat()];
}
