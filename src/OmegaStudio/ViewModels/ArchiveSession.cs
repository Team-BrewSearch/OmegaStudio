using Omega.Plugins;
using OmegaStudio.Plugins;

namespace OmegaStudio.ViewModels;

/// <summary>
/// Owns the open archive: choosing which plugin opens a file, tracking whether it
/// has edits, and writing it back behind a one-time backup.
///
/// <para>
/// The backup policy lives here rather than in the plugins, so every format gets the
/// same one. A plugin names the files its save will overwrite through
/// <see cref="IArchive.BackupPaths"/>; each is copied to <c>.bak</c> once per
/// session, and only if no backup is already sitting there, so repeated saves can
/// never overwrite the pristine copy with an edited one.
/// </para>
/// </summary>
internal sealed class ArchiveSession : IDisposable
{
    /// <summary>How much of a file a format is shown when asked whether it can open it.</summary>
    private const int SniffBytes = 4096;

    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Logged;

    /// <summary>The open archive, or null.</summary>
    public IArchive? Archive { get; private set; }

    /// <summary>Its path on disk.</summary>
    public string? Path { get; private set; }

    /// <summary>The plugin that opened it, for the status bar and diagnostics.</summary>
    public LoadedPlugin? Owner { get; private set; }

    public bool HasChanges => Archive is { IsDirty: true };

    /// <summary>
    /// Opens <paramref name="path"/> with the first format that claims it, off the UI
    /// thread. Returns null and logs if nothing could read it.
    /// </summary>
    public async Task<IArchive?> OpenAsync(
        string path, PluginCatalog catalog, AssetRecognition recognition)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(recognition);

        string name = System.IO.Path.GetFileName(path);

        byte[] head;
        try
        {
            head = await ReadHeadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logged?.Invoke($"Could not read {name}: {ex.Message}");
            return null;
        }

        (LoadedPlugin Owner, IArchiveFormat Format)? match = null;
        foreach ((LoadedPlugin owner, IArchiveFormat format) in catalog.Formats)
        {
            if (PluginGuard.CanOpen(format, path, head))
            {
                match = (owner, format);
                break;
            }
        }

        if (match is not { } chosen)
        {
            Logged?.Invoke(catalog.Plugins.Count == 0
                ? $"Cannot open {name}: no format plugins are loaded. See the Formats menu."
                : $"Cannot open {name}: no loaded format recognises it.");
            return null;
        }

        try
        {
            HostServices services = new(chosen.Owner, line => Logged?.Invoke(line), recognition);
            IArchive archive = await Task.Run(() => chosen.Format.Open(path, services));

            Close();
            Archive = archive;
            Path = path;
            Owner = chosen.Owner;

            Logged?.Invoke($"Opened {name} as {chosen.Format.DisplayName}.");
            return archive;
        }
        catch (Exception ex)
        {
            // A malformed file is the normal case here, not an exceptional one: the
            // plugin is expected to throw with a message worth showing.
            Logged?.Invoke($"Failed to open {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Closes whatever is open, disposing the plugin's archive object.</summary>
    public void Close()
    {
        if (Archive is { } archive)
        {
            PluginGuard.Try("Closing the archive", archive.Dispose);
        }

        Archive = null;
        Path = null;
        Owner = null;
        _backedUp.Clear();
    }

    /// <summary>Writes the archive back, behind a one-time backup of everything it touches.</summary>
    public async Task SaveAsync()
    {
        if (Archive is not { IsDirty: true } archive)
        {
            Logged?.Invoke("Nothing to save.");
            return;
        }

        try
        {
            await BackUpAsync(archive);
            await Task.Run(archive.Save);
            Logged?.Invoke($"Saved {System.IO.Path.GetFileName(Path ?? "archive")}.");
        }
        catch (Exception ex)
        {
            Logged?.Invoke($"Save failed: {ex.Message}");
        }
    }

    private async Task BackUpAsync(IArchive archive)
    {
        foreach (string target in archive.BackupPaths)
        {
            // File.Exists first: marking a path as backed up before knowing whether it was
            // there meant a file the plugin creates on its first save was recorded as protected
            // and then never copied on any later one.
            if (!File.Exists(target) || !_backedUp.Add(target))
            {
                continue;
            }

            string backup = target + ".bak";
            if (File.Exists(backup))
            {
                continue;
            }

            await Task.Run(() => File.Copy(target, backup));
            Logged?.Invoke($"Backed up to {System.IO.Path.GetFileName(backup)}.");
        }
    }

    private static async Task<byte[]> ReadHeadAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        byte[] buffer = new byte[(int)Math.Min(SniffBytes, file.Length)];
        int read = await file.ReadAsync(buffer);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public void Dispose()
    {
        Close();
    }
}
