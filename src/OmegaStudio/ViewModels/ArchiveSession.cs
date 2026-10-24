using Omega.Plugins;
using OmegaStudio.Plugins;
namespace OmegaStudio.ViewModels;
internal sealed class ArchiveSession : IDisposable
{
    private const int SniffBytes = 4096;
    private readonly HashSet<string> _backedUp = new(AtomicSave.PathComparer);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;
    public event Action<string>? Logged;
    public int Generation { get; private set; }
    public bool IsBusy => _operations.CurrentCount == 0;
    public async Task<T> RunAsync<T>(int generation, Func<IArchive, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _operations.WaitAsync();
        try
        {
            if (generation != Generation || Archive is not { } archive)
            {
                throw new ArchiveClosedException();
            }
            return await Task.Run(() => work(archive));
        }
        finally
        {
            _operations.Release();
        }
    }
    public async Task CloseAsync()
    {
        await _operations.WaitAsync();
        try
        {
            Close();
        }
        finally
        {
            _operations.Release();
        }
    }
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
            if (_disposed)
            {
                PluginGuard.Try("Closing the archive", archive.Dispose);
                return null;
            }
            await _operations.WaitAsync();
            try
            {
                Close();
            }
            finally
            {
                _operations.Release();
            }
            Archive = archive;
            Path = path;
            Owner = chosen.Owner;
            Pending = pending;
            pending.Open();
            Logged?.Invoke($"Opened {name} as {chosen.Format.DisplayName}.");
            return archive;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logged?.Invoke($"Failed to open {name}: {ex.Message}");
            return null;
        }
    }
    private void Close()
    {
        Generation++;
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
    public async Task<bool> SaveAsync()
    {
        await _operations.WaitAsync();
        try
        {
            return await SaveLockedAsync();
        }
        finally
        {
            _operations.Release();
        }
    }
    private async Task<bool> SaveLockedAsync()
    {
        if (Archive is not { } archive || !HasChanges)
        {
            Logged?.Invoke("Nothing to save.");
            return true;
        }
        string name = System.IO.Path.GetFileName(Path ?? "The archive");
        if (Pending is { Any: true } pending)
        {
            try
            {
                await Task.Run(pending.Flush);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Logged?.Invoke($"Save failed: an edited container could not be written back into {name}: {ex.GetType().Name}: {ex.Message}");
                Logged?.Invoke($"{name} may be partly written, and its edits are kept: save again, or restore it from the .bak beside it.");
                return false;
            }
        }
        try
        {
            await BackUpAsync(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logged?.Invoke($"Save failed: the backup of {name} could not be made ({ex.Message}), so nothing was written.");
            return false;
        }
        try
        {
            await Task.Run(archive.Save);
            Logged?.Invoke($"Saved {name}.");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logged?.Invoke($"Save failed: {ex.GetType().Name}: {ex.Message}");
            Logged?.Invoke(ex.StackTrace is { Length: > 0 } trace ? $"  {trace}" : string.Empty);
            Logged?.Invoke(
                $"{name} may be partly written, and its edits are kept. The .bak beside it holds the file as it was "
                + "before Omega Studio first saved over it: save again, or close this one and restore from that.");
            return false;
        }
    }
    private async Task BackUpAsync(IArchive archive)
    {
        foreach (string target in archive.BackupPaths)
        {
            if (!File.Exists(target) || _backedUp.Contains(target))
            {
                continue;
            }
            string backup = target + ".bak";
            if (File.Exists(backup))
            {
                Logged?.Invoke($"Kept the existing {System.IO.Path.GetFileName(backup)} as the backup.");
            }
            else
            {
                await Task.Run(() => CopyBackup(target, backup));
                Logged?.Invoke($"Backed up to {System.IO.Path.GetFileName(backup)}.");
            }
            _backedUp.Add(target);
        }
    }
    internal static void CopyBackup(string target, string backup)
    {
        string temporary = AtomicSave.TempPath(backup);
        try
        {
            File.Copy(target, temporary);
            using (FileStream flush = File.Open(temporary, FileMode.Open, FileAccess.ReadWrite))
            {
                if (flush.Length != new FileInfo(target).Length)
                {
                    throw new IOException("the backup came out a different length from the file it copies");
                }
                flush.Flush(flushToDisk: true);
            }
            File.Move(temporary, backup, overwrite: false);
        }
        finally
        {
            AtomicSave.TryDelete(temporary);
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
        _disposed = true;
        Close();
    }
}
internal sealed class ArchiveClosedException : InvalidOperationException
{
    public ArchiveClosedException()
        : base("the archive was closed before this finished")
    {
    }
}
