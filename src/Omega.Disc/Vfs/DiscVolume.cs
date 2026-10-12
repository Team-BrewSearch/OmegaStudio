namespace Omega.Disc.Vfs
{
    public enum EditKind
    {
        None = 0,
        Inline,
        Applied,
        Overlay,
    }
    public sealed record DiscEditState(
        EditKind Kind,
        byte[]? Payload = null,
        string? SourcePath = null,
        long Length = 0,
        DateTime SourceTimestamp = default)
    {
        public static readonly DiscEditState None = new(EditKind.None);
        public static DiscEditState InMemory(byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(payload);
            return new DiscEditState(EditKind.Inline, Payload: payload);
        }
        public static DiscEditState Patched()
        {
            return new DiscEditState(EditKind.Applied);
        }
        public static DiscEditState Deferred(string sourcePath, long length, DateTime timestamp)
        {
            ArgumentException.ThrowIfNullOrEmpty(sourcePath);
            return new DiscEditState(
                EditKind.Overlay, SourcePath: sourcePath, Length: length, SourceTimestamp: timestamp);
        }
    }
    public static class DiscEdit
    {
        public const long InlineLimit = 32 * 1024 * 1024;
    }
    public abstract class DiscEntry(DiscVolume volume, string path)
    {
        public DiscVolume Volume { get; } = volume ?? throw new ArgumentNullException(nameof(volume));
        public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
        public abstract bool IsDirectory { get; }
        public abstract long Length { get; }
        public abstract DateTime? Timestamp { get; }
        public abstract DiscEditState EditState { get; }
        public abstract bool CanReplace(long newLength);
        public abstract Stream OpenRead();
        public abstract void Replace(byte[]? payload, string? source);
        protected static byte[] ReadAll(Stream stream, string what)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (stream.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"'{what}' holds {stream.Length} bytes, more than this host can read into one node");
            }
            byte[] buffer = new byte[stream.Length];
            stream.Position = 0;
            stream.ReadExactly(buffer);
            return buffer;
        }
    }
    public abstract class DiscVolume : IDisposable
    {
        public abstract string FormatName { get; }
        public abstract string? Label { get; }
        public abstract DiscEntry Root { get; }
        public abstract DiscEntry? Find(string path);
        public abstract bool CanRebuild { get; }
        public abstract bool TryRebuild(RebuildContext context, out string? reason);
        public abstract bool IsDirty { get; }
        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
        }
    }
    public sealed class RebuildContext
    {
        public RebuildContext(
            string sourcePath,
            Func<string, Stream> openEntry,
            Action<string> log)
        {
            SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
            OpenEntry = openEntry ?? throw new ArgumentNullException(nameof(openEntry));
            Log = log ?? throw new ArgumentNullException(nameof(log));
        }
        public string SourcePath { get; }
        public Func<string, Stream> OpenEntry { get; }
        public Action<string> Log { get; }
        public string TemporaryPath => SourcePath + ".tmp";
    }
}
