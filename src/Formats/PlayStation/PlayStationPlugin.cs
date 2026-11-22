using Omega.Plugins;
using PlayStation.Formats;
[assembly: OmegaPlugin(typeof(PlayStationPlugin))]
namespace PlayStation.Formats;
public sealed class PlayStationPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "playstation",
        DisplayName = "PlayStation",
        Version = "1.0.0",
        Author = "Omega Studio",
        Description = "PlayStation disc images: PS3 (encrypted ISO), PS2/PS1 (ISO 9660), PSP UMD (ISO 9660), PSP CSO (compressed ISO).",
    };
    public IEnumerable<IArchiveFormat> Archives { get; } =
    [
        new Ps3DiscArchiveFormat(),
        new Ps2DiscArchiveFormat(),
        new PspIsoArchiveFormat(),
        new PspCsoArchiveFormat(),
    ];
}
