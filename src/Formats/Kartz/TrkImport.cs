using System.Numerics;

namespace Kartz.Formats;

/// <summary>
/// One glTF primitive's geometry, read as its own independent (non-shared) vertex block —
/// the shape a <see cref="TrkWriter"/> chunk is built from.
/// </summary>
/// <remarks>
/// This is a plain data carrier so it can sit in the format layer next to <see cref="SskTrack"/>
/// and <see cref="TrkWriter"/>; the glTF-aware code that produces it
/// (<see cref="Kartz.Formats.Gltf.TrkGltfReader"/>) lives above this layer, same as
/// <see cref="SskTexture"/>/<see cref="SskTextureEncoder"/> versus the app's PNG codec.
/// </remarks>
public sealed class TrkImportPrimitive
{
    public required float[] Positions { get; init; }  // 3/vert, world-space (baked from the node transform)
    public required float[] Normals { get; init; }     // 3/vert
    public required float[] Uvs { get; init; }          // 2/vert, pixel space (already × texture dimensions)
    public required byte[] Colors { get; init; }        // RGBA, 4/vert
    public required int[] Indices { get; init; }         // local to this primitive's own vertex block
    public required int TextureIndex { get; init; }      // 0-based into the target track's existing Textures[], or -1

    /// <summary>
    /// The original <c>.trk</c> chunk this primitive's triangles belong to, when known exactly —
    /// parsed from a <c>"chunk_&lt;index&gt;"</c> node name written by the app's own per-chunk
    /// glTF export. <see langword="null"/> when the source glTF wasn't authored that way (a
    /// non-Kartz file, or a node the user renamed), in which case
    /// <c>TrkChunker.ChunkToAnchors</c>'s nearest-anchor bucketing is the fallback.
    /// </summary>
    public int? ChunkIndex { get; init; }

    /// <summary>
    /// The source file's own <c>BEGIN_VTXS</c> block layout for this primitive, as
    /// <c>(mode, triangleCount)</c> runs in triangle order — read back from the
    /// <c>extras.kartzBlocks</c> array this app's own glTF export writes.
    ///
    /// <para>
    /// Whether a run of triangles was shipped as a mode-2 triangle strip or a mode-0 loose batch
    /// is <em>source</em> information: real tracks contain plenty of mode-0 blocks whose triangles
    /// would chain into perfectly valid strips, so no re-stripping heuristic can reproduce the
    /// original's choice for every chunk. Preserving it end to end is what makes an unmodified
    /// round trip byte-identical. <see langword="null"/> for glTF files that don't carry it (hand
    /// authored, or re-exported through another tool), in which case <c>TrkWriter</c> falls back
    /// to its own sequential stripper.
    /// </para>
    /// </summary>
    public IReadOnlyList<(int Mode, int TriangleCount)>? Blocks { get; init; }

    /// <summary>
    /// One flag per emitted vertex (in display-list emission order across this primitive's
    /// <see cref="Blocks"/>) recording whether the source file emitted a <c>TEXCOORD</c> opcode
    /// for it — read back from <c>extras.kartzTexcoordEmit</c>. See
    /// <see cref="SskTrack.VertexTexcoordEmitted"/>.
    ///
    /// <para>
    /// Purely advisory, and safe against edited geometry: <see cref="TrkWriter"/> emits a
    /// <c>TEXCOORD</c> whenever the value differs from the one already in effect regardless of
    /// what this says, so a stale flag can only ever cost a redundant opcode, never a wrong UV.
    /// </para>
    /// </summary>
    public IReadOnlyList<bool>? TexcoordEmit { get; init; }

