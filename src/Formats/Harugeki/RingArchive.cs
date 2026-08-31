using Harugeki.Formats.Binary;
using Omega.Plugins;
using System.Buffers.Binary;

namespace Harugeki.Formats;

/// <summary>
/// The nested table-of-contents container used by every <c>data/*.bin</c> file.
///
/// <code>
/// u32      count
/// u32[]    child offset x count, relative to this table's own base
/// u8[]     0xFE padding up to the first child
/// </code>
///
/// Three rules are easy to get wrong and are all load-bearing: offsets are
/// <b>not sorted</b>, they <b>may repeat</b> (two slots aliasing one payload),
/// and an offset of <c>0</c> is an empty slot. A child's length runs to the next
/// larger <i>distinct</i> offset; the last runs to the end of the parent.
/// </summary>
public sealed class RingArchive
{
    public const byte Pad = 0xFE;
    public const int Align = 32;

    private const long MaxArchiveSize = 4L * 1024 * 1024 * 1024;
    private const int MinArchiveSize = 8;
    private const int MaxTableCount = 8192;

    private readonly Lock _aliasLock = new();
    private readonly Dictionary<int, object> _aliasKeys = [];

    /// <param name="data">The whole archive.</param>
    /// <param name="path">Where it came from, for the Properties pane.</param>
    /// <param name="host">
    /// Services, so the container walk can ask whether a blob is a standard payload it
    /// should stop at rather than descend into. Optional: without it the walk falls
    /// back to this format's own checks plus the table validation in
    /// <see cref="ReadToc"/>, which is safe, just less informed.
    /// </param>
    public RingArchive(byte[] data, string? path = null, IHostServices? host = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Path = path;
        Host = host;
        Root = new RingNode(this, null, -1, 0, data.Length);
    }

    public byte[] Data { get; }
    public string? Path { get; }
    public RingNode Root { get; }

    /// <summary>Host services, or null when the archive was opened outside the app.</summary>
    internal IHostServices? Host { get; }

    /// <summary>
    /// Whether any node's bytes have been replaced since the archive was opened or
    /// last written.
    ///
    /// <para>
    /// Tracked here rather than by the caller because the archive is the only thing
    /// that sees every edit: a texture spliced into a model reaches
    /// <see cref="RingNode.Replace"/> without the caller who asked for it ever
    /// touching the archive object.
    /// </para>
    /// </summary>
    public bool IsDirty { get; private set; }

    internal void MarkDirty()
    {
        IsDirty = true;
    }

    /// <summary>Records that the current contents are what is on disk.</summary>
    public void MarkSaved()
    {
        IsDirty = false;
    }

    public static RingArchive Load(string path, IHostServices? host = null)
    {
        FileInfo file = new(path);

        return file.Length > MaxArchiveSize
            ? throw new InvalidOperationException(
                $"Archive file is too large ({file.Length / 1024 / 1024} MB). " +
                $"Maximum supported: {MaxArchiveSize / 1024 / 1024} MB")
            : file.Length < MinArchiveSize
            ? throw new InvalidOperationException("Archive file is too small to be valid")
            : new RingArchive(File.ReadAllBytes(path), path, host);
    }

    /// <summary>
    /// Interns one identity per source offset, so two slots pointing at the same
    /// payload compare equal by reference and stay aliased when written back.
    /// </summary>
    internal object AliasKeyFor(int offset)
    {
        // Locked for the same reason RingNode.EnsureProbed is: nodes are reached from the UI
        // thread and from the search walk, and this interning table is shared by all of them.
        lock (_aliasLock)
        {
            if (!_aliasKeys.TryGetValue(offset, out object? key))
            {
                _aliasKeys[offset] = key = new object();
            }

            return key;
        }
    }

    internal static int AlignUp(int value)
    {
        return (value + Align - 1) & ~(Align - 1);
    }

    /// <summary>
    /// Whether a file's opening bytes look like this container, given how long the
    /// whole file is.
    ///
    /// <para>
    /// Separate from <see cref="ReadToc"/> because the two answer different questions.
    /// ReadToc has the whole payload and can insist every child offset lands inside
    /// it; this has only the first few kilobytes, and a 133 MB archive's last child
    /// offset is nowhere near them. Sniffing with ReadToc therefore rejected every
    /// real archive — correctly, for the question it was being asked, and uselessly
    /// for the question that mattered.
    /// </para>
    ///
    /// <para>
    /// What can still be checked from a head alone is quite strong: a plausible count,
    /// a table that fits, offsets that stay inside the file and start after the table,
    /// and the 0xFE padding that runs from the end of the table to the first child.
    /// </para>
    /// </summary>
    public static bool LooksLikeArchive(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head.Length < MinArchiveSize || fileLength < MinArchiveSize)
        {
            return false;
        }

