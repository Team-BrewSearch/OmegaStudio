using System.Numerics;
using SMath = System.Math;

namespace Kartz.Formats;

/// <summary>
/// Builds a <c>.trk</c> blob from spatially chunked glTF geometry — the write-side
/// counterpart of <see cref="SskTrack"/>'s parser, targeted at re-loading correctly in this
/// app rather than at reproducing every byte a real DS engine would need.
/// </summary>
/// <remarks>
/// <para>
/// <b>The real per-vertex opcode sequence has no <c>NORMAL</c> opcode at all, and <c>COLOR</c>
/// is only re-emitted when it changes.</b> Confirmed by directly tracing a chunk from the
/// actual factory-shipped ROM (not a previously-converted file — see the cautionary note
/// below): after each <c>TEXIMAGE_PARAM</c>, real chunks emit <c>PLTT_BASE</c> (0x2B, param
/// always 0) then a baseline white <c>COLOR</c> (0x7FFF) *before* <c>BEGIN_VTXS</c>, and then
/// per vertex: <c>COLOR</c> only if it differs from the last one written, always <c>TEXCOORD</c>,
/// then <c>VTX_16</c>. No vertex in any traced chunk ever carries a <c>NORMAL</c> opcode — track
/// geometry is vertex-color-shaded only, and <see cref="SskTrack"/>'s decoder already defaults
/// an unset normal to <c>(0,0,0)</c>, exactly what every real vertex decodes to. An earlier
/// version of this writer emitted <c>NORMAL</c> for every vertex, always re-emitted <c>COLOR</c>
/// even when unchanged, and never emitted <c>PLTT_BASE</c> or the baseline <c>COLOR</c> — none
/// of that was caught by this project's own round-trip testing (which only re-decodes with the
/// same lenient, state-based parser that tolerates extra/missing opcodes just fine), and inflated
/// converted files by ~68% versus the true original. It's the leading suspect for real-hardware
/// "Undefined Opcode" crashes: a real engine's load-time DL processing plausibly assumes a fixed
/// per-vertex opcode shape, and unexpected extra opcodes woven through the stream would desync
/// whatever's parsing it.
/// </para>
/// <para>
/// <b>Cautionary tale on what "pristine" means when testing this.</b> For a long stretch of this
/// feature's debugging, "231264 bytes" was treated as this track's original size and used as the
/// baseline for round-trip verification — it was not; the actual factory-shipped
/// <c>FarFarAway.trk</c> is 137452 bytes (confirmed by extracting straight from the retail ROM's
/// own filesystem two independent ways). Every round-trip test run against the wrong baseline
/// "passed" because it was silently comparing this writer's output against *itself* from a
/// previous run, not against real data — which is exactly how the missing-<c>PLTT_BASE</c>/extra-
/// <c>NORMAL</c> bug above went undetected for so long. When verifying this format against "the
/// original," extract straight from an unmodified ROM rather than trusting whatever the working
/// archive currently holds.
/// </para>
/// <para>
/// <b>Tail sections are relocated, not regenerated — and the tail is not opaque.</b> The header
/// (bytes 0x14-0x37) carries five absolute byte offsets into a contiguous run of tail data after
/// the display lists (vertex-color palette, texcoord-transform params, track-section defs,
/// material data, spatial BVH). This project's own parser reads none of it, so the run is copied
/// byte-for-byte and appended after the new chunk data, with the five header offsets shifted by a
/// constant delta.
/// </para>
/// <para>
/// <b>Shifting only those five header offsets is not enough.</b> The section at 0x34 <i>begins with
/// its own table of absolute file offsets</i> — <c>count</c> of them, read from 0x30 — pointing at
/// the records that follow it. Copying the tail verbatim leaves every one of those pointing at its
/// address in the <i>old</i> file. Verified present on all 16 shipped tracks (11-19 entries each:
/// contiguous, strictly ascending, first entry always exactly <c>sectionStart + count*4</c>), so
/// <see cref="RelocateOffsetTable"/> rewrites them by the same delta.
/// </para>
/// <para>
/// This was the long-standing cause of load-time crashes on real hardware and emulators. It hid
/// extremely well: at <i>identical</i> file size the delta is zero and the stale pointers happen to
/// stay correct, so a byte-perfect round trip loaded fine and appeared to vindicate the whole write
/// path — while <i>any</i> conversion that changed the file's size (an edit, or merely a different
/// legal opcode encoding) left 11-19 wild pointers in the file. That matches the scattered
/// wild-address reads in the emulator logs exactly. Note the failure is invisible to every check
/// this project can make on its own output: the geometry decodes perfectly, the display lists are
/// structurally valid, and the bytes differ from the original only where they legitimately should.
/// </para>
/// <para>
/// Not every in-range word in the tail is a pointer. Some neighbouring fields hold 20.12
/// fixed-point coordinates whose raw values fall in the same numeric range — e.g. <c>0x21000</c> is
/// both the address 135168 and the coordinate 33.0, and in FarFarAway's section at 0x2C it is
/// plainly the latter (the surrounding record reads as a heading of 180.0 with mirrored X
/// positions of ±7.0, and as an address it would land mid-record inside another section rather
/// than on a boundary). Relocation is therefore deliberately restricted to the one table whose
/// structure is unambiguous; see <see cref="RelocateOffsetTable"/>'s remarks.
/// </para>
/// <para>
/// <b>Chunk count must match the template's own count exactly, not just be array-bounds-safe.</b>
/// An early attempt collapsed everything into a single chunk (on the theory that one chunk has
/// no cross-chunk visibility data to be wrong about) and regenerated a matching minimal
/// <c>.bpvs</c> — but the tail data above is copied unchanged from the template and still
/// describes the *original* chunk count (e.g. 87 sections' worth of section defs / BVH nodes).
/// With <c>numTotal=1</c> that produced a genuine count mismatch between the header and the
/// tail's own internal structure, not just a stale-content one, and confirmed on real
/// hardware/emulator to crash every tested emulator (including ones that had tolerated a
/// count-matched multi-chunk conversion with stale positions just fine) immediately on load.
/// The fix: always target the template's own chunk count exactly — see
/// <c>TrkChunker.ChunkToAnchors</c>, which buckets the new geometry
/// against the template's own per-chunk positions (via <see cref="ReadChunkPositions"/>) so
/// chunk index <c>i</c> keeps roughly the same world location the unchanged tail/.bpvs assume
/// for it, instead of an arbitrary grid cell.
/// </para>
/// <para>
/// <b>Also not attempted</b> (lower-risk gaps than the above): the real VRAM texel address a
/// <c>TEXIMAGE_PARAM</c> opcode carries on real hardware (texture selection here is resolved
/// purely through the chunk's texture-reference table, exactly like the parser does), and true
/// per-chunk LOD (every chunk written here is chunk_type <c>0x0E</c>, "primary" — there are no
/// paired <c>0x0000</c> detail chunks). The paired <c>.col</c> file is not regenerated and
/// becomes semantically stale relative to new geometry.
/// </para>
/// <para>
/// <b>Vertex quantization, and why the emitted opcode order is not arbitrary.</b>
/// <c>VTX_16</c>'s raw fixed-point range is only about ±8 units, so one translate/scale pair is
/// emitted per chunk and every vertex is encoded relative to it.
/// <see cref="SskTrack"/>'s decoder implements <c>MTX_TRANS</c>/<c>MTX_SCALE</c> as plain state
/// assignment (<c>worldPos = local*scale + translate</c>, in whatever order the two appear),
/// which is a simplification — on real hardware both opcodes *multiply* the current matrix, and
/// order therefore matters. The two agree only for the <c>MTX_PUSH</c>, <c>MTX_TRANS</c>,
/// <c>MTX_SCALE</c> order real chunks actually use: hardware then computes
/// <c>view · T · S · v</c> = <c>view · (translate + scale·v)</c>, matching the decoder. Emitting
/// <c>MTX_SCALE</c> before <c>MTX_TRANS</c> would give <c>scale·translate + scale·v</c> on
/// hardware while decoding identically here — a bug this project's own round-trip could never
/// detect. Same story for the surrounding push/pop: the decoder ignores the matrix stack
/// entirely, so only real hardware can tell that an unbalanced push corrupts it.
/// </para>
/// </remarks>
public static class TrkWriter
{
    private const int HeaderSize = 0x38; // 56 bytes
    private const int ChunkHeaderSize = 24;
    private const int TexRefEntrySize = 12;
    private const float VtxRange = 7.5f; // int16/4096 safety margin under the true ±8.0 limit

