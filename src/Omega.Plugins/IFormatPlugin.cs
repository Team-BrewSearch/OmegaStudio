namespace Omega.Plugins;

/// <summary>Who a plugin is, shown in the Formats menu and used for diagnostics.</summary>
public sealed record FormatPluginInfo
{
    /// <summary>Stable machine identifier, e.g. <c>harugeki</c>. Must be unique across loaded plugins.</summary>
    public required string Id { get; init; }

    /// <summary>Human name, e.g. <c>Suzumiya Haruhi no Gekitou</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Plugin version, for the Formats menu. Free-form; <c>1.0.0</c> by convention.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Who wrote the plugin. Shown in the Formats menu.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>One line describing what this plugin reads.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The contract this plugin was compiled against. Leave it at the default so it
    /// tracks the assembly you built with; the host refuses a plugin whose value
    /// does not match its own <see cref="PluginContract.Version"/>.
    /// </summary>
    public int ContractVersion { get; init; } = PluginContract.Version;
}

/// <summary>
/// A format plugin: its identity, and the archive formats it can open.
///
/// <para>
/// Implementations need a public parameterless constructor and an
/// <see cref="OmegaPluginAttribute"/> on the assembly naming them. The host creates one
/// instance for the life of the process, so keep it cheap to construct and free of
/// per-file state — that belongs on <see cref="IArchive"/>.
/// </para>
///
/// <para>
/// Both collections are optional: a plugin usually opens a container, but one that
/// only recognises standard content implements <see cref="Recognizers"/> alone.
/// </para>
/// </summary>
public interface IFormatPlugin
{
    /// <summary>Who this plugin is. Shown in the Formats menu and used for diagnostics.</summary>
    FormatPluginInfo Info { get; }

    /// <summary>
    /// Every container format this plugin can open. Each appears as its own entry in
    /// the Open dialog's filter list. Empty for a plugin that only recognises content.
    /// </summary>
    IEnumerable<IArchiveFormat> Archives => [];

    /// <summary>
    /// Recognisers for standard payloads found inside any archive — see
    /// <see cref="IAssetRecognizer"/>. Empty for a plugin that only reads a container,
    /// which is most of them.
    /// </summary>
    IEnumerable<IAssetRecognizer> Recognizers => [];
}