    /// <summary>
    /// Indices into <see cref="Blocks"/> at which the source file opened a new
    /// <c>TEXIMAGE_PARAM</c> group — read back from <c>extras.kartzGroupBreaks</c>. Block 0 always
    /// starts a group and is left implicit.
    ///
    /// <para>
    /// A chunk can open several groups selecting the *same* texture, which grouping by texture
    /// index alone would merge. <see langword="null"/> or empty means one group for the whole
    /// primitive, which is the overwhelmingly common case.
    /// </para>
    /// </summary>
    public IReadOnlyList<int>? GroupBreaks { get; init; }
}

/// <summary>One spatial bucket of triangles destined for a single TRK chunk.</summary>
public sealed class TrkChunk
{
    /// <summary>World-space centroid of this chunk's triangles — the TRK chunk position table entry.</summary>
    public required Vector3 Position { get; init; }

    public required List<(TrkImportPrimitive Prim, int A, int B, int C)> Triangles { get; init; }

    /// <summary>
    /// The chunk header's own type field (offset +0x02). Confirmed by direct inspection of a
    /// real shipped track (FarFarAway): <c>0x000E</c> marks a "primary" chunk — there are
    /// exactly as many of these as the paired <c>.bpvs</c> has sections (87 for FarFarAway) —
    /// and <c>0x0000</c> marks a "detail" chunk that outnumbers primaries several-to-one (301
    /// for FarFarAway) and isn't tracked by <c>.bpvs</c> at all. Defaults to <c>0x000E</c> for
    /// chunking strategies that don't come from a real per-chunk source (e.g. <c>TrkChunker.Chunk</c>'s
    /// independent grid search) — <c>TrkChunker.ChunkToAnchors</c> overrides this per
    /// chunk from the template's own chunk types instead of guessing.
    /// </summary>
    public ushort ChunkType { get; init; } = 0x000E;

    /// <summary>
    /// The chunk header's field at +0x08. <b>Not an independent value: it is a duplicate of the
    /// chunk's own <c>MTX_SCALE</c> parameter, in the same raw 20.12 fixed-point form.</b>
    /// Verified exhaustively — it equals the emitted <c>MTX_SCALE</c> in all 3988 non-empty chunks
    /// across all 16 shipped tracks, with zero exceptions (the other 8 chunks have no display list
    /// at all). Which is why <see cref="TrkWriter"/> writes it from the scale it actually emits
    /// rather than copying this value: whenever edited geometry outgrows the template's scale the
    /// writer picks a larger one, and a copied header field would then contradict the display list.
    ///
    /// <para>
    /// An earlier revision of this comment claimed FarFarAway's 388 chunks were "all genuinely
    /// zero" here. That was wrong — no chunk in any shipped track has a zero at +0x08; the values
    /// cluster on clean powers of two (0x2000, 0x4000, 0x8000, …) precisely because they mirror
    /// the power-of-two <c>MTX_SCALE</c> values.
    /// </para>
    /// </summary>
    public uint CoordScale { get; init; }

    /// <summary>
    /// The chunk header's field at +0x0C — a densely populated ~17-21 bit value whose meaning is
    /// not decoded, and which this project's parser never reads. Unlike <see cref="CoordScale"/>
    /// it has no known relationship to anything else in the chunk, so it is preserved verbatim
    /// from the template rather than derived.
    /// </summary>
    public uint PackedParams { get; init; }

    /// <summary>
    /// The real chunk's own exact <c>MTX_SCALE</c> value, when known (see
    /// <c>Kartz.Formats.TrkWriter.ReadChunkVtxScales</c> for why this must be read from the
    /// template rather than computed from the triangles actually landing in this chunk —
    /// the real value reflects the original level tool's nominal cell size for this chunk
    /// index, not a bounding box of whatever geometry a reconversion happens to produce).
    /// <c>TrkWriter.BuildChunk</c> uses this exactly, unchanged, when the
    /// chunk's actual (possibly edited) geometry still safely fits inside it, and only falls
    /// back to computing a fresh safe scale when it doesn't — e.g. because the geometry was
    /// edited to extend past the original chunk's bounds.
    /// </summary>
    public float? TemplateScale { get; init; }
}
