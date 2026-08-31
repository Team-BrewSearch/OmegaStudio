using System.Globalization;

namespace Harugeki.Formats;

/// <summary>One entry in the container tree. Children are parsed on demand.</summary>
public sealed class RingNode
{
    private readonly Lock _probeLock = new();

    private List<RingNode?>? _children;
    private volatile bool _probed;
    private byte[]? _replacement;

    internal RingNode(RingArchive archive, RingNode? parent, int index, int offset, int length)
    {
        Archive = archive;
        Parent = parent;
        Index = index;
        Offset = offset;
        Length = length;
    }

    public RingArchive Archive { get; }
    public RingNode? Parent { get; }
    public int Index { get; }
    public int Offset { get; }
    public int Length { get; }

    /// <summary>Path of slot indices from the root, e.g. <c>00-08</c>.</summary>
    public string PathText
    {
        get
        {
            Stack<int> stack = new();
            for (RingNode? n = this; n is { Index: >= 0 }; n = n.Parent)
            {
                stack.Push(n.Index);
            }

            // Invariant: this path is used for filenames and round-trips through
            // the UI, so it must read the same under every locale.
            return stack.Count == 0
                ? "/"
                : string.Join("-", stack.Select(i => i.ToString("00", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Identity used to preserve aliasing on save. Unmodified nodes that share a
    /// source offset are the same payload; a replaced node becomes its own.
    /// </summary>
    internal object AliasKey
    {
        get => field ??= _replacement is not null ? new object() : Archive.AliasKeyFor(Offset);
        private set;
    }

    public ReadOnlySpan<byte> Span => _replacement ?? Archive.Data.AsSpan(Offset, Length);

    /// <summary>
    /// The payload's size right now. <see cref="Length"/> is the slot's size in the
    /// file that was opened, which a replacement is free to differ from — the
    /// archive recomputes every offset on save, so a payload may grow or shrink.
    /// </summary>
    public int Size => _replacement?.Length ?? Length;

    public bool IsDirectory
    {
        get
        {
            EnsureProbed();
            return _children is not null;
        }
    }

    public IReadOnlyList<RingNode?> Children
    {
        get
        {
            EnsureProbed();
            return _children ?? (IReadOnlyList<RingNode?>)[];
        }
    }

    public AssetKind Kind => IsDirectory ? AssetKind.Container : AssetTypes.Detect(Span);

    public byte[] GetPayload()
    {
        return _replacement ?? Archive.Data.AsSpan(Offset, Length).ToArray();
    }

    /// <summary>
    /// Replaces this leaf's bytes. The archive rebuilds on the next save, so the
    /// payload may be any size — larger or smaller than the slot it came from.
    ///
    /// <para>
    /// One caveat comes from the container format rather than from here: a child's
    /// length is the gap to the next child, so a payload whose length is not a
    /// multiple of <see cref="RingArchive.Align"/> reads back with up to 31 zero
    /// bytes of alignment padding after it. Every format in these archives carries
    /// its own length — RIFF chunk sizes, Ogg pages, the ringtex2 header — so the
    /// padding is inert, and because it is re-derived rather than kept it cannot
    /// accumulate over repeated edits.
    /// </para>
    /// </summary>
    public void Replace(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (IsDirectory)
        {
            throw new InvalidOperationException("cannot replace a container node's bytes");
        }

        _replacement = payload;
        AliasKey = new object();
        Archive.MarkDirty();
    }

    /// <summary>
    /// Works out once whether this slot is a table of contents, and builds its children if so.
    ///
    /// <para>
    /// Locked because the host reaches it from more than one thread: the Outliner expands a row
    /// on the UI thread, and the search walks the whole tree on the pool. Two threads probing
    /// one node used to race on the child list and could leave it half-built.
    /// </para>
    /// </summary>
    private void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        lock (_probeLock)
        {
            if (_probed)
            {
                return;
            }

            Probe();

            // Written last, and volatile: the release orders the child list ahead of it, so a
            // reader that sees the flag sees a finished list.
            _probed = true;
        }
    }

    private void Probe()
    {
        if (_replacement is not null)
        {
            return;
        }

        // Stop at a payload we recognise. An animation blob is shaped exactly like a
        // table of contents, so without this the walk would shred it into its bone
        // tracks. The second question covers standard payloads this format knows
        // nothing about — an OGG, a PNG — which the loaded recognisers answer for us.
        if (AssetTypes.Detect(Span) != AssetKind.Unknown || Archive.Host?.IsKnownPayload(Span) == true)
        {
            return;
        }

        (int Offset, int Length)?[]? spans = RingArchive.ReadToc(Archive.Data, Offset, Length);
        if (spans is null)
        {
            return;
        }

        _children = new List<RingNode?>(spans.Length);
        for (int i = 0; i < spans.Length; i++)
        {
            _children.Add(spans[i] is { } s ? new RingNode(Archive, this, i, s.Offset, s.Length) : null);
        }
    }

    public override string ToString()
    {
        return $"[{Index:00}] 0x{Offset:X} {Length} {Kind}";
    }
}
