namespace Kartz.Formats;

public sealed class DwkEntry
{
    private readonly Memory<byte> _source;
    private byte[]? _replacement;
    internal DwkEntry(int index, string gdfPath, Memory<byte> source, int gdaOffset = -1)
    {
        Index = index;
        GdfPath = gdfPath;
        _source = source;
        GdaOffset = gdaOffset;
    }
    internal DwkArchive? Owner { get; set; }
    public int Index { get; }
    public int GdaOffset { get; }
    public string GdfPath { get; }
    public string NormalizedPath => GdfPath.Replace('\\', '/');
    public string Name => NormalizedPath[(NormalizedPath.LastIndexOf('/') + 1)..];
    public bool IsDirty { get; private set; }
    public ReadOnlySpan<byte> Span => _replacement is not null ? _replacement.AsSpan() : _source.Span;
    public int Size => _replacement?.Length ?? _source.Length;
    public byte[] GetPayload()
    {
        return _replacement is not null ? [.. _replacement] : _source.ToArray();
    }
    internal byte[] DataForSave => _replacement ?? _source.ToArray();
    public void Replace(byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(newData);
        _replacement = newData;
        IsDirty = true;
        Owner?.MarkChanged();
    }
    public override string ToString()
    {
        return $"[{Index}] {GdfPath} ({Size} B)";
    }
}