    // Real DS GX command numbers (GBATEK: cmd = (ioAddress - 0x40000400) / 4). Note 0x11 is
    // MTX_PUSH and 0x12 is MTX_POP — *not* MTX_IDENTITY (which is 0x15) as this project's own
    // SskTrack decoder historically labelled 0x11. That mislabelling is harmless for decoding
    // (every chunk pushes and then immediately sets TRANS/SCALE, so treating the push as an
    // identity-reset produces the same object-space result) but it hid a real hardware bug on the
    // write side: a push with no matching pop leaks a level of the GX matrix stack — see
    // BuildChunk's empty-chunk early return.
    private const byte CmdMtxPush = 0x11;
    private const byte CmdMtxPop = 0x12;
    private const byte CmdMtxScale = 0x1B;
    private const byte CmdMtxTrans = 0x1C;
    private const byte CmdColor = 0x20;
    private const byte CmdTexcoord = 0x22;
    private const byte CmdVtx16 = 0x23;
    private const byte CmdVtxDiff = 0x28;
    private const byte CmdPlttBase = 0x2B;
    private const byte CmdTeximageParam = 0x2A;
    private const byte CmdBeginVtxs = 0x40;
    private const byte CmdEndVtxs = 0x41;
    // White, matching the COLOR that real chunks set right before each BEGIN_VTXS.
    private const uint BaselineColor555 = 0x7FFF;

    /// <summary>
    /// Builds a full <c>.trk</c> blob. <paramref name="originalTemplate"/> supplies every
    /// header byte this format's own parser never reads (only <c>numTotal</c>@0x02 is
    /// patched; <c>chunkTableStart</c>@0x0C is forced to <see cref="HeaderSize"/>) — safer
    /// than inventing values for fields whose real meaning isn't confirmed.
    /// </summary>
    public static byte[] Build(ReadOnlySpan<byte> originalTemplate, IReadOnlyList<TrkChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new ArgumentException("At least one chunk is required.", nameof(chunks));
        }

        if (originalTemplate.Length < HeaderSize)
        {
            throw new ArgumentException("Template is too small to hold a .trk header.", nameof(originalTemplate));
        }

