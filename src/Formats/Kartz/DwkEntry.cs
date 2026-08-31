namespace Kartz.Formats;

/// <summary>One file entry from a GDA/GDF/GDT archive triple.</summary>
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

    public int Index { get; }

    /// <summary>Byte offset of this entry in the GDA blob (-1 if unknown or empty).</summary>
    public int GdaOffset { get; }

    /// <summary>Path as stored in the GDF (backslash-separated).</summary>
    public string GdfPath { get; }

    /// <summary>
    /// <see cref="GdfPath"/> with forward slashes, for comparing and splitting.
    /// </summary>
    /// <remarks>
    /// Everything that walks these paths wants this form, and every caller reaching for
    /// <c>Path.GetDirectoryName</c> instead got mixed separators back on Windows.
    /// </remarks>
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
    }

    public override string ToString()
    {
        return $"[{Index}] {GdfPath} ({Size} B)";
    }
}