        int count = (int)BinaryRead.U32(head, 0);
        if (count is <= 0 or >= MaxTableCount)
        {
            return false;
        }

        int need = 4 + (count * 4);
        if (need > head.Length)
        {
            return false;
        }

        int first = int.MaxValue;
        bool any = false;

        for (int i = 0; i < count; i++)
        {
            uint value = BinaryRead.U32(head, 4 + (i * 4));
            if (value > fileLength)
            {
                return false;
            }

            if (value == 0)
            {
                continue;   // an empty slot
            }

            if (value < (uint)need)
            {
                return false;   // a child inside its own table
            }

            any = true;
            first = System.Math.Min(first, (int)value);
        }

        if (!any)
        {
            return false;
        }

        // A genuine table pads with 0xFE up to its first child. Only as much of that
        // run as the head actually contains can be checked, which is plenty: the pad
        // is at most Align - 1 bytes past the table.
        for (int i = need; i < System.Math.Min(first, head.Length); i++)
        {
            if (head[i] != Pad)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the child spans of a table at <paramref name="baseOff"/>, or null if
    /// this is not a table. Entries are (offset, length); null marks an empty slot.
    /// </summary>
    internal static (int Offset, int Length)?[]? ReadToc(ReadOnlySpan<byte> data, int baseOff, int size)
    {
        if (size < MinArchiveSize)
        {
            return null;
        }

        ReadOnlySpan<byte> span = data.Slice(baseOff, size);
        int count = (int)BinaryRead.U32(span, 0);
        if (count is <= 0 or >= MaxTableCount)
        {
            return null;
        }

        int need = 4 + (count * 4);
        if (need > size)
        {
            return null;
        }

        int[] offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint value = BinaryRead.U32(span, 4 + (i * 4));
            if (value > int.MaxValue)
            {
                return null;
            }

            offsets[i] = (int)value;
        }

        int[] distinct = [.. offsets.Where(o => o != 0).Distinct().Order()];
        if (distinct.Length == 0 || distinct[0] < need || distinct[^1] > size)
        {
            return null;
        }

        // A genuine table pads with 0xFE up to its first child.
        for (int i = need; i < distinct[0]; i++)
        {
            if (span[i] != Pad)
            {
                return null;
            }
        }

        // A child runs to the next larger distinct offset; the last to the end.
        Dictionary<int, int> endOf = new(distinct.Length);
        for (int i = 0; i < distinct.Length; i++)
        {
            endOf[distinct[i]] = i + 1 < distinct.Length ? distinct[i + 1] : size;
        }

        (int, int)?[] spans = new (int, int)?[count];
        for (int i = 0; i < count; i++)
        {
            spans[i] = offsets[i] == 0 ? null : (baseOff + offsets[i], endOf[offsets[i]] - offsets[i]);
        }

        return spans;
    }

    /// <summary>Rebuilds the whole archive, honouring any replaced payloads.</summary>
    public byte[] Save()
    {
        return Serialize(Root);
    }

    private static byte[] Serialize(RingNode node)
    {
        if (!node.IsDirectory)
        {
            return node.GetPayload();
        }

        IReadOnlyList<RingNode?> children = node.Children;
        int count = children.Count;
        int need = 4 + (count * 4);
        int first = AlignUp(need);

        using MemoryStream body = new();
        int[] offsets = new int[count];

        // Aliased slots must stay aliased: emit each distinct payload once.
        Dictionary<object, int> seen = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < count; i++)
        {
            RingNode? child = children[i];
            if (child is null)
            {
                offsets[i] = 0;
                continue;
            }

            object key = child.AliasKey;
            if (seen.TryGetValue(key, out int already))
            {
                offsets[i] = already;
                continue;
            }

            int at = first + (int)body.Length;
            byte[] data = Serialize(child);
            body.Write(data, 0, data.Length);

            // Keep the next child on a 32-byte boundary.
            int padTo = AlignUp((int)body.Length);
            for (int p = (int)body.Length; p < padTo; p++)
            {
                body.WriteByte(0);
            }

            offsets[i] = at;
            seen[key] = at;
        }

        byte[] result = new byte[first + (int)body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)count);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4 + (i * 4)), (uint)offsets[i]);
        }

        result.AsSpan(need, first - need).Fill(Pad);
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(first));
        return result;
    }
}