        int chunkCount = chunks.Count;
        byte[][] chunkBlobs = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            // A chunk with no geometry is reproduced from the template rather than synthesised.
            // Its "empty" shape is not uniform: Super Star Kartz always uses dlSize=0, but
            // Madagascar Kartz (same engine) also ships a *hollow* variant — a real display list
            // holding MTX_PUSH/TRANS/SCALE, TEXIMAGE_PARAM(0), a baseline COLOR and MTX_POP, with
            // no vertices at all (Volcano chunk 106, 44 bytes). Both are legal and neither is
            // derivable from the geometry, so the template's own bytes are the only correct source.
            chunkBlobs[i] = chunks[i].Triangles.Count == 0
                    && TryCopyTemplateChunk(originalTemplate, i, out byte[]? copied)
                ? copied
                : BuildChunk(chunks[i], i);
        }

        byte[] header = originalTemplate[..HeaderSize].ToArray();
        WriteU16(header, 0x02, (ushort)chunkCount);
        WriteU32(header, 0x0C, HeaderSize);

        // The five tail-section offset fields, in file order — section1_offset is the start of
        // one contiguous run of tail data (verified: all five offsets in a real track increase
        // monotonically and the last one's data runs to EOF), so this is copy-and-relocate, not
        // copy-five-separate-pieces.
        int[] tailFieldOffsets = [0x14, 0x1C, 0x24, 0x2C, 0x34];
        uint originalTailStart = ReadU32(header, 0x14);
        byte[] tail = originalTailStart > 0 && originalTailStart < originalTemplate.Length
            ? originalTemplate[(int)originalTailStart..].ToArray()
            : [];

        int offsetTableSize = chunkCount * 4;
        int positionTableSize = chunkCount * 12;
        int bodyStart = HeaderSize + offsetTableSize + positionTableSize;

        int[] chunkOffsets = new int[chunkCount];
        int cursor = bodyStart;
        for (int i = 0; i < chunkCount; i++)
        {
            chunkOffsets[i] = cursor;
            cursor += chunkBlobs[i].Length;
        }

        int newTailStart = cursor;
        if (tail.Length > 0)
        {
            long delta = newTailStart - originalTailStart;

            // The tail is not opaque: section 5 begins with a table of *absolute file offsets*
            // pointing at its own records, so moving the tail without rewriting them leaves the
            // engine following stale pointers into whatever now occupies those addresses.
            //
            // Confirmed on every shipped track: the first `count` u32s of the section at 0x34
            // (count read from 0x30) are all in-file addresses, contiguous, ascending, and the
            // first one is exactly sectionStart + count*4 — i.e. the records begin immediately
            // after the table. 12 entries in FarFarAway, 13 in KingOfNewYork, 11 in
            // GallaxharsShip, etc.
            //
            // This is why a byte-identical round trip loaded correctly while *every* conversion
            // that changed the file's size crashed on hardware: at identical size delta is 0 and
            // the stale pointers happen to remain right. Any other size left 11-13 wild pointers
            // in the file, which matches the scattered wild-address reads seen in emulator logs.
            RelocateOffsetTable(tail, originalTemplate, originalTailStart, 0x34, 0x30, delta);

            foreach (int fieldOff in tailFieldOffsets)
            {
                uint orig = ReadU32(header, fieldOff);
                if (orig != 0) // a genuinely absent/unused section stays 0, not 0+delta
                {
                    WriteU32(header, fieldOff, (uint)(orig + delta));
                }
            }
        }

        byte[] result = new byte[newTailStart + tail.Length];
        header.CopyTo(result, 0);

        for (int i = 0; i < chunkCount; i++)
        {
            WriteU32(result, HeaderSize + (i * 4), (uint)chunkOffsets[i]);

            int posOff = HeaderSize + offsetTableSize + (i * 12);
            Vector3 p = chunks[i].Position;
            WriteI32(result, posOff, (int)MathF.Round(p.X * 4096f));
            WriteI32(result, posOff + 4, (int)MathF.Round(p.Y * 4096f));
            WriteI32(result, posOff + 8, (int)MathF.Round(p.Z * 4096f));
        }

        for (int i = 0; i < chunkCount; i++)
        {
            chunkBlobs[i].CopyTo(result, chunkOffsets[i]);
        }

        tail.CopyTo(result, newTailStart);

        return result;
    }

    private static uint ReadU32(ReadOnlySpan<byte> buf, int off)
    {
        return (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));
    }

    /// <summary>
    /// Reads a real <c>.trk</c>'s own per-chunk position table — <c>numTotal</c>@0x02, then
    /// (after the chunk offset table) one i32 x/y/z triple per chunk in 20.12 fixed point.
    /// Used by <c>TrkChunker.ChunkToAnchors</c> to anchor a
    /// reconversion's new chunks to the same indices and approximate world locations as the
    /// track being replaced, which is what keeps the tail sections and paired <c>.bpvs</c>
    /// (both left byte-for-byte unchanged, both keyed by chunk index) meaningfully valid for
    /// the new geometry rather than merely array-bounds-safe. Returns an empty array if
    /// <paramref name="template"/> is too small or malformed to hold a full position table —
    /// callers should treat that as "fall back to something else," not "assume zero chunks."
    /// </summary>
    public static Vector3[] ReadChunkPositions(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }

        int numTotal = template[0x02] | (template[0x03] << 8);
        int posTableStart = HeaderSize + (numTotal * 4);
        if (numTotal <= 0 || posTableStart + (numTotal * 12) > template.Length)
        {
            return [];
        }

        Vector3[] positions = new Vector3[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int off = posTableStart + (i * 12);
            int x = (int)ReadU32(template, off);
            int y = (int)ReadU32(template, off + 4);
            int z = (int)ReadU32(template, off + 8);
            positions[i] = new Vector3(x / 4096f, y / 4096f, z / 4096f);
        }

        return positions;
    }

    /// <summary>
    /// Reads a real <c>.trk</c>'s own per-chunk header type field (offset +0x02 of each
    /// chunk, via the offset table at <see cref="HeaderSize"/>) — see <see cref="TrkChunk.ChunkType"/>
    /// for what the values mean and why preserving them per index matters. Returns an empty
    /// array on the same malformed-input conditions as <see cref="ReadChunkPositions"/>
    /// (and callers should treat it the same way).
    /// </summary>
    public static ushort[] ReadChunkTypes(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }

        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }

        ushort[] types = new ushort[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            types[i] = chunkOff + 4 <= template.Length
                ? (ushort)(template[chunkOff + 2] | (template[chunkOff + 3] << 8))
                : (ushort)0x000E;
        }

        return types;
    }

    /// <summary>
    /// Reads a real <c>.trk</c>'s own per-chunk header fields at +0x08 (coord_scale) and +0x0C
    /// (packed_params) — see <see cref="TrkChunk.CoordScale"/> for why these must be preserved
    /// rather than zeroed. Same malformed-input contract as <see cref="ReadChunkPositions"/>.
    /// </summary>
    public static (uint CoordScale, uint PackedParams)[] ReadChunkExtraFields(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }

        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }

        (uint, uint)[] fields = new (uint, uint)[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            fields[i] = chunkOff + 0x10 <= template.Length
                ? (ReadU32(template, chunkOff + 0x08), ReadU32(template, chunkOff + 0x0C))
                : (0u, 0u);
        }

        return fields;
    }

    /// <summary>
    /// Reads each real chunk's own <c>MTX_SCALE</c> value (the uniform scale in its
    /// <c>MTX_IDENTITY</c>/<c>MTX_TRANS</c>/<c>MTX_SCALE</c> display-list header — see the type
    /// remarks on why this needs to be read, not computed). Real values cluster on clean powers
    /// of two (confirmed: 2, 4, 8, 16, 32, 64, 256 across FarFarAway's 386 non-degenerate
    /// chunks) but aren't derivable from the chunk's actual triangle bounds — confirmed the
    /// value reflects the *nominal* grid cell size the original level tool targeted, not the
    /// realized geometry's bounding box (only 212/386 chunks would match a "smallest power of
    /// two that fits the actual triangles" formula; the rest use a strictly larger scale than
    /// their own content needs). Returns <see langword="null"/> per chunk whose display list
    /// doesn't start with the expected <c>MTX_IDENTITY(0x11), MTX_TRANS(0x1C), MTX_SCALE(0x1B)</c>
    /// three-opcode header (degenerate/empty chunks, or a malformed template) — callers should
    /// fall back to computing a safe scale for those specifically, not for the whole file.
    /// </summary>
    public static float?[] ReadChunkVtxScales(ReadOnlySpan<byte> template)
    {
        if (template.Length < HeaderSize)
        {
            return [];
        }

        int numTotal = template[0x02] | (template[0x03] << 8);
        if (numTotal <= 0 || HeaderSize + (numTotal * 4) > template.Length)
        {
            return [];
        }

        float?[] scales = new float?[numTotal];
        for (int i = 0; i < numTotal; i++)
        {
            int chunkOff = (int)ReadU32(template, HeaderSize + (i * 4));
            if (chunkOff + 24 > template.Length)
            {
                continue;
            }

            uint texCount = ReadU32(template, chunkOff + 0x10);
            uint dlSize = ReadU32(template, chunkOff + 0x04);
            int dlOff = chunkOff + 24 + ((int)texCount * 12);
            if (dlSize < 28 || dlOff + 28 > template.Length)
            {
                continue;
            }

            if (template[dlOff] != 0x11 || template[dlOff + 1] != 0x1C || template[dlOff + 2] != 0x1B)
            {
                continue;
            }

            int scaleRaw = (int)ReadU32(template, dlOff + 16); // MTX_SCALE's first param (X; Y/Z always equal X)
            scales[i] = scaleRaw / 4096f;
        }

        return scales;
    }

    /// <summary>
    /// Copies chunk <paramref name="chunkIndex"/> verbatim out of <paramref name="template"/>,
    /// for chunks that carry no geometry. Returns <see langword="false"/> (and the caller
    /// synthesises one instead) when the template has no such chunk or its declared extent runs
    /// past the end of the file.
    /// </summary>
    /// <remarks>
    /// Only ever used for zero-triangle chunks, where the template's bytes cannot conflict with
    /// anything the conversion produced: there is no geometry to place, and the chunk's own
    /// header fields (type, coord_scale, packed_params) are exactly what would be copied anyway.
    /// It preserves whichever empty shape the source game uses — see the call site.
    /// </remarks>
    private static bool TryCopyTemplateChunk(ReadOnlySpan<byte> template, int chunkIndex, out byte[] copy)
    {
        copy = [];
        int tableEntry = HeaderSize + (chunkIndex * 4);
        if (tableEntry + 4 > template.Length)
        {
            return false;
        }

        uint numTotal = (uint)(template[0x02] | (template[0x03] << 8));
        if (chunkIndex >= numTotal)
        {
            return false;
        }

        uint off = ReadU32(template, tableEntry);
        if (off == 0 || off + ChunkHeaderSize > template.Length)
        {
            return false;
        }

        uint texCount = ReadU32(template, (int)off + 0x10);
        uint dlSize = ReadU32(template, (int)off + 0x04);
        long total = ChunkHeaderSize + ((long)texCount * TexRefEntrySize) + dlSize;
        if (texCount != 0 || off + total > template.Length)
        {
            // A geometry-less chunk with texture references isn't a shape this has ever seen;
            // fall back to synthesising rather than copying something unexplained.
            return false;
        }

        copy = template.Slice((int)off, (int)total).ToArray();
        return true;
    }

    /// <summary>
    /// Shifts a tail section's leading table of absolute file offsets by <paramref name="delta"/>,
    /// in place, inside the copied <paramref name="tail"/> buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately conservative: an entry is rewritten only when it actually looks like a pointer
    /// into the original tail region. Some neighbouring fields in these sections hold 20.12
    /// fixed-point coordinates whose raw values can coincidentally land in the same numeric range
    /// (e.g. <c>0x21000</c> is both the address 135168 and the coordinate 33.0), so blindly adding
    /// a delta to every u32 that "looks like" an offset would silently corrupt geometry data. Only
    /// the section-5 leading table is treated as pointers, because only there is the evidence
    /// unambiguous — the entries are contiguous, strictly ascending, exactly <c>count</c> of them,
    /// and the first equals <c>sectionStart + count*4</c> on every shipped track.
    /// </para>
    /// <para>
    /// If the table fails those structural checks (an unfamiliar track, or a template this project
    /// has not seen), nothing is rewritten: leaving offsets alone is recoverable, whereas rewriting
    /// the wrong words is not.
    /// </para>
    /// </remarks>
    private static void RelocateOffsetTable(
        byte[] tail, ReadOnlySpan<byte> originalTemplate, uint originalTailStart,
        int offsetField, int countField, long delta)
    {
        if (delta == 0 || tail.Length == 0)
        {
            return;
        }

        uint sectionStart = ReadU32(originalTemplate, offsetField);
        uint count = ReadU32(originalTemplate, countField);
        if (sectionStart == 0 || count == 0 || count > 4096)
        {
            return;
        }

        long indexInTail = (long)sectionStart - originalTailStart;
        long tableBytes = (long)count * 4;
        if (indexInTail < 0 || indexInTail + tableBytes > tail.Length)
        {
            return;
        }

        // Structural validation before touching anything.
        uint originalFileLength = (uint)(originalTailStart + tail.Length);
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint value = ReadU32(tail, (int)indexInTail + (i * 4));
            bool inFile = value >= sectionStart && value < originalFileLength;
            bool ascending = i == 0 || value > previous;
            if (!inFile || !ascending)
            {
                return;
            }

            previous = value;
        }

        if (ReadU32(tail, (int)indexInTail) != sectionStart + tableBytes)
        {
            return; // records don't start immediately after the table — not the shape we know
        }

        for (int i = 0; i < count; i++)
        {
            int at = (int)indexInTail + (i * 4);
            WriteU32(tail, at, (uint)(ReadU32(tail, at) + delta));
        }
    }

    // ── One chunk ────────────────────────────────────────────────────────────

    private static byte[] BuildChunk(TrkChunk chunk, int chunkIndex)
    {
        // A chunk with no geometry gets NO display list at all: dlSize=0, texCount=0, just the
        // 24-byte header. This is the only "empty" shape that exists in real data — verified
        // exhaustively across all 3996 chunks of all 16 shipped tracks: 8 chunks have dlSize==0,
        // and *zero* chunks anywhere have a non-empty DL that emits no vertices.
        //
        // An earlier version of this writer instead emitted a fabricated 28-byte "hollow" DL
        // (MTX_PUSH + MTX_TRANS + MTX_SCALE + NOP, no geometry, no MTX_POP). That shape occurs
        // nowhere in real data, and it is an outright hardware bug: 0x11 is MTX_PUSH, not the
        // MTX_IDENTITY this project's decoder calls it, so every one of those chunks pushed a
        // level onto the GX matrix stack and never popped it. Real data is perfectly balanced —
        // every non-empty chunk has exactly one MTX_PUSH and pops exactly one level, with zero
        // exceptions archive-wide — whereas a converted FarFarAway leaked 2 levels (388 pushes
        // vs 386 pops). The DS position/direction matrix stack is only 31 deep and sets a GXSTAT
        // error bit on overflow, so leaked levels are a real load-time failure mode, not a
        // cosmetic byte difference.
        //
        // (The earlier claim that "FarFarAway's own 37 texCount=0 chunks are all exactly 28
        // bytes" was simply wrong: FarFarAway has only 7 texCount=0 chunks — 2 genuinely empty
        // with dlSize=0, and 5 carrying real untextured geometry, which are handled by the
        // untextured branch below rather than by being treated as empty.)
        if (chunk.Triangles.Count == 0)
        {
            byte[] empty = new byte[ChunkHeaderSize];
            WriteU16(empty, 0x00, (ushort)chunkIndex);
            WriteU16(empty, 0x02, chunk.ChunkType);
            WriteU32(empty, 0x04, 0); // dlSize — no display list at all
            WriteU32(empty, 0x08, chunk.CoordScale);
            WriteU32(empty, 0x0C, chunk.PackedParams);
            WriteU32(empty, 0x10, 0); // texCount
            WriteU32(empty, 0x14, 0); // reserved (always 0 in real data — verified archive-wide)
            return empty;
        }

        (Vector3 translate, float scale) = FitTransform(chunk);

        // Group by texture so each distinct texture in the chunk gets exactly one
        // TEXIMAGE_PARAM/BEGIN_VTXS/END_VTXS run, matching how the real chunks are shaped.
        List<IGrouping<int, (TrkImportPrimitive Prim, int A, int B, int C)>> groups = chunk.Triangles
            .GroupBy(t => t.Prim.TextureIndex)
            .ToList();

        // MTX_PUSH, then TRANS, then SCALE — the exact order every real chunk uses, and the order
        // real GX semantics require. MTX_TRANS/MTX_SCALE *multiply* the current matrix on real
        // hardware, so TRANS-then-SCALE yields `view * T * S`, placing a vertex at
        // `trans + scale*v` — which is exactly what this project's decoder computes when it
        // treats them as plain state. Emitting SCALE before TRANS would instead give
        // `scale*trans + scale*v` on hardware while decoding identically here, i.e. a bug this
        // project's own round-trip could never detect. The matching MTX_POP is emitted at the end.
        List<Op> ops = [
            new Op(CmdMtxPush),
            new Op(
                CmdMtxTrans,
                EncodeFixed20_12(translate.X),
                EncodeFixed20_12(translate.Y),
                EncodeFixed20_12(translate.Z),
                3),
            new Op(CmdMtxScale, EncodeFixed20_12(scale), EncodeFixed20_12(scale), EncodeFixed20_12(scale), 3)];

        List<int> texImageOpIndex = [];
        List<int> plttBaseOpIndex = [];
        List<int> textureSlot1Based = [];

        // TEXCOORD state is chunk-scoped, not per-block and not per-group: SskTrack.DecodeChunk's
        // tu/tv are chunk locals that no opcode ever resets, starting at (0,0). Tracking it the
        // same way here is what makes a skipped TEXCOORD provably safe.
        uint lastTexcoord = 0;

        // Whether this chunk has emitted a TEXCOORD yet. A block restates the coordinate in
        // state when it opens, but only once there is one: a chunk that never textures anything
        // emits no TEXCOORD at all, in 70 Super Star Kartz blocks and 38 Madagascar Kartz ones.
        bool hasTexcoord = false;

        // Per-primitive count of vertices emitted so far, indexing TrkImportPrimitive.TexcoordEmit.
        Dictionary<TrkImportPrimitive, int> vertexOrdinal = [];

        foreach (IGrouping<int, (TrkImportPrimitive Prim, int A, int B, int C)> group in groups)
        {
            // Untextured geometry (TextureIndex == -1) is NOT given a texRefEntry at all in real
            // data — confirmed by scanning every texRefEntry across all 16 shipped tracks (3996
            // chunks): not one ever has texIdx1based == 0. An earlier version of this writer
            // always created a table entry (texIdx1based = 0 for untextured groups), on the
            // theory that real data did this too — that theory was never actually verified and
            // turned out to be wrong. Real untextured chunks instead have <c>texCount == 0</c> in
            // their header entirely, with a single TEXIMAGE_PARAM opcode embedded directly in the
            // DL (value 0x00000000, not the normal 0x40000000 — confirmed by direct trace of
            // FarFarAway chunks 48/50/84/85/86, all texCount=0 with real non-empty geometry: dl
            // shape is IDENTITY/TRANS/SCALE, TEXIMAGE(0), baseline COLOR, then strips/singles with
            // no PLTT_BASE and no TEXCOORD at all — texcoord is meaningless with no texture to
            // sample). This is a distinct shape from the genuinely-empty 28-byte chunk (0 triangles,
            // no groups at all): this is real, present geometry that simply isn't textured.
            bool untextured = group.Key < 0;

            // Confirmed by direct inspection: every real *textured* TEXIMAGE_PARAM value in
            // FarFarAway.trk is exactly 0x40000000 — bit 30 (texcoord-transform mode) set, every
            // VRAM-offset/size/format bit zero. That's not a fully-valid real-hardware texture
            // setup on its own (zero format/size would sample nothing), which is strong evidence
            // the real engine patches the actual per-texture VRAM address/format/size into this
            // opcode at load time from the texRefTable, exactly mirroring how this project's own
            // parser resolves textures — the stored value is already a placeholder upstream, not
            // something this project is choosing to leave unfinished.
            void EmitGroupHeader()
            {
                if (!untextured)
                {
                    texImageOpIndex.Add(ops.Count);
                }

                ops.Add(new Op(CmdTeximageParam, untextured ? 0x00000000u : 0x40000000u, 0, 0, 1));
                if (!untextured)
                {
                    textureSlot1Based.Add(group.Key + 1);
                    plttBaseOpIndex.Add(ops.Count);
                    ops.Add(new Op(CmdPlttBase, 0, 0, 0, 1));
                }

                ops.Add(new Op(CmdColor, BaselineColor555, 0, 0, 1));
            }

            // PLTT_BASE and a baseline white COLOR always follow a *textured* TEXIMAGE_PARAM
            // (confirmed by direct trace of the true pristine FarFarAway.trk). PLTT_BASE's own
            // value is always 0, but the texRefEntry's plttbase_dl_offset pointing *at* it is
            // very much not — see plttBaseOpIndex. Untextured groups skip PLTT_BASE entirely.
            EmitGroupHeader();

            // COLOR state persists across BEGIN_VTXS/END_VTXS boundaries in the real decoder
            // (confirmed: a real chunk's second strip within the same texture group never
            // re-emits COLOR at all when it hasn't changed) — one tracker for the whole group,
            // not reset per strip.
            uint lastColor555 = BaselineColor555;

            // Strips can only share vertices within one primitive's own local vertex block (each
            // TrkImportPrimitive is independent/non-shared — see its own remarks), so triangles
            // are stripped per-primitive, not across the whole texture group. Real multi-triangle
            // strips (mode 2) each get their own BEGIN_VTXS/END_VTXS block, but unstrippable
            // singles from every primitive are batched into one shared mode-0 block — separate
            // triangles need no edge-sharing between them, so mixing primitives there is fine, and
            // giving every leftover single its own BEGIN_VTXS/END_VTXS (an earlier version of this
            // code did, one per primitive) adds opcode overhead that measurably outweighed what
            // stripping saved.
            // Blocks to emit for this texture group, in order. Each is one BEGIN_VTXS/END_VTXS
            // run: a mode-2 strip's chained vertex sequence, or a mode-0 batch's 3-vertices-per-
            // triangle sequence.
            // Each entry pairs a vertex index with its owning primitive: indices are local to a
            // primitive's own vertex block, and the shared mode-0 batch below can mix primitives.
            // StartsGroup marks a block that reopens TEXIMAGE_PARAM — see TrkImportPrimitive.GroupBreaks.
            List<(int Mode, List<(TrkImportPrimitive Prim, int V)> Verts, bool StartsGroup)> blocks = [];
            List<(TrkImportPrimitive Prim, int A, int B, int C)> singles = [];

            foreach (IGrouping<TrkImportPrimitive, (TrkImportPrimitive Prim, int A, int B, int C)> byPrim in group.GroupBy(t => t.Prim))
            {
                TrkImportPrimitive prim = byPrim.Key;
                List<(int A, int B, int C)> tris = [.. byPrim.Select(t => (t.A, t.B, t.C))];

                // Prefer the source file's own recorded block layout when it survived the round
                // trip (see TrkImportPrimitive.Blocks) — it reproduces the original's strip-vs-loose
                // choices exactly, which no heuristic can, and is what makes an unmodified round
                // trip byte-identical. BuildStrips is the fallback for glTF files without it.
                List<(int Mode, List<int> Verts)>? recorded = SplitByBlocks(tris, prim.Blocks);
                if (recorded is not null)
                {
                    IReadOnlyList<int> breaks = prim.GroupBreaks ?? [];
                    for (int bi = 0; bi < recorded.Count; bi++)
                    {
                        (int mode, List<int> verts) = recorded[bi];
                        blocks.Add((mode, [.. verts.Select(v => (prim, v))], breaks.Contains(bi)));
                    }

                    continue;
                }

                foreach ((int mode, List<int> verts) in BuildStrips(tris))
                {
                    if (mode == 2)
                    {
                        blocks.Add((2, [.. verts.Select(v => (prim, v))], false));
                        continue;
                    }

                    // Every primitive's trailing list joins one shared mode-0 block appended
                    // after the strips: separate triangles need no edge sharing, so mixing
                    // primitives is fine, and giving each its own BEGIN_VTXS/END_VTXS (an earlier
                    // version did) costs more opcode overhead than the stripping saves.
                    for (int v = 0; v + 2 < verts.Count; v += 3)
                    {
                        singles.Add((prim, verts[v], verts[v + 1], verts[v + 2]));
                    }
                }
            }

            if (singles.Count > 0)
            {
                List<(TrkImportPrimitive, int)> flat = [];
                foreach ((TrkImportPrimitive sPrim, int a, int b, int c) in singles)
                {
                    flat.Add((sPrim, a)); flat.Add((sPrim, b)); flat.Add((sPrim, c));
                }

                blocks.Add((0, flat, false));
            }

            foreach ((int mode, List<(TrkImportPrimitive Prim, int V)> verts, bool startsGroup) in blocks)
            {
                if (startsGroup)
                {
                    // The source reopened TEXIMAGE_PARAM here, even though the texture is
                    // unchanged — see TrkImportPrimitive.GroupBreaks. The baseline COLOR that
                    // comes with it resets the group's colour state too.
                    EmitGroupHeader();
                    lastColor555 = BaselineColor555;
                }

                ops.Add(new Op(CmdBeginVtxs, (uint)mode, 0, 0, 1));

                // Position state restarts per block: a block's first vertex is always VTX_16
                // (there is nothing to repeat yet), confirmed by all 9732 real first-in-block
                // vertices being VTX_16.
                int stripPos = 0;
                (int X, int Y, int Z) lastQuant = default;

                foreach ((TrkImportPrimitive vPrim, int v) in verts)
                {
                    _ = vertexOrdinal.TryGetValue(vPrim, out int ordinal);
                    vertexOrdinal[vPrim] = ordinal + 1;

                    // Prefer the source file's own recorded TEXCOORD emission for this vertex;
                    // without one, fall back to "always emit at the start of a block", which is
                    // what real chunks do in the overwhelming majority of cases.
                    // A recorded flag is authoritative. Without one, restate the coordinate at
                    // the start of a block - but only once the chunk has one to restate, or an
                    // untextured chunk picks up a TEXCOORD it never had.
                    bool forceTexcoord = vPrim.TexcoordEmit is { } flags && ordinal < flags.Count
                        ? flags[ordinal]
                        : stripPos == 0 && hasTexcoord;

                    EmitVertex(
                        ops, vPrim, v, translate, scale, ref lastColor555, ref lastTexcoord,
                        ref hasTexcoord, ref stripPos, ref lastQuant, forceTexcoord);
                }

                EmitEndVtxs(ops);
            }
        }

        // Every real chunk's DL ends with MTX_POP (0x12), one param, always exactly 1 — the
        // matching pop for the MTX_PUSH this chunk opened with. (An earlier reading treated 0x12
        // as an unknown/undocumented opcode that merely "happened to" carry the value 1; it is in
        // fact the standard GX MTX_POP, whose parameter is the signed number of stack levels to
        // pop, so "always 1" is simply "pop the one level we pushed".) It always immediately
        // follows the chunk's very last END_VTXS — packed into whatever slot naturally follows,
        // with the same EmitEndVtxs padding rule applying — and the DL ends there, padded with
        // NOPs only to complete that final pack word. Unconditional here: this point is only
        // reachable for chunks that have geometry (empty ones returned early above), and real
        // data is exactly balanced 1:1 push/pop with zero exceptions across all 16 tracks.
        ops.Add(new Op(CmdMtxPop, 1, 0, 0, 1));

        byte[] dl = SerializeOps(ops, out Dictionary<int, int> byteOffsets);

        // texCount counts only *textured* groups — untextured groups (see above) get no
        // texRefEntry at all, so texImageOpIndex.Count (not groups.Count) is the real header value.
        int texCount = texImageOpIndex.Count;
        byte[] result = new byte[ChunkHeaderSize + (texCount * TexRefEntrySize) + dl.Length];

        WriteU16(result, 0x00, (ushort)chunkIndex);
        WriteU16(result, 0x02, chunk.ChunkType); // see TrkChunk.ChunkType's remarks
        WriteU32(result, 0x04, (uint)dl.Length);

        // Header +0x08 is not an independent field: it is a *duplicate* of this chunk's own
        // MTX_SCALE parameter, in the same raw 20.12 form. Verified exhaustively — it matches
        // for all 3988 non-empty chunks across all 16 shipped tracks, with zero exceptions.
        // It must therefore be written from the scale actually emitted below, never copied from
        // the template: FitTransform will pick a larger scale whenever edited geometry outgrows
        // the template's, and a copied value would then contradict the display list. (Copying it
        // was harmless only as long as the scale never changed, i.e. for unmodified round trips.)
        WriteU32(result, 0x08, EncodeFixed20_12(scale));
        WriteU32(result, 0x0C, chunk.PackedParams);
        WriteU32(result, 0x10, (uint)texCount);
        WriteU32(result, 0x14, 0); // reserved

        for (int g = 0; g < texCount; g++)
        {
            int entOff = ChunkHeaderSize + (g * TexRefEntrySize);
            WriteU32(result, entOff, (uint)byteOffsets[texImageOpIndex[g]]);
            WriteU32(result, entOff + 4, (uint)byteOffsets[plttBaseOpIndex[g]]);
            WriteU32(result, entOff + 8, (uint)textureSlot1Based[g]);
        }

        dl.CopyTo(result, ChunkHeaderSize + (texCount * TexRefEntrySize));
        return result;
    }

    /// <summary>
    /// One scale/translate pair for the whole chunk, sized so every vertex's <c>VTX_16</c>
    /// fixed-point value stays inside its raw int16 range. See the type's remarks.
    /// </summary>
    /// <summary>
    /// Translate is always the chunk's own anchor position — confirmed exactly (386/386,
    /// zero exceptions) to equal the real <c>MTX_TRANS</c> value in every non-degenerate chunk
    /// of the true pristine FarFarAway.trk, not a recomputed triangle bounding-box center.
    /// Scale prefers <see cref="TrkChunk.TemplateScale"/> exactly when the chunk's actual
    /// (possibly edited) geometry still fits inside it — see that property's remarks for why a
    /// real chunk's scale can't be derived from its own triangles' bounds at all (it reflects
    /// the original level tool's nominal cell size, confirmed strictly larger than the minimum
    /// needed for 174 of FarFarAway's 386 chunks) — and only falls back to computing a fresh
    /// scale when it doesn't fit, or when no template value is available.
    /// </summary>
    private static (Vector3 Translate, float Scale) FitTransform(TrkChunk chunk)
    {
        Vector3 translate = chunk.Position;

        if (chunk.Triangles.Count == 0)
        {
            return (translate, chunk.TemplateScale ?? 1f);
        }

        // Three straight calls rather than a loop over a stackalloc: a stackalloc inside a loop
        // allocates once per iteration and holds every one of them until the method returns, so
        // a chunk with tens of thousands of triangles walked the thread off the end of its
        // stack - and a StackOverflowException cannot be caught, so PluginGuard would not have
        // contained it either.
        float maxAbs = 0f;
        foreach ((TrkImportPrimitive prim, int a, int b, int c) in chunk.Triangles)
        {
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, a, translate));
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, b, translate));
            maxAbs = MathF.Max(maxAbs, LocalExtent(prim, c, translate));
        }

        if (chunk.TemplateScale is { } templateScale && maxAbs <= templateScale * VtxRange)
        {
            return (translate, templateScale);
        }

        // Fallback: smallest power-of-two scale that fits — matches the real format's own
        // convention (every real MTX_SCALE value observed is a power of two) even though
        // nothing in this app's own parser requires it.
        float scale = 1f;
        while (maxAbs > scale * VtxRange)
        {
            scale *= 2f;
        }

        return (translate, scale);
    }

    /// <summary>How far one vertex sits from the chunk's anchor, along its furthest axis.</summary>
    private static float LocalExtent(TrkImportPrimitive prim, int vertex, Vector3 translate)
    {
        Vector3 local = new Vector3(
            prim.Positions[vertex * 3],
            prim.Positions[(vertex * 3) + 1],
            prim.Positions[(vertex * 3) + 2]) - translate;

        return MathF.Max(MathF.Abs(local.X), MathF.Max(MathF.Abs(local.Y), MathF.Abs(local.Z)));
    }

    /// <summary>
    /// Splits a group's triangles the way the shipped tracks are split: a run of strips, then
    /// one trailing list of everything that would not strip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recovered by replaying candidate rules against all 9145 texture groups in both games and
    /// keeping what reproduced them. Three constraints came out of that, and together they
    /// account for 98.9% of Super Star Kartz's groups and 99.3% of Madagascar Kartz's:
    /// </para>
    /// <list type="number">
    /// <item>
    /// The pass is sequential. It only ever extends the current strip with the very *next*
    /// triangle, never searching ahead for one that would fit. Exactly one group in 9145 is
    /// stripped better than a sequential pass manages.
    /// </item>
    /// <item>
    /// It never rotates a triangle to make it fit. A strip continues only when the candidate is
    /// already written as <c>(x, y, z)</c> for the pair the strip needs. Allowing the cyclic
    /// rotation an earlier version permitted costs 159 groups per game, and not one group that
    /// does reproduce ever needs it.
    /// </item>
    /// <item>
    /// The fallback is final: once a triangle cannot open a strip, every remaining triangle in
    /// the group goes out as one list. Of the 9145 shipped groups, not a single one has a strip
    /// after a list.
    /// </item>
    /// </list>
    /// <para>
    /// What is left over is one genuinely ambiguous case, 83 groups across both games: a group
    /// that opens with a two-triangle strip and then falls back. 52 of those ship as a pure list
    /// and 20 keep the strip in Super Star Kartz (31 and 7 in Madagascar Kartz), with no property
    /// of the geometry separating them — the choice rests on something the TRK does not carry.
    /// This keeps the strip, the commoner reading among groups this rule can still get right.
    /// </para>
    /// </remarks>
    private static List<(int Mode, List<int> Verts)> BuildStrips(
        List<(int A, int B, int C)> triangles)
    {
        List<(int Mode, List<int> Verts)> runs = [];
        int i = 0;

        while (i < triangles.Count)
        {
            (int a, int b, int c) = triangles[i];
            List<int> strip = [a, b, c];
            int consumed = 1;

            while (i + consumed < triangles.Count)
            {
                int next = strip.Count; // 0-based index the next vertex would occupy
                int last = strip[next - 1];
                int prev = strip[next - 2];

                // Even index keeps (prev, last); odd index swaps to (last, prev).
                (int x, int y) = next % 2 == 0 ? (prev, last) : (last, prev);

                (int ta, int tb, int tc) = triangles[i + consumed];
                if (ta != x || tb != y)
                {
                    break;
                }

                strip.Add(tc);
                consumed++;
            }

            if (consumed >= 2)
            {
                runs.Add((2, strip));
                i += consumed;
                continue;
            }

            // Stripping is over for this group: everything left goes out as one list.
            List<int> tail = new((triangles.Count - i) * 3);
            for (int t = i; t < triangles.Count; t++)
            {
                (int ta, int tb, int tc) = triangles[t];
                tail.Add(ta);
                tail.Add(tb);
                tail.Add(tc);
            }

            runs.Add((0, tail));
            break;
        }

        return runs;
    }

    /// <summary>
    /// Rebuilds the source file's own <c>BEGIN_VTXS</c> blocks from a recorded
    /// <paramref name="blocks"/> layout (see <see cref="TrkImportPrimitive.Blocks"/>), returning
    /// each block's primitive mode and vertex sequence. Returns <see langword="null"/> — meaning
    /// "fall back to <see cref="BuildStrips"/>" — when no layout was recorded, when the runs don't
    /// account for exactly <paramref name="triangles"/>, when a mode this method can't rebuild is
    /// present, or when a mode-2 run's triangles don't actually chain into a strip (which would
    /// mean the geometry was edited after the layout was recorded, making it stale).
    /// </summary>
    private static List<(int Mode, List<int> Verts)>? SplitByBlocks(
        List<(int A, int B, int C)> triangles, IReadOnlyList<(int Mode, int TriangleCount)>? blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return null;
        }

        if (blocks.Sum(b => b.TriangleCount) != triangles.Count)
        {
            return null;
        }

        List<(int, List<int>)> result = [];
        int at = 0;

        foreach ((int mode, int count) in blocks)
        {
            if (mode == 0)
            {
                // Separate triangles: three independent vertices each, no sharing — exactly how
                // real mode-0 blocks are laid out (they routinely re-emit a vertex the previous
                // triangle already used, which is where those zero-delta VTX_DIFFs come from).
                List<int> verts = new(count * 3);
                for (int i = 0; i < count; i++)
                {
                    (int a, int b, int c) = triangles[at + i];
                    verts.Add(a); verts.Add(b); verts.Add(c);
                }

                result.Add((0, verts));
            }
            else if (mode == 2)
            {
                (int sa, int sb, int sc) = triangles[at];
                List<int> strip = [sa, sb, sc];

                for (int i = 1; i < count; i++)
                {
                    int next = strip.Count;
                    int last = strip[next - 1], prev = strip[next - 2];
                    (int x, int y) = next % 2 == 0 ? (prev, last) : (last, prev);

                    int? third = MatchThirdVertex(triangles[at + i], x, y);
                    if (third is null)
                    {
                        return null; // recorded layout no longer matches the geometry
                    }

                    strip.Add(third.Value);
                }

                result.Add((2, strip));
            }
            else
            {
                // Modes 1 (separate quads) and 3 (quad strip) don't occur in this game's track
                // data; rebuilding a quad's original vertex order from its two decoded triangles
                // isn't attempted rather than guessed at.
                return null;
            }

            at += count;
        }

        return result;
    }

    /// <summary>
    /// If ordered triangle <paramref name="tri"/> can be written as <c>(x, y, z)</c> under a
    /// cyclic rotation (which preserves winding), returns <c>z</c>; otherwise <see langword="null"/>.
    /// </summary>
    private static int? MatchThirdVertex((int A, int B, int C) tri, int x, int y)
    {
        return tri.A == x && tri.B == y ? tri.C : tri.B == x && tri.C == y ? tri.A : tri.C == x && tri.A == y ? tri.B : null;
    }

    /// <summary>
    /// Appends <c>END_VTXS</c>, then — only when it lands as the 4th (last) opcode of its
    /// packed word — one full extra padding word (4 <c>NOP</c>s) before the next real opcode.
    /// Confirmed by direct word/slot-position tracing of the true pristine FarFarAway.trk:
    /// every one of its <c>END_VTXS</c> occurrences that happened to complete its own group of
    /// 4 was followed by a whole padding word (even though nothing needed realigning — the
    /// group was already word-complete), while occurrences landing in slot 0-2 had no padding
    /// at all and the next opcode packed in immediately. Skipping this reproduces the same
    /// *opcodes* correctly but not the same *bytes* — which matters for exact reproduction, and
    /// was the single largest remaining source of size/byte drift once vertex encoding, PLTT_BASE,
    /// and TEXIMAGE_PARAM were already correct.
    /// </summary>
    private static void EmitEndVtxs(List<Op> ops)
    {
        ops.Add(new Op(CmdEndVtxs));
        if (ops.Count % 4 == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                ops.Add(new Op(0x00));
            }
        }
    }

    private static void EmitVertex(
        List<Op> ops, TrkImportPrimitive prim, int v, Vector3 translate, float scale,
        ref uint lastColor555, ref uint lastTexcoord, ref bool hasTexcoord,
        ref int stripPos, ref (int X, int Y, int Z) lastQuant, bool forceTexcoord)
    {
        float px = prim.Positions[v * 3], py = prim.Positions[(v * 3) + 1], pz = prim.Positions[(v * 3) + 2];
        float u = prim.Uvs[v * 2], t = prim.Uvs[(v * 2) + 1];
        byte cr = prim.Colors[v * 4], cg = prim.Colors[(v * 4) + 1], cb = prim.Colors[(v * 4) + 2];

        // No NORMAL opcode: confirmed absent from every vertex in the true pristine track (track
        // geometry is vertex-color-shaded only) — the decoder defaults an unset normal to
        // (0,0,0), which is what every real vertex already decodes to. COLOR and TEXCOORD are
        // each only re-emitted when they change from the last one written, mirroring the sparse
        // pattern observed in real chunks (the decoder carries both as mutable state across
        // vertices — see SskTrack.DecodeChunk's cr/cg/cb and tu/tv locals — so redundant
        // re-emission was never necessary). An earlier version assumed TEXCOORD always differs
        // between consecutive vertices (true often enough in the one chunk first sampled to be
        // misleading) and always re-emitted it; real chunks skip it too whenever two vertices
        // genuinely share a UV, e.g. two corners of a strip meeting at the same texel.
        uint color555 = EncodeColor555(cr, cg, cb);
        if (color555 != lastColor555)
        {
            ops.Add(new Op(CmdColor, color555, 0, 0, 1));
            lastColor555 = color555;
        }

        // TEXCOORD state is chunk-scoped in the decoder (tu/tv are DecodeChunk locals that nothing
        // ever resets, starting at 0), so a value-change test against that state is what actually
        // determines correctness — and it always wins here, which is why a stale
        // TrkImportPrimitive.TexcoordEmit flag can never produce a wrong UV. On top of that,
        // `forceTexcoord` reproduces emissions the value test alone would not explain: either the
        // source file's own recorded flag, or (without one) the first vertex of each block, which
        // restates the coordinate already in state. Measured over both games, that block-start
        // restatement holds for 99.93% of Super Star Kartz's vertices and 98.69% of Madagascar
        // Kartz's; the rest are blocks that restate a coordinate nothing distinguishes from one
        // that does not, so a rebuild without the recorded flags cannot be exact. See
        // SskTrack.VertexTexcoordEmitted.
        uint texcoord = EncodeTexcoord(u, t);
        if (forceTexcoord || texcoord != lastTexcoord)
        {
            ops.Add(new Op(CmdTexcoord, texcoord, 0, 0, 1));
            lastTexcoord = texcoord;
            hasTexcoord = true;
        }

        float lx = (px - translate.X) / scale, ly = (py - translate.Y) / scale, lz = (pz - translate.Z) / scale;
        int qx = EncodeFixed4_12(lx), qy = EncodeFixed4_12(ly), qz = EncodeFixed4_12(lz);

        // VTX_DIFF encodes a 3x signed 10-bit delta from the previous vertex position, but this
        // format uses it for exactly one purpose: repeating the previous vertex's position
        // verbatim, as a 1-parameter opcode instead of VTX_16's 2. The rule is exact and has no
        // exceptions anywhere in the archive — across all 16 shipped tracks, all 9526 VTX_DIFF
        // occurrences carry a parameter of literally 0, and not one of the 84,105 VTX_16
        // occurrences repeats the preceding position. So: duplicate position (and not the first
        // vertex of a block, which has nothing to repeat) => VTX_DIFF 0; anything else => VTX_16.
        //
        // Duplicates are common because a mode-0 block emits three independent vertices per
        // triangle, so a triangle that reuses a vertex from the one before it re-states that
        // position. (Two earlier attempts got this wrong by treating VTX_DIFF as a general
        // small-delta compression: a greedy "use it whenever the delta fits ±511/4096" rule
        // regressed byte parity badly, and a later rule keyed on the vertex's position within its
        // block — a real correlation, since duplicates cluster at triangle boundaries — was only
        // an approximation of this one.)
        if (stripPos > 0 && qx == lastQuant.X && qy == lastQuant.Y && qz == lastQuant.Z)
        {
            ops.Add(new Op(CmdVtxDiff, 0, 0, 0, 1));
        }
        else
        {
            uint p0 = (ushort)qx | ((uint)(ushort)qy << 16);
            uint p1 = (ushort)qz;
            ops.Add(new Op(CmdVtx16, p0, p1, 0, 2));
        }

        lastQuant = (qx, qy, qz);
        stripPos++;
    }

    // ── Fixed-point / packed-color encodes (exact inverses of SskTrack.DecodeChunk) ─────

    private static int EncodeFixed4_12(float v)
    {
        return SMath.Clamp((int)MathF.Round(v * 4096f), short.MinValue, short.MaxValue);
    }

    private static uint EncodeFixed20_12(float v)
    {
        return unchecked((uint)(int)MathF.Round(v * 4096f));
    }

    private static uint EncodeTexcoord(float u, float v)
    {
        short us = (short)SMath.Clamp((int)MathF.Round(u * 16f), short.MinValue, short.MaxValue);
        short vs = (short)SMath.Clamp((int)MathF.Round(v * 16f), short.MinValue, short.MaxValue);
        return (ushort)us | ((uint)(ushort)vs << 16);
    }

    /// <summary>
    /// Packs an RGB888 vertex color back into the format <c>SskTrack</c> decodes with
    /// <c>Exp5</c> (5-to-8-bit bit replication) — truncation, deliberately not the
    /// perceptual <see cref="DsColor"/> tables the texture codec uses, so a round-tripped
    /// vertex color matches what the viewport actually displayed, not a hardware-accurate
    /// reconstruction the decoder never performs.
    /// </summary>
    private static uint EncodeColor555(byte r, byte g, byte b)
    {
        uint c = (uint)(r >> 3) | ((uint)(g >> 3) << 5) | ((uint)(b >> 3) << 10);
        return c & 0x7FFF;
    }

    // ── Packed display-list opcode stream ───────────────────────────────────

    private readonly record struct Op(byte Cmd, uint P0 = 0, uint P1 = 0, uint P2 = 0, int ParamCount = 0);

    /// <summary>
    /// Packs opcodes 4-per-word with their parameter words following in order, exactly
    /// mirroring <c>SskTrack.DecodeChunk</c>'s read loop. A final partial group of fewer
    /// than 4 opcodes is padded with <c>0x00</c> bytes — command 0 takes no parameters and
    /// does nothing on decode, so it is a safe filler. <paramref name="paramByteOffsets"/>
    /// records the resulting DL-relative byte offset of each <c>TEXIMAGE_PARAM</c>'s and
    /// <c>PLTT_BASE</c>'s own parameter word — both texRefEntry fields (<c>dlByteOff</c> and
    /// <c>plttbase_dl_offset</c>) point at real, distinct locations in real chunks (confirmed:
    /// <c>plttbase_dl_offset</c> is never 0, contrary to an earlier, wrong reading of stale
    /// data — see <c>BuildChunk</c>'s remarks), so both need this same lookup.
    /// </summary>
    private static byte[] SerializeOps(List<Op> ops, out Dictionary<int, int> paramByteOffsets)
    {
        paramByteOffsets = [];
        List<byte> bytes = [];
        int words = 0;

        void AppendWord(uint w)
        {
            bytes.Add((byte)(w & 0xFF));
            bytes.Add((byte)((w >> 8) & 0xFF));
            bytes.Add((byte)((w >> 16) & 0xFF));
            bytes.Add((byte)((w >> 24) & 0xFF));
        }

        for (int g = 0; g < ops.Count; g += 4)
        {
            int groupSize = SMath.Min(4, ops.Count - g);
            byte b0 = ops[g].Cmd;
            byte b1 = groupSize > 1 ? ops[g + 1].Cmd : (byte)0;
            byte b2 = groupSize > 2 ? ops[g + 2].Cmd : (byte)0;
            byte b3 = groupSize > 3 ? ops[g + 3].Cmd : (byte)0;
            AppendWord((uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24)));
            words++;

            for (int k = 0; k < groupSize; k++)
            {
                Op op = ops[g + k];
                if (op.Cmd is CmdTeximageParam or CmdPlttBase)
                {
                    paramByteOffsets[g + k] = words * 4;
                }

                if (op.ParamCount >= 1)
                {
                    AppendWord(op.P0);
                    words++;
                }

                if (op.ParamCount >= 2)
                {
                    AppendWord(op.P1);
                    words++;
                }

                if (op.ParamCount >= 3)
                {
                    AppendWord(op.P2);
                    words++;
                }
            }
        }

        return [.. bytes];
    }

    // ── Byte writers ─────────────────────────────────────────────────────────

    private static void WriteU16(byte[] buf, int off, ushort v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)(v >> 24);
    }

    private static void WriteI32(byte[] buf, int off, int v)
    {
        WriteU32(buf, off, unchecked((uint)v));
    }
}
