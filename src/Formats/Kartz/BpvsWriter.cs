namespace Kartz.Formats;

/// <summary>
/// Builds a minimal, structurally valid <c>.bpvs</c> (potentially-visible-set) file for a
/// single-chunk track.
/// </summary>
/// <remarks>
/// <para>
/// The real format is only partially decoded — direct inspection of shipped files (confirmed
/// across three different tracks) shows: <c>u32 numSections</c>, <c>u32 offsetToData</c>
/// (always <c>8</c> — it points at the per-section offset table immediately following it, not
/// at a separate data region as an earlier reading of this project's own notes assumed),
/// <c>numSections × u32</c> per-section byte offsets into a visibility-data region, a constant
/// 4-byte gap, then the data itself: one variable-length record per section (94, 124, 127...
/// bytes in a real 87-section file — nowhere close to the ~11 bytes a simple 1-bit-per-section
/// mask would need), with a byte pattern (0x8N tag bytes paired with small values) that looks
/// like a run-length or otherwise compressed encoding, not a flat bitfield.
/// </para>
/// <para>
/// That was discovered the expensive way: an early attempt at multi-chunk glTF→TRK conversion
/// matched the new <c>.trk</c>'s chunk count to the stale <c>.bpvs</c>'s section count (avoiding
/// an array-bounds crash — see <c>TrkChunker</c>'s history) but left the
/// visibility *content* itself untouched, still describing the original chunks' spatial
/// arrangement. Confirmed on real hardware/emulator: the engine trusts that stale content enough
/// to treat some new, spatially-unrelated chunk as "always visible near the camera," which
/// crashed a Rust-based emulator's 3D rasterizer with an out-of-bounds screen-coordinate index —
/// consistent with the renderer being handed geometry it never expected to need to project.
/// Attempting to patch that content into "everything visible from everywhere" without properly
/// decoding the compression would risk writing structurally invalid data — a strictly worse
/// failure mode than the one being fixed.
/// </para>
/// <para>
/// The safe way out: single-chunk conversion needs nothing decoded at all. One section has
/// nothing else to report visibility against, so its record can simply be empty — zero bytes,
/// no encoding to get wrong. This produces a <c>.bpvs</c> that is genuinely well-formed by the
/// same structural rules observed in every real file, just for the trivial one-section case.
/// </para>
/// </remarks>
public static class BpvsWriter
{
    /// <summary>
    /// A 16-byte <c>.bpvs</c> declaring exactly one section with an empty (zero-length)
    /// visibility record: <c>numSections=1</c>, <c>offsetToData=8</c>, one table entry
    /// pointing 4 bytes past where the table itself ends (matching the gap observed in every
    /// real file), and no section data — the file ends right there.
    /// </summary>
    public static byte[] BuildMinimalSingleSection()
    {
        const int numSections = 1;
        const int tableStart = 8;
        int tableEnd = tableStart + (numSections * 4);
        int section0Offset = tableEnd + 4; // the constant gap observed in every real .bpvs

        byte[] blob = new byte[section0Offset]; // section 0's data is zero bytes long
        WriteU32(blob, 0x00, numSections);
        WriteU32(blob, 0x04, tableStart);
        WriteU32(blob, tableStart, (uint)section0Offset);
        // bytes [tableEnd, section0Offset) are the observed constant gap; left as zero, matching
        // no confirmed meaning for them in any real file either.
        return blob;
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)(v >> 24);
    }
}
