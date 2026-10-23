using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal sealed class ArchiveSession : IDisposable
{
    private const int SniffBytes = 4096;
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Logged;
    public IArchive? Archive { get; private set; }
    public string? Path { get; private set; }
    public LoadedPlugin? Owner { get; private set; }
    public string? DisplayName => Archive?.DisplayName;
    public PendingFlushes? Pending { get; private set; }
    public bool HasChanges => Archive is { IsDirty: true } || Pending is { Any: true };
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
            PendingFlushes pending = new();
            HostServices services = new(chosen.Owner, line => Logged?.Invoke(line), recognition, pending);
            IArchive archive = await Task.Run(() => chosen.Format.Open(path, services));
            Close();
            Archive = archive;
            Path = path;
            Owner = chosen.Owner;
            Pending = pending;
            pending.Open();
            Logged?.Invoke($"Opened {name} as {chosen.Format.DisplayName}.");
            return archive;
        }
        catch (Exception ex)
        {
            Logged?.Invoke($"Failed to open {name}: {ex.Message}");
            return null;
        }
    }
    public void Close()
    {
        Pending?.Close();
        Pending = null;
        if (Archive is { } archive)
        {
            PluginGuard.Try("Closing the archive", archive.Dispose);
        }
        Archive = null;
        Path = null;
        Owner = null;
        _backedUp.Clear();
    }
    public async Task SaveAsync()
    {
        if (Archive is not { } archive || !HasChanges)
        {
            Logged?.Invoke("Nothing to save.");
            return;
        }
        string name = System.IO.Path.GetFileName(Path ?? "The archive");
        if (Pending is { Any: true } pending)
        {
            try
            {
                await Task.Run(pending.Flush);
            }
            catch (Exception ex)
            {
                Logged?.Invoke($"Save failed: an edited container could not be written back into {name}: {ex.GetType().Name}: {ex.Message}");
                Logged?.Invoke($"{name} was not written and its edits are kept: change them and save again, or close without saving.");
                return;
            }
        }
        try
        {
            await BackUpAsync(archive);
            await Task.Run(archive.Save);
            Logged?.Invoke($"Saved {System.IO.Path.GetFileName(Path ?? "archive")}.");
        }
        catch (Exception ex)
        {
            Logged?.Invoke($"Save failed: {ex.GetType().Name}: {ex.Message}");
            Logged?.Invoke(ex.StackTrace is { Length: > 0 } trace ? $"  {trace}" : string.Empty);
            Logged?.Invoke(
                $"{System.IO.Path.GetFileName(Path ?? "The archive")} was not written. Any .bak beside it holds the file "
                + "as it was before Omega Studio first saved over it, so close this one and start again from that if needed.");
        }
    }
    private async Task BackUpAsync(IArchive archive)
    {
        foreach (string target in archive.BackupPaths)
        {
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
