# Formats

Ten plugins are built from this repository. The host knows nothing about any of them; each plugin teaches it one or more formats, and the host draws the panes and the edit commands from what the plugin says a row can do.

| Plugin | Opens | Reads | Written back |
| --- | --- | --- | --- |
| Harugeki | PC game archives | models, textures, materials, skeletons, animation | textures, geometry, raw payloads |
| Virtuos | Nintendo DS game archives | tracks, textures, zones, collision, visibility, cars, models, skeletons, animation | textures, tracks, models, skeletons, animation, collision |
| Nintendo | DS ROMs, WBFS containers, 3DS images | whole file systems, disc images | DS ROMs in full, WBFS by same-length patch |
| Polarbit | iOS game archives | models, tracks, scenes, textures, fonts, sounds, music, strings | textures, sounds, scene geometry, strings |
| DWSSK | console game WADs, sound banks, Wwise audio, Scaleform movies, Bink videos | textures, models, animation, levels, collision, navigation, data tables, strings, shaders, audio, video | textures, models, data tables, strings, shaders, audio, movie images, whole WADs and banks |
| SSNCR | `Packfile.dat` archives (GameCube, PS2, PSP) | GameCube, PS2 and PSP models with skeletons, skin, textures and animation; GameCube, PS2 and PSP texture dictionaries; file details for audio, video, collision and paths | nothing (read-only) |
| Disgaea DS | nothing of its own; rows inside a DS ROM | DSARC FL and DSARCIDX archives, DSEQ music, LZ10 maps and models, IMY images, SWAV and STRM sound | archive entries, DSEQ parts, LZ10 payloads, IMY images from PNG |
| PlayStation 3 | game discs | ISO 9660 file system | nothing (read-only) |
| Xbox 360 | game discs | game partition directory tree | nothing (read-only) |
| Standard Formats | no archives of its own | OGG, WAV, PNG, JPEG, GIF, BMP payloads | PNG replacement where the container allows |

The source code carries no comments. Where a line exists because of how the game behaves, the reason is recorded in that format's **Specification notes** below: a function the code copies, a rounding it reproduces, an order the game's lookups depend on, a layout fact measured over the shipped files. This is the specification of record. Most of these lines make an unedited file rebuild byte for byte, and no test catches a change to them. Read before simplifying. The notes were recovered from deleted code comments (`e8a7d81` for Harugeki and Virtuos, `f088509` for Polarbit, `b8b4583` for DWSSK) and from the Harugeki format write-up deleted in `ca066cc`. Each has been checked against the current code.

## Geometry space

Every plugin hands the host its geometry in the space glTF uses: right-handed, +Y up, with
matrices as sixteen floats row by row in System.Numerics' row-vector convention (translation
in the bottom row, as `MatrixOps` reads them), and texture `v = 0` at the top row of the
image. The viewport draws that space as it stands, and the glTF writer and reader copy it
through untransformed (see [ARCHITECTURE.md](ARCHITECTURE.md#rendering)). Units are left as
each format stores them.

| Plugin | Source space | Done at parse time | What the host gets |
| --- | --- | --- | --- |
| Harugeki | Direct3D 9: Y up, row vectors, about 1 cm per unit, left-handed according to the original write-up | X and Z negated, matrices conjugated to match | Y up, with the file's handedness kept (see [Axes and handedness](#axes-and-handedness)) |
| Virtuos | DS geometry engine, 20.12 and 4.12 fixed point | none: `GxRunner` places vertices as the display list does; UVs divided by the texture size | the game's own space |
| Polarbit | OpenGL ES: right-handed, Y up, texture v running upward | v flipped (`Axes.FlipV`), positions unchanged | the game's own space, which is the host's |
| DWSSK | as each platform stores it; bone translations in centimetres | none on positions (`GdScene.Arrays`); bone translations scaled by 0.00125 into the vertex space | the stored vertex space, without the game's 8.0 mesh scale |

## Harugeki

*Suzumiya Haruhi no Chourantou* and *Gekitou* (PC).

Opens the `data/*.bin` containers. Models, embedded `ringtex2` textures, materials,
skeletons, and animation all appear in the tree; animation clips play in the Animation pane.

A texture can be replaced from a PNG. A model can be replaced from a glTF file, and its
joints are mapped onto the model's bones by name, so a skinned model re-binds to its own
skeleton. Any payload that is not understood further can still be extracted and replaced as
raw bytes. Saving rewrites the container in place.

### Specification notes

All integers are little-endian and floats are IEEE-754 single precision.

#### Container

- A table of contents is a `u32` count, that many `u32` child offsets relative to the
  table's own start, then `0xFE` padding up to the first child. There is no magic number, so
  `RingArchive.LooksLikeArchive` and `RingArchive.ReadToc` accept a table only when it is
  self-consistent: a count below 8192, offsets inside the file and past the table, and the
  `0xFE` run intact.
- Offsets are not sorted and may repeat. Two slots with the same offset are one shared
  payload. An offset of 0 is an empty slot, kept as a placeholder row so row numbers match
  the file's. A child runs to the next larger distinct offset, and the last child runs to
  the end of its parent.
- `RingArchive.Serialize` writes each distinct payload once (slots that shared a payload,
  keyed by `RingNode.AliasKey`, still share it), pads children to 32 bytes with zeros, and
  writes `0xFE` only inside tables. A payload whose length is not a multiple of 32 reads
  back with up to 31 zero bytes after it. Every format in these archives carries its own
  length, so the padding has no effect, and it never accumulates.
- An animation blob starts with a count and a table of offsets, exactly like a table of
  contents. The walk has to recognise payloads (`AssetTypes`, plus the host's
  `IsKnownPayload` for OGG, WAV and PNG) before it descends, or it shreds an animation into
  its bone tracks.

#### Textures

- `RingTexture`: `ringtex2` is a 64-byte header (`"ringtex2"`, a 16-byte name at 0x10,
  width at 0x2C, height at 0x30) followed by uncompressed pixels, top-down with no row
  padding, so a texture is exactly `0x40 + w × h × 4` bytes. No compressed or palettised
  variant exists in the shipped data. Header bytes the plugin does not interpret are kept
  (`HeaderTail`).
- The channel order is R, G, B, A, not the B, G, R, A a Direct3D 9 surface uses. Over the
  140 face textures the channel means are 220/163/111, a skin tone, which read as B, G, R
  would be sky blue. The old write-up said B, G, R, A; the code and the measurement agree
  on R, G, B, A, so do not add a swap.

#### Models

- `RingModel`: version 1 at 0x00, node count at 0x04, texture count at 0x0C, embedded
  texture offsets from 0x10, node offsets from 0x50. The first node starts right after the
  node offset table, nodes are packed with no alignment, and the embedded textures follow
  the last node. A node is a mesh when the four bytes at +0x70 are `Mate` (the start of its
  first material name); otherwise it is a bone.
- `RingBone.Parse`: a bone is a 0x70-byte name, `related` at +0x70 (parent node index + 1,
  with no exceptions), the index of the mesh it skins at +0x80, its weight count at +0x84,
  a world-space bind matrix at +0x88, and `(u32 vertex, f32 weight)` pairs from +0x150. A
  bone node is exactly `0x150 + 8 × count` bytes, which is the parser's self-check. The
  matrix at +0xC8 is not read: a few weightless `DBS_` anchors carry one that is not the
  inverse of their bind, so the inverse is always computed (`MatrixOps.InvertOrIdentity`).
  `DBS_` and `DBE_` bones mark the start and end of hair, skirt and ribbon chains.
- `RingMesh.Parse`: per-material triangle counts at +0x30, up to 16, ending at the first
  zero, and their number is the material count (the `u32` at +0x28 that looks like one is
  not reliable). Material records of 0x44 bytes start at +0x70: a name, the texture index at
  +0x20 (0xFFFFFFFF when untextured) and an RGBA float colour at +0x24. Then comes the count
  block `(vertices, vertices, triangles, node index)`. `RingMesh.FindCountBlock` searches
  for it, and `ModelWriter` writes it at the fixed `0x70 + 0x44 × materials + 4`, which every
  shipped model follows. Skin positions start 0xDC past the count block, followed by the
  skin normals, then the draw stream. `ModelWriter` copies the 0xCC bytes before the vertex
  arrays without interpreting them (`PreambleSize`).
- `RingMesh.ReadDrawStream`: the draw stream is a plain triangle list with no index buffer.
  Each triangle is three 40-byte vertices (position, normal, UV with the origin top-left,
  colour stored B, G, R, A, then the skin vertex index as a `u16` stored twice), followed by
  a 28-byte trailer. All 608,460 shipped vertices hold the same index in both halves. The
  trailer's first three floats are the triangle's geometric normal in file space and its
  last four are zero (measured on 202,820 faces), and `ModelWriter.WriteFaceNormal`
  recomputes it. Triangles go to materials in the order of the count table. Every shipped
  vertex colour is neutral grey, so the colour order has no visible effect.
- Bone weight lists index the skin arrays, not the draw vertices, and `RingMesh.VertexIds`
  maps a draw vertex to its skin vertex. `SkinBinding` inverts the per-bone lists into the
  four heaviest influences per vertex and renormalises them. Out-of-range and non-positive
  entries occur in the shipped data and are dropped.
- `ModelTextures`: embedded textures are packed back to back at their exact encoded size,
  so replacing one at a new size moves only the textures after it. Exactly two places point
  into that run: the header table at 0x10 and the word at node + 0x24. This was established
  over 177 models, 338 textures and 2,917 nodes, and `VerifyNoStaleReferences` re-checks it
  on every splice.
- `ModelWriter` retargets rather than synthesises. Everything it does not interpret (the
  node table, each bone's fixed 0x150-byte header with its bind matrix, the material
  records, the preamble) is copied from the model being replaced, so the skeleton survives
  a geometry import, and so do the animations, which address bones by node index.
  `GltfImport` maps joints by name, then by position.

#### Animation

- `RingAnimation`: node count at 0x00 (the model's), duration in frames at 0x04, per-node
  track offsets from 0x08, the first landing at `8 + 4 × nodes`. A track is a 0x20-byte node
  name, a key count, then 68-byte keys (a 4×4 matrix and a time in frames), so it is exactly
  `36 + 68 × keys` bytes. Frames run at 60 fps (`RingAnimation.Fps`). Keys are sparse: a
  96-frame clip typically stores 25.
- `ModelPoser.BuildPalette`: key matrices are absolute, in the same world space as the bind
  matrices, and every track of a clip has the same key times. A bone's skinning matrix is
  therefore its inverse bind times its key, with no parent chain and no resampling.
- `ModelPoser.RigidFallback`: an unweighted mesh the clip does not name (an alternative
  costume's hat) stays where it is when its bounds enclose the origin, because it was
  modelled in its own space (`kk00`, `sister01`'s hat). Otherwise it rides the bone nearest
  its centre (`hat_kyon01`, which sits at head height).

#### Axes and handedness

- The engine is Direct3D 9: row vectors (`v' = v × M`), translation in the bottom row, Y up,
  and left-handed according to the original write-up.
- Positions and normals have X and Z negated on read (`RingMesh.ReadSkinArrays`,
  `RingMesh.ReadDrawStream`) and negated back on write (`ModelWriter.WriteMirrored`). Bind
  matrices and animation keys get the matching conjugation by `S = diag(−1, 1, −1, 1)`,
  that is `S × M × S` (`Transforms.Mirror`, called from `RingBone.Parse`; `Transforms.Mirrored`,
  called from `ModelPoser.SampleTracks`). It has to be a conjugation rather than a row or
  column negation, because only conjugation keeps `mirror(A) × mirror(B) = mirror(A × B)`
  and `mirror(A)⁻¹ = mirror(A⁻¹)`, and skinning composes mirrored matrices.
- Negating two axes is a half turn about Y, not a reflection. Y stays up, the winding is
  unchanged, and so is the file's handedness. The first version (`8cab252`) mirrored Z alone
  and reversed the winding, which does change handedness. `c29fe47` replaced that with the
  half turn, and its comments described the result as Z-up, which it is not. Nobody has
  checked against the game whether Harugeki models now show as mirror images of it.

## Nintendo

Nintendo DS ROM images (`.nds`, `.srl`), WBFS Wii containers, and 3DS cart images (`.3ds`,
`.cci`). The plugin reads DS ROMs through [SceneGate Ekona](https://github.com/SceneGate/Ekona).

### Nintendo DS

Every file in the ROM can be extracted or replaced. The banner icon shows in the Texture
pane and can be replaced from a PNG. The header and the banner text, in every language the
banner holds, are edited in the Properties pane. Saving rebuilds the whole image.

### Wii (WBFS)

Every disc in the container opens with it. The game partition's file system is read out of
the container's cluster table and shown as a tree. A file that keeps its length is patched
straight back into the clusters it lives in, so the rest of the container is untouched. The
partition data is decrypted block by block as it is read, so nothing large is held in memory.

A patch also rebuilds the partition's H0 to H2 hash chain and the master table's H3 entry,
because an image with a stale chain is rejected by Dolphin and by real hardware. File offsets
in the partition FST are 34-bit, so files past 4 GiB on dual-layer discs are read from the
right place.

### Nintendo 3DS

Each partition the NCSD table names opens as a folder over the NCCH it carries: the
executable file system with its code, banner, icon, and logo, and the read-only file system
exactly as the image stores it, folder by folder and file by file. 3DS images are read-only.

## Virtuos

*Hot Wheels: Battle Force 5*, *Pimp My Ride: Street Racing*, *Monster Jam: Path of
Destruction*, *Madagascar Kartz*, and *DreamWorks Super Star Kartz* (Nintendo DS).

Reads the GDA/GDF/GDT archive triple: tracks, textures, zones, collision, visibility, cars,
skinned models, skeletons, and animation. The resource database is picked up automatically
when it sits beside the archive, and the Console pane reports how many it found. The triple
is recognised wherever it turns up, so the archive opens and edits in place inside a DS ROM
as well as from a folder on disk.

A texture can be replaced from a PNG. Tracks, models, skeletons, animation, and collision
volumes can each be replaced from a glTF file. Saving rebuilds the `.gda` and the `.gdt`;
the `.gdf` name table is only read.

### Specification notes

These were measured on the retail ROMs of *DreamWorks Super Star Kartz* and *Madagascar
Kartz*, when this plugin was called Kartz, and many were confirmed on hardware and
emulators. Counts are over Super Star Kartz's 16 tracks unless a game is named. The track
rules apply to the Kartz track layout (a 0x38-byte header), which is the one
`TrkWriter.BuildCore` relocates.

#### Archive

- `DwkArchive.Load`: the `.gdf` is a `u32` count, that many `u32` name offsets relative to
  its string table, then NUL-terminated backslash paths. The `.gdt` is a zero `u32`, a
  `u32` count, then `(u32 offset, u32 size)` pairs. The `.gda` is the raw data.
- `DwkArchive.Pack`: the shipped archive deliberately has entries share identical content
  (327 of 1,770 entries, about 963 KB, such as a driver's player and AI texture variants or
  two tracks' skybox meshes), very likely because the engine caches VRAM uploads by GDA
  offset. The writer therefore stores byte-identical entries once, bucketed by SHA-256 and
  always confirmed by a byte comparison, which also keeps every unchanged entry at its
  offset. `VerifyRepack` re-reads the result. `DetectAlignment` takes the largest power of
  two, from 4096 down, that divides every real offset.

#### Textures

- `SskTexture`: 5TX (an NTTX header, then PALT and IMGE sections) is NitroPaint's "iStudio"
  container, and the decoder is a port of NitroPaint's `TxReadIStudio`, including its RGB555
  rounding tables (`DsColor`: rounding tables, not bit replication). Formats are 1 A3I5,
  2, 3 and 4 palettes of 4, 16 and 256 colours, 5 4×4 blocks, 6 A5I3 and 7 direct RGB555.
  Texel arrays are sized to the padded power-of-two dimensions, not the original size.
- `SskTexture.ApplyColorKey`: transparency is a colour key. Any pixel that decodes to pure
  magenta (255, 0, 255) becomes transparent, whatever the format. The IMGE colour-0
  transparency flag (`c0xp`) is barely used (1 texture of 1,422 in Super Star Kartz, 5 of
  1,045 in Madagascar Kartz). `SskTextureEncoder` maps alpha below 128 to magenta (palette
  slot 0, or the 4×4 format's transparent block mode) and keeps `c0xp` as it was.
- `SskTexture.ParseForTrack`: tracks ignore `c0xp`. On hardware, colour-0 transparency is
  bit 29 of `TEXIMAGE_PARAM`, and all 9,145 textured groups in both games' tracks carry
  0x40000000 or 0, so the bit is always clear. Honouring the flag punched holes in
  Madagascar's water and lava (`NY_water`, `WH_river`, `VO_lava_islands`).
- The NTTX words at 0x0C (`TextureParam`, 0x0010 on every shipped texture but one) and 0x0E
  (`FormatHint`) are read by nothing here, but the game almost certainly validates them, so
  a re-encode keeps the original's values.
- `SskTextureEncoder`, 4×4 format: the stored palette address (`code & 0x3FFF`) is shifted
  left by one on read, so a block's base index must be even. Bits 14 and 15 are mode flags,
  so a stored address past 0x3FFF (a palette past about 32,766 entries) would decode in the
  wrong mode, and the encoder refuses it. A 512×512 replacement can reach that limit.

#### Tracks

- `TrkWriter.BuildCore`: the chunk count is at 0x02 and the chunk offset table at 0x38.
  Five absolute offsets in 0x14 to 0x37 point into one contiguous run after the display
  lists (vertex-colour palette, texture-coordinate transform parameters, track-section
  definitions, material data, spatial BVH). Nothing here decodes that run, so it is copied
  verbatim and every header word that points into it moves by the change in size.
- `TrkWriter.RelocateOffsetTable`: the section at 0x34 begins with its own table of absolute
  file offsets to its records, `count` of them (from 0x30), contiguous, strictly ascending,
  the first exactly `sectionStart + 4 × count` (11 to 19 entries on every shipped track).
  They have to move by the same delta. Missing them was the long-standing load-time crash on
  hardware and emulators, hidden because a same-size rebuild leaves them right. Only this
  table is relocated: neighbouring fields hold 20.12 coordinates whose raw values look like
  addresses (0x21000 is both offset 135,168 and 33.0), and a table that fails the checks
  above is left alone.
- `TrkChunker.ChunkToAnchors`: the chunk count must equal the template's, because the copied
  tail still describes the original chunks (87 sections in FarFarAway) and a one-chunk
  rebuild crashed every emulator tested. New geometry is bucketed to the template's own
  chunk positions (`TrkWriter.ReadChunkPositions`), so chunk *i* keeps roughly its world
  location; the tail and the `.bpvs` are keyed by chunk index, and an independent grid
  caused per-chunk corruption and a strict emulator's rasteriser crash.
  `ChunkByExplicitIndex` uses the `chunk_<n>` mesh names of the plugin's own export for an
  exact match. The grid fallback (`TrkChunker.Chunk`) anchors cells to the mesh's bounds,
  not the origin, since FarFarAway's X extent runs from −2351 to 895.
- Chunk header, 24 bytes (`TrkWriter.BuildChunk`): the type at +0x02 (0x000E for a primary
  chunk, which the `.bpvs` tracks, 0x0000 for a detail chunk; FarFarAway has 87 primary and
  301 detail chunks of 388), the display-list size at +0x04, `coord_scale` at +0x08, an
  undecoded word at +0x0C copied from the template (`ReadChunkExtraFields`), and the
  texture-reference count at +0x10. `coord_scale` duplicates the chunk's own `MTX_SCALE` in
  20.12 (in all 3,988 non-empty chunks), so it is written from the scale actually emitted.
- `TrkWriter.BuildChunk`, matrix: a display list opens with `MTX_PUSH` (0x11, which the
  decoder once mislabelled `MTX_IDENTITY`), `MTX_TRANS`, `MTX_SCALE`, and ends with `MTX_POP`
  (0x12) with parameter 1. The order matters on hardware, where both opcodes multiply the
  current matrix: TRANS then SCALE places a vertex at `trans + scale × v`, which is what the
  decoder's plain state assignment computes, while the other order decodes the same here and
  is wrong on hardware. Pushes and pops balance one to one across the archive; the DS matrix
  stack is 31 deep, so a chunk that pushes without popping leaks a level on every load.
- `TrkWriter.FitTransform`: `MTX_TRANS` is exactly the chunk's anchor position (386 of 386 in
  FarFarAway). `MTX_SCALE` is a power of two reflecting the level tool's nominal cell size,
  not the geometry's bounds (larger than needed in 174 of 386 chunks), so the template's
  scale (`ReadChunkVtxScales`) is kept whenever the geometry still fits.
- `TrkWriter.BuildChunk`, textures: a textured `TEXIMAGE_PARAM` is always 0x40000000 in the
  files (the engine evidently patches in the VRAM address at load, from the
  texture-reference table) and is followed by `PLTT_BASE` 0 and a baseline white `COLOR`
  (0x7FFF) before `BEGIN_VTXS`. Untextured geometry has no texture-reference entry and no
  `PLTT_BASE`: the chunk's texture count stays 0 and one `TEXIMAGE_PARAM` of 0 sits in the
  display list.
- `TrkWriter.EmitVertex`: there is no `NORMAL` opcode, since tracks are lit by vertex colour
  (and `TrackScene` hands them over as `SceneLighting.VertexBaked`). `COLOR` and `TEXCOORD`
  are emitted only when they change; colour state carries across blocks in a group, and
  texture-coordinate state is per chunk, starting at (0, 0). `VTX_DIFF` is used for one
  thing only: repeating the previous vertex's position, with parameter 0 (all 9,526
  occurrences, while none of the 84,105 `VTX_16`s repeats one). The first vertex of a block
  is always `VTX_16`. An earlier writer that emitted `NORMAL` for every vertex and skipped
  `PLTT_BASE` made files about 68% larger, and is the leading suspect for "Undefined Opcode"
  crashes on hardware.
- `TrkWriter.EmitEndVtxs`: an `END_VTXS` that lands as the fourth opcode of its packed word
  is followed by a whole padding word of four NOPs. Without this the opcodes match and the
  bytes do not.
- `TrkWriter.TryCopyTemplateChunk`: an empty chunk is a bare 24-byte header with a
  display-list size of 0 (8 of 3,996 chunks), but Madagascar Kartz also ships a hollow
  display list with no vertices (Volcano chunk 106, 44 bytes). A chunk left with no geometry
  is copied from the template, so whichever shape the game used survives.
- `TrkWriter.BuildStrips`: replaying candidate rules over all 9,145 texture groups of both
  games shows the original tool strips sequentially (it only extends a strip with the very
  next triangle), never rotates a triangle to make it fit, and once a triangle cannot open a
  strip, sends everything left as one mode-0 list. That reproduces 98.9% of Super Star
  Kartz's groups and 99.3% of Madagascar Kartz's. The rest cannot be told apart from the
  geometry, so the export records the real block layout (`virtuosBlocks`), the per-vertex
  `TEXCOORD` emissions (`virtuosTexcoordEmit`) and the group restarts (`virtuosGroupBreaks`)
  in glTF `extras` (`TrackScene.RoundTripHints`), and `TrkWriter.SplitByBlocks` follows them
  when they are present. Quad modes 1 and 3 never occur.
- `TrackScene.BuildMesh`: vertices are shared only when every attribute is bit-for-bit
  equal. A re-import has nothing but index sharing to tell it which corners were one vertex,
  and it needs that to rebuild strips. Triangle order is kept within each (chunk, texture)
  group, because the recorded block layout is a run-length list in that order.
- `TrackTextureTable` and `TrkGltfReader`: the texture slot table comes from the `.rdf`
  databases beside the archive (`VirtuosResourceIndex`). A track can use a slot beyond the
  textures it owns (Madagascar's ShippingFreighter draws slot 34 with 33), and the game binds
  one texture to more than one slot. The export names each material `virtuos_tex_<n>` after
  its slot, and the import trusts that name before any pixel matching: a PNG round trip of a
  colour-keyed texture can change the RGB under transparent pixels, which sent 26% of an
  unmodified FarFarAway round trip to the wrong slot when pixels were the only clue. The
  slot is bounded only by a ceiling of 4095, never by how many textures resolved.
- Test against a file extracted from an unmodified ROM. The shipped `FarFarAway.trk` is
  137,452 bytes; for a long time a previously converted copy (231,264 bytes) was used as the
  baseline, and the round trip compared the writer against itself, which is how the opcode
  mistakes above went unnoticed.

## Polarbit

*Crash Bandicoot Nitro Kart 2*, *Crash Bandicoot Nitro Kart 3D*, *Krazy Kart Racing*,
*Reckless Getaway*, and *Raging Thunder 2* (iOS).

Reads the `.vfs` archives: models, tracks, scenes, textures, fonts, sounds, music, and
string tables. The older games store only name hashes, so the plugin recovers names from the
paths the data mentions, which names about half their files. The Console pane reports how
many names were recovered.

PIFF models play their animation clips in the Animation pane. PIFF tracks, and PXC cars,
tracks, and props render with their textures in the Viewport.

Textures can be replaced from a PNG, sounds and music samples from a WAV, and scene geometry
from a glTF file of any shape. LANG and FCOL string tables are edited in the Properties
pane. Saving rebuilds the archive in place.

### Specification notes

The deleted comments cited sections of a reverse-engineering report ("REPORT 8.1" and so on)
that is not in this repository.

#### Archive and names

- `VfsArchive.NameHash`: an entry's name hash is `h = h × 67 + toupper(c) − 113` over the
  path's bytes, mod 2³², recovered from the 649 names the 2011 games store. The payload
  table is sorted by hash.
- `VfsArchive.BuildNameTail`: in the 2011 games a name table follows the payloads: the
  names, NUL-terminated, then their total length, then a level-9 zlib copy of them that is
  sync-flushed and never finished.
- `NameRecovery.Recover`: the three older archives store hashes only. Every run of three or
  more path characters in the data, as single bytes and as UTF-16LE, is hashed as it stands
  and under `data/`, then joined with known and discovered directories. A hit counts only
  when the payload's kind fits the extension, which keeps chance matches near zero.
  Textures, sounds, modules and fonts name nothing, so they are not scanned; skipping them
  finds the same 995 names.
- `VfsEntry.Replace`: a payload is stored under the compression layers it came with (a PLZP
  wrapper or bare zlib, at the level the header names). A payload equal to the one first
  read takes back its original bytes, so an untouched entry rebuilds byte for byte.

#### Geometry and animation

- `Axes` (in `Payload.cs`): the engine is OpenGL ES, right-handed and Y-up, with texture v
  running upward. Positions and normals pass through unchanged (`ToHost`, `FromHost`), and
  only v is flipped (`FlipV`) to the host's downward v. Turning the models from Y-up to Z-up
  (+90° about X) showed every model lying on its back in the viewport, which is how the
  host's Y-up convention was confirmed.
- `PiffAnimation`: each key holds, per bone, a rotation about the bone's model-space pivot
  and an optional offset, both relative to the parent bone. The bind pose is the identity,
  and quaternions are stored w, x, y, z. The parent-relative reading was chosen over an
  absolute one because it stretches skinned edges least in 27 of 49 models, and the stored
  sign over its conjugate because it puts the drivers' hands forward on the wheel.
- `PxcScene.Apply`: an object's placement is a row-major 3×3 matrix and a translation,
  applied as `world = M × v + t`. A `PxcVertexBlock` holds a fixed-point position and UV,
  plus a normal when format bit 0x10 is set and an RGBA colour when bit 0x100 is.
- `PxcWriter.Bounds`: the radius, box centre and half size are 16.16 values of the
  untransformed vertices, as the game's exporter wrote them. `PxcWriter.Replace` never
  touches track scenery (LICM).

#### Sound and music

- `Ima`: PSND's IMA ADPCM runs high nibble first, with the predictor and step index starting
  at 0 and no block headers. `Ima.Encode` keeps the original nibble wherever it still gives
  the wanted sample and otherwise takes the nearest output, so unchanged audio re-encodes to
  the same bytes; the audio alone cannot settle ties.
- `PsndSound.Pcm16`: when the audio before it is unchanged, a trailing half frame in the
  original survives a replacement (two CNK2 songs end that way).
- `PmodModule`: a 42-byte header, then one level-9 zlib body of orders, patterns, PSND
  samples and instruments. `WithSample` re-deflates it at level 9.

#### Textures, fonts and strings

- `PvrTexture`: a PowerVR legacy (version 2) texture is a 52-byte header, then the whole mip
  chain, largest level first.
- `Pvrtc`: PVRTC 4 bpp (4×4 blocks) and 2 bpp (8×4 blocks) follow the PowerVR SDK decoder.
  Blocks are in Morton order with the y bit lowest (`Pvrtc.Order`). Colour A has 4-bit blue
  in opaque mode. The endpoint images are bilinear, with block colours at block centres,
  wrapping at the edges.
- Known limitation, `Pvrtc.Encode`: each block's endpoints are its per-channel minimum and
  maximum, found in one pass, and each texel then takes the modulation weight that best
  matches it under the decoder's own endpoint images. An optimising encoder (PVRTexTool)
  gives sharper results on detailed art.
- `PvrTexture.EncodeLevel`, linear formats: `round(c × max / 255)` exactly undoes the
  decoder's `c = v × 255 / max` at every channel depth, and grey formats take the luminance.
  Unchanged pixels give back the original file.
- `PfntFont`: a glyph table over one horizontal atlas strip. 8-bit fonts are palette colours
  with index 0 transparent, 3-bit alpha over a 5-bit palette index (flags 0xDE), or white
  with the byte as alpha. Glyph positions are fixed, so a replacement keeps the atlas size.
- `Plate`: atlas plates are named rectangles (TLPB, *Reckless Getaway*) or numbered ones
  (PLT, *Raging Thunder 2*).
- `LangTable`: one level-9 zlib stream of NUL-terminated strings per language, string *k*
  meaning the same thing in each; language names are NUL-terminated and padded to four
  bytes. `FcolTable`: ASCII keys, then UTF-16 values.
- `GltfInput.Convert`: the host computes area-weighted face normals for a glTF primitive that
  has none. A mesh whose normals are all exactly +Z is still treated as having none, because
  older hosts filled missing normals with +Z.

## DWSSK

*DreamWorks Super Star Kartz* on PlayStation 3, Xbox 360, Wii and 3DS. The DS version runs
on a different engine and is covered by the Virtuos plugin.

Opens the `HIG!` WAD containers. A level's `.wad`, `.wad.mem1` and `.wad.vram` are read
together as one tree. A texture a model names is looked up in its own WAD, then its other
parts, then the `global.wad` in a folder above it (inside a disc image, the one above the WAD on the
same disc, never another disc or archive), and last in the other WADs of the same
game folder, which are indexed the first time a texture is missing. That last step works
the same inside a disc image, where the index covers the disc's game folder. The TJZIP
compression is rebuilt byte for byte, so an untouched WAD saves to the same file. Sound banks
(`.bnk`), Wwise audio (`.wem`), Scaleform movies (`.swf`), Bink videos (`.bik`) and level
name tables (`.nog`) also open on their own. A single file extracted from a WAD opens without
reading its sibling files; they are read only when a texture is missing. Inside a PS3, Xbox
360, Wii or 3DS disc image the same files open in place, and the WAD parts there are grouped
the same way; a WAD row is decompressed the first time it is expanded, and an edit inside it
is kept in memory and written into the disc image once, when the archive is saved. If the
rebuilt WAD no longer fits the file it came from (a Wii partition file cannot change length),
the save stops with a message and nothing is written.

What each row does:

- `.htx` textures decode for every platform layout: PS3 swizzled or linear (header bit 0)
  and DXT, Xbox 360 linear with a padded pitch, Wii GX tiles and CMPR, 3DS CTPK. Formats and
  channel orders follow the tables in the game executables; PS3 32-bit textures, for example,
  are stored blue, green, red, alpha. Replacing one from a PNG re-encodes it in the original
  format; the lossless formats give back the same bytes for an unchanged PNG.
- `.gdw`, `.gds`, `.gdi`, `.gdt` and `.gdt.piece` models render with their textures. A world
  splits into one mesh per visibility zone. Skinned models carry their skeleton and play
  every animation from the `.animpack` files beside their `MASTER` folder. Geometry can be
  replaced from a glTF (the first LOD), and every model exports and imports as the JSON and
  `.bin` pair the reverse-engineering tools write. Vertex colors are alpha, red, green, blue
  on the Xbox 360 (its `D3DCOLOR`) and red, green, blue, alpha everywhere else.
- `.animpack` files play on their character's master model and export and import as JSON.
  Rotation keys decode the way the game does: each 10-bit component is `(c - 0.5) * step -
  max`, with `max` the square root of 0.5 and `step` `2 * max / 1023`, and the quaternion is
  normalized afterwards. Translations are sign-magnitude fractions of 100 times the range at
  0x40, scales of the range at 0x44, and key times are a byte scaled to the range at 0x48.
  Poses are built with the game's own single-precision math (rest pose, interpolation and
  bone matrices), so the skinning matrices match the Wii build's bit for bit.
- `.bog` level databases, `.p2b` data tables, `.lcl` string tables, `.fkd` font kerning,
  `.cam` cameras, `.csd` collision surfaces, `.nav` navigation meshes, `.col` and
  `.col.piece` collision and `.nog` name tables export and import as JSON. Navigation and
  collision meshes show in the Viewport and export as OBJ; level placements export as a
  glTF scene. Strings in a `.lcl` are edited in the Properties pane.
- `.vs_bin` and `.ps_bin` shaders (RSX, Xenos, PICA200) export as JSON with their
  disassembly and rebuild from edited JSON.
- `.arc` U8 archives on the Wii open as folders with their TPL images.
- `.wem` audio plays and exports as WAV with no outside tools. PCM, Wwise IMA, Nintendo DSP
  ADPCM, Wwise Vorbis and XMA2 all decode inside the plugin. Wwise IMA follows the game's
  routine: each 36-byte block starts with its header sample, then 63 nibbles, with each step
  `step * (2n + 1) / 8`. Vorbis decodes through a port of the Wii game's own decoder (Tremor
  with a floating-point floor, MDCT and window), so the samples match what the game plays bit
  for bit. A sound is replaced from a 16-bit WAV in its own codec: Vorbis is encoded with the
  sound's own Wwise setup and codebooks, and XMA2 is encoded as XMA2 with its seek table
  rebuilt. Sound banks list their events by name and hold their embedded media as rows.
- `.swf` Scaleform movies list their bitmaps as rows. JPEG bitmaps decode to the same pixels
  the game's libjpeg 6b gives. Each one previews and can be replaced from a PNG: lossless
  bitmaps stay lossless, and JPEG bitmaps are re-encoded as JPEG, so they lose a little
  quality.
- `.bik` Bink 1 videos (revisions `f` to `k`) play in the Video tab with their sound, and export
  as frames, a WAV soundtrack or a Matroska movie. The sound decodes through a port of the Wii
  game's Bink audio decoder: Ooura's split-radix DCT in single precision with the game's own
  tables, the game's rounding to 16 bits and its integer crossfade between blocks. Each packet
  stops at the sample count in its header, as the game's does. The PS3 build's decoder, which
  converts and crossfades with VMX, gives the same samples.
- Every WAD, U8 archive and sound bank extracts to a folder with a manifest and rebuilds
  from it, in the layout the Python tools use. An alias entry names its target by index
  only; manifests from the older tools, which also carry an `offset`, still rebuild.

### Specification notes

An address names the game function a line copies: 0x80xxxxxx addresses are in the Wii
executable, 0x82xxxxxx in the Xbox 360 executable, and 0x15293c in the 3DS code. The Wii
build is the reference: where the code reproduces floating-point behaviour, it is Broadway's
(the Wii CPU's), so the results match the Wii build bit for bit.

#### Animation keys and rigs

- `AnimPack.Short`: key values go to JSON with nine significant digits (`Py.FormatG(x, 9)`),
  the fewest that give back the exact single-precision value the game decodes.
- `AnimPack.SmRead`, `AnimPack.SmValue`: a translation or scale key component is
  sign-magnitude, decoded as the game decodes it (0x800c6f60 for translations, 0x800c7430
  for scales): the low bits divided by their maximum, then times the range, in single
  precision, with the top bit making it negative. The casts in `SmValue` are that
  single-precision order; folding them into one double expression can change the low bits.
- `AnimPack.SmWrite`: in single precision a 3-byte step can decode to the same value as its
  neighbour, so after rounding the writer searches two steps either side for the step the
  game decodes to exactly this value (at these widths there is one per value). This is what
  lets keys rebuild unchanged.
- `AnimRig.Read`: key times are a byte scaled to the range at 0x48 of each animation
  (`unknown_48`, decoded by 0x800c6dc0). The shipped files use 255/30 s.
- `AnimRig.TranslationScale`: bone translations, in the skeleton's rest poses and in
  animation keys, are centimetres. The game's palette builder (0x800cab00, through
  0x800ca970) scales them by 0.00125 into the space the skinned vertices are stored in. The
  mesh scale at mesh+4 (8.0) is applied only when the game draws; the plugin hands geometry
  over in the stored space, without it.
- `AnimRig.Fma`, `AnimRig.Add`: Broadway's single-precision float operations compute in
  double and round the result to single, so fused and summed values are rounded twice.
  These two helpers reproduce that. Swapping them for `MathF.FusedMultiplyAdd` or a plain
  `float` add can change the low bits, and then the skinning matrices stop matching the
  game.
- `AnimRig.Local`: the local matrix 0x800ca970 builds: the rotation 0x8000bd00 makes from the
  quaternion, each row scaled by S, then T on the bottom row. Row vectors throughout, as
  System.Numerics uses.
- `AnimRig.Mul`: the affine product `a × b` as 0x8008fc10 (bone worlds) and 0x800cab00
  (skin palette) compute it: each element is `fma(a2, b2, fma(a0, b0, a1 × b1))`, and the
  bottom row adds `b`'s translation last. The order of the terms inside each element is
  part of the result.
- `AnimRig.Slerp`: as 0x80091210 does it: the first quaternion is negated when the two point
  apart, and the blend falls back to linear weights, without renormalising, when they are
  within 1e-4 of each other (`1 − dot ≤ 0.0001`).
- `AnimRig.Lerp`: 0x80015ce0, `a + t × (b − a)`, fused.

#### Skinning matrices

- `GdScene.BoneWorlds`: rest-pose bone world matrices are built the way the game builds them
  (0x800cab00): each bone's rest pose as a local matrix (`AnimRig.Local(AnimRig.Rest(...))`),
  times its parent's world, in index order, so a parent has to come before its child. The
  inverse binds stored in the file undo these only to within float error, not exactly.
- `AnimRig.Pose`: a bone's skinning matrix is `Mul(inverseBind, world)`, the 0x800cab00
  palette product, with every product going through `AnimRig.Mul`.

#### String and surface tables

- `Lcl.Write`: the game finds a string by binary search with the hashes in descending order
  (0x800f5370), so the table is written sorted by hash, descending. The shipped files
  already are, so they rebuild unchanged. In any other order the game's lookups would fail.
- `Csd.Write`: the game finds a collision surface by binary search on its name hash
  (0x8008f030), so the table is written sorted by name hash, ascending. The shipped files
  already are, so they rebuild unchanged.

#### Models, materials and draw data

- `GdFile.GdtMaterial`: the material a `.gdt` draw renders with comes from the byte each
  platform's renderer reads: byte 0x0E of the draw record on the Wii (0x801588d0; bits 8 to
  15 of `flags`), byte 0x10 on the PS3 and Xbox 360 (0x8217dd08; the top byte of `flags`),
  and byte 0x00 on the 3DS (0x15293c; the low byte of `material`). The word at 0x00 is
  unused on the Wii and is the index buffer handle on the PS3 and Xbox 360.
- Draw data is laid out in that same material order, a stable sort of the draws by
  `GdtMaterial`. `GdFile`'s reader records a `data_order` list in the JSON only when a file
  departs from it, and the writer follows `data_order` when present, so both kinds of file
  rebuild byte for byte.
- `GdImport.PartOf`: a draw's hide group is a bit in the instance's hidden-parts mask, and
  the renderers (0x80157b80 on the Wii, 0x8217b740 on the Xbox 360) skip the draw when that
  bit is set. A draw made from glTF therefore takes the group its material's old draw had
  (`created`, or bits 16 to 23 of `flags`).
- `GdReader.Reader.MeshGdsWii`: GX sends a vertex's indices in attribute order: POS, NRM,
  CLR0, TEX0. In each index tuple, `t[0]` is the position, `t[2]` the colour and `t[3]` the
  texture coordinate.

#### Audio encoder

- Known limitation, `VorbisEncoder.Encode`: the encoder writes long blocks only (it takes
  the first long-block mode in the sound's own setup), so sharp attacks can pre-echo. If
  that proves audible, the fix is a transient detector that switches to the short-block
  mode.

## SSNCR

*Shrek Smash n' Crash Racing* (Torus Games, 2006) on GameCube, PlayStation 2 and PSP. The
engine is RenderWare 3.6 with Torus wrappers; every chunk header carries the library stamp
`0x1803FFFF`.

Opens `Packfile.dat` (`PAK\0`), on its own or in place inside a disc image. Entries whose
payload starts with `!CMP` are zlib streams with a little-endian unpacked size. The archive is
read-only.

What each row does:

- GameCube `.tex` texture dictionaries list their textures, and each decodes from its GX
  format (CMPR, CI4 and CI8 with an RGB565 or RGB5A3 palette, and the other GX formats).
- PS2 `.tex` texture dictionaries decode their PSMT4 and PSMT8 textures through an emulated
  GS memory, so the swizzled uploads come out as the game draws them.
- PSP `.tex` files decode every texture from its GE state (CLUT formats, 16- and 32-bit
  formats, swizzled or linear).
- GameCube and PS2 `.trm` and PSP `.psp` models render with their textures and play every
  animation they carry (skinned characters and rigid props alike), and the animations export
  with the model to glTF. A model's atomics are placed by the
  frames its instance table names, so a level shows every placed prop. Skinned characters
  carry their frame hierarchy as a skeleton with per-vertex bones and weights. A texture is
  looked up by name in the `.tex` of the model's own level folder, then
  `gcn/textures/structure`, then any other dictionary in the pack. Transparency follows
  RenderWare's rule of blending whenever the texture raster or the prelight colors carry
  alpha: a texture whose alpha is only 0 or 255 is drawn as a cutout, one with partial alpha
  is blended, and so is any mesh whose vertex alpha falls below 254. No shipped vertex color
  has alpha 255; 254 is the exporter's full opacity (on PS2 it is twice `0x7F`). Models with no atomics
  (camera and object animation files) show only their details.
- `.fsb` sound banks, the `.thp` movie, `.col`, `.wcol`,
  `.path` and `.dat` show their details only.

### Specification notes

The model layout was read from the GameCube USA `main.dol`: the reader at `0x80144304`
(entered from `0x80145834`, which picks it when the marker is `0xBADA5535` and otherwise the
byte-swapping reader at `0x80144abc`), the writer at `0x8014409c`, and the animation
relocation at `0x80143e14`. Every count below was checked against all 367 GameCube `.trm`
files and all 5413 GameCube textures with no exception.

#### Model header

A `.trm` is one RW struct chunk followed by `numAtomics` RpAtomic chunks (`0x14`) and nothing
else. The struct's payload begins with a 4-byte endian marker (`BA DA 55 35` on GameCube,
`35 55 DA BA` on PS2) that the chunk size does not count, so the atomics start at
`0x10 + size`. Then a 0x88-byte header in the marker's byte order:

| Offset | Field |
| --- | --- |
| `0x00`, `0x02` | reference count and global slot, overwritten on load |
| `0x04` | atomic count |
| `0x06` | instance count |
| `0x08` | first instance that may take the distance-fade render types (equals the instance count in 319 of 326 GameCube files) |
| `0x0A` | frame count |
| `0x0C` | frame name hash count |
| `0x0E` | named instance count |
| `0x10`, `0x12`, `0x14`, `0x16` | animations (32 bytes each), tracks (16), keys (8), keys (8) |
| `0x18` | count of 0x54-byte records |
| `0x1C`-`0x30` | pointer slots (atomics, instances, name hashes, named instances, parents, matrices); the loader at `0x80144304` overwrites them, so file values are stale exporter pointers |
| `0x34` | animation blob size (the loader replaces it with the blob pointer) |
| `0x38` | pointer slot for the 0x54-byte records |
| `0x3C` | second blob size (8-aligned allocation; replaced by its pointer) |
| `0x40`-`0x47` | zero in every file |
| `0x48` | name, 64 bytes, such as `models\|CreatureFairy.trm` |

The arrays follow in this order: instances `{u16 frame, u16 atomic, u32 ARGB colour}`, name
hashes `{u32 hash, u32 frame}`, named instances `{u32 name hash, u32 instance index}`, parents (`s16`, always lower than the
frame's own index), local matrices, the 0x54-byte records, the second blob, the animation
blob. The header size equals the sum of these exactly. Local matrices are RW row-vector 4x4
floats (translation in the last row); a frame's world matrix is its local times its
parent's world. The atomic's own RW struct holds stale pointers and is not used.

The instancing routine at `0x801459f0` builds one 0x34-byte runtime record per instance. It
takes the atomic from the instance's u16 at +2 and the frame's world matrix from the u16 at +0.
An instance colour of 0 becomes the default `0xFEFFFFFF`; any other colour is forced opaque
(`| 0xFF000000`), which shows the word is ARGB. The renderer's use of that colour is not traced
yet, so the viewer does not apply it; the properties panel lists how many instances set one.
Named instances let the game find parts by name (pinocchio names instances 4 and 7). Exported
meshes carry the hash in their name. No shipped model has 0x54-byte records; one GameCube file
has a second blob (3,242 bytes) whose contents are not decoded.

#### Animation

The animation blob (header +0x34 bytes, last in the struct) is, in order: animations (32
bytes), tracks (16), keys (8), events (8), then the key data; the counts are header +0x10,
+0x12, +0x14 and +0x16, and the relocation at `0x80143e14` points each animation at the key
data. An animation is `{u32 name hash, u32 track count, f32 duration in seconds, u32 0, u16,
u16 event count, u32 first track, u32 first event, key data}`. A track is `{u32 bone name
hash, u32 key count, u32 type, u32 first key}`; the hash is the one in the model's frame name
table. A key is `{f32 time in seconds, u32 byte offset into the key data}`, and an event is
`{f32 time, u32 id}`. PSP holds the same records with pointers in place of the indices. Key
data is little-endian on every platform, GameCube included. The track type is a mask, and a
key holds its parts in this order: rotation (4, a quaternion x, y, z, w as s16 over 16384),
translation (2, three floats), uniform scale (1, a float). All 373,509 GameCube and PS2
rotation keys are unit length under that scale, all 385,728 key times rise, and the first
translation key equals the frame's bind translation in 81% of tracks. A key builds the local
matrix scale x rotation x translation in RenderWare's row-vector order; between keys the
rotation is slerped and the rest interpolated linearly. Frames without a track keep their bind
local. All 804 GameCube, 811 PS2 and 838 PSP clips pose without a non-finite value.

glTF export carries every clip losslessly. Rigid atomics in a rig become one-bone skins of
their frame, so clips move them. The skin's own inverse bind matrices are exported, including
the 27 that disagree with the frame hierarchy. Key parts go out as the decoded translation,
rotation and scale rather than decomposed matrices. Keys with a negative uniform scale
(weaponnose) and mirrored binds (windmills, cogs) would otherwise decompose inconsistently
from key to key. Clip extras hold the anim hash, exact duration, flags and events. Reimporting
every model on all six discs and posing each clip under glTF rules matches the viewer's pose
to within 6e-6 of model size. Rotation keys are normalized at decode.

Each platform's exporter reduced keys on its own. Props, creatures and the `structure*` rigs
carry the same keys everywhere and pose within 0.1% of model size across GameCube and PS2.
The `character*` and `mount*` clips do not: donkey has 5,006 GameCube keys against 6,688 on
PS2. GameCube keys still lie on the PS2 curve (PS2 sampled at GameCube key times is within
0.6 degrees at the 95th percentile), but the dropped keys include extremes, so between keys
the two platforms differ by up to 65 degrees. No interpolation (slerp, nlerp, Catmull-Rom)
recovers them, and the GameCube tables are complete (every key referenced, key data covered
exactly), so the coarser GameCube motion is the shipped data. Skinned vertices that never
move are all weighted to bones no clip animates (or are rigid atomics on unanimated frames),
the same set on all three platforms. Clip,
frame and event names are hashes; the hash function is not known yet.

#### GameCube native geometry

Geometry flags always include `rpGEOMETRYNATIVE`; the binmesh (`0x50E`) holds no indices,
only per mesh `{u32 index count, u32 material}`. The native data struct (`0x510`) begins with
three little-endian words: platform `6`, header size, data size. The header is big-endian: two
words, the attribute count, then per attribute `{u32 offset, u8 GX attribute, u8 stride, u8
index type, u8 0}`, then per binmesh mesh `{u32 display list offset, u32 length}`. Offsets
count from the end of the header. Attributes seen are position (9, three floats), normal
(10, three floats), color 0 (11, RGBA8), texture 0 and 1 (13 and 14, two floats); index type
2 is 8-bit and 3 is 16-bit. A display list is GX primitives, opcode `0x98` (triangle strip)
in every shipped file, a big-endian vertex count, and one index per attribute per vertex in
attribute order; it ends exactly at its stated length. GX strips are clockwise-front, so the
host's triangles are emitted reversed.

#### Skin

The skin plugin (`0x116`) struct is: u32 platform `6`, u8 bone count (always the frame
count), u8 used-bone count, u8 weights per vertex (2 or 3), a pad byte, the used bones,
`numVerts x weights` bone indices, the same count of weights as bytes where `0x80` is 1.0,
one inverse bind matrix per bone (RW 4x4, big-endian; the fourth column holds RW flags and
padding, not matrix data), and 12 trailing bytes. Bone indices are frame indices. Skin
vertices are the position array, which always has at least `numVerts` entries. Each used
bone's inverse bind matrix times its world matrix is the identity.

#### PS2 native geometry

The PS2 model header is the GameCube one in little-endian order. The native data chunk
(`0x510`) holds a struct header whose size field is wrong (it repeats the atomic's size), so
its payload is read by hand: u32 platform `4`, then per binmesh mesh `{u32 size, u32
no-pointers flag, size bytes}`, ending exactly at the chunk end. A mesh is a DMA chain of `cnt`,
`ref` and `ret` tags; `ref` addresses are quadwords from the mesh start, and the chain ends at
`ret`, after which the buffer holds `0xC5` allocator fill. Each tag's upper 8 bytes are VIF
codes that continue into its data. Every batch ends in `MSCAL`, with `ITOP` giving the vertex
count. Unpacks seen: position `V4-32` (the w word's bit `0x8000` is the GS ADC flag: no
triangle closes on that vertex) or `V3-32` at slot 0, UV `V2-32`, prelight `V4-8` unsigned
(the same bytes as the GameCube color array), normal `V3-8` signed over 127, and skin weights
`V4-32` at slot 3, where each word is a float weight with a matrix slot in bits 2 to 9
(`(w & 0x3FF) >> 2`, weight `w & ~0x3FF`). Slot 0 is not a bone: it only appears with weight
0, and slot `n` is bone `n - 1`. With that, 99.7% of PS2 skin influences match the GameCube
vertex at the same position. Geometry flags decide which of normals, colors and
UVs the model has. All meshes are triangle strips. Batches overlap by two vertices and every
batch length minus two is even, so strip parity restarts cleanly per batch. Strips without ADC
restart through repeated vertices, which make zero-area triangles that are dropped.

The PS2 skin struct is u32 platform `4`, bone count, used-bone count, weights per vertex, a
pad byte, the used bones, the inverse bind matrices (RW 4x4, little-endian) and 28 trailing
bytes. The same 27 used bones in `animgiantfrog`, `animswampbootm` and `weaponmirror` fail
`inverse bind x world = identity` on both PS2 and GameCube, so that mismatch is in the
shipped data.

#### PSP models and textures

PSP files have no RenderWare. The reader in `BOOT.BIN` (`0x1541d8`, dispatching through the
table at `0x224620`) walks chunks `{u32 size, u16 type, u16 version, payload}`. Type 1 is the
header (`6, 1, 0`). A model loads types 23, 19, 20 and 3 into blocks 0 to 3 (model struct, GE
display lists, vertices, string table `{u32 count, names}`); a texture file loads 18, 19, 21
and 22 (texture table, display lists, CLUTs, pixels). Type 2 lists pointers to relocate and
type 16 GE address words to relocate, each entry `block << 24 | offset`; a pointer's value is
encoded the same way. Type 17 lists texture binds: each site in the display lists holds a
string-table offset and is patched at load to a `BASE` + `CALL` of the named texture's list.

The model struct carries the same arrays as the GameCube header in its own order: u16 atomic
count at +4, frames +6, instances +8, name hashes +0xC; pointers to atomics +0x18, instances
+0x1C, name hashes +0x20, the second table +0x24, parents +0x28, local matrices +0x2C,
animation +0x38. Instances, hashes, parents and matrices are byte for byte the GameCube
values in little-endian order. An atomic record is 0x34 bytes: flags, u16 skin list count at
+4 (0 when rigid), u16 vertex count, u16 stride, bounding sphere at +0x18, and at +0x28 either
its display list or a skin record `{u32 used count, display lists, palette words, used bones,
inverse binds}`; the inverse binds are one RW 4x4 per used bone, the same values as the
GameCube skin (including the 27 that disagree with the hierarchy). A display list sets UV scale and offset (`0x48`-`0x4B`, applied to 16-bit
UVs over 32768), culling (`0x1D`) and front face (`0x9B`, 47 meshes flip it), the texture
placeholder, `VTYPE`, `BASE`/`VADDR` into the vertex block, then triangle-strip `PRIM`s; the
vertex pointer carries over from one draw and one skin list to the next. Every vertex type in
the game is 16-bit UV or none, 5650, 4444 or 8888 color or none, 8-bit normal or none, float
position, and 1 to 4 16-bit weights over 32768. Each skin list's palette word gives one byte
per weight, a 1-based index into the used-bone list. Skinned vertices are in bind space. Over
all 69 skinned models, 97.8% of the single-bone vertices that sit on a GameCube vertex name
the same bone (the rest lie on seams the PSP mesh tessellates differently).

PSP skinned strips repeat shared vertices with positions jittered by about 5e-4, so they draw
sliver triangles where other platforms have exact degenerates; the GE draws them too, and they
are kept. Leaving those out, skinned triangles wind like rigid ones (99%). Some vertex types
carry no normal (7,839 vertices that have one on GameCube); those export with zero normals and
the viewport lights them with the face normal.

A texture table entry is 0x34 bytes: display list pointer, a word, buffer width, width and
height (u16 at +8, +0xA, +0xC), and the name at +0x12. The texture's list sets TPSM, TMODE
(bit 0 swizzled), TSIZE (the power of two that holds the real size), TBW and TBP (the address
high byte is the block), and the CLUT commands. Swizzled data is 16-byte by 8-row blocks. CLUT
indices follow CMODE's shift, mask and start. One CLUT is loaded with fewer entries than its
indices use; the decoder reads the entries that follow it in the block. All 5254 USA PSP
textures decode, and the T8 ones agree with their PS2 copies.

#### Texture dictionaries

A `.tex` is an RW texture dictionary (`0x16`, device 3) of native textures (`0x15`). The
native struct is big-endian: platform `6`, filter and addressing, four words, the name at
`0x18` and mask at `0x38` (32 bytes each), a word, width and height at `0x5C`, depth, mip
level count, GX format and GX palette format at `0x60`, a word, then the palette at `0x68`
(CI4: 16 entries, CI8: 256, others none), a word with the pixel byte count of every mip
level, and the pixels. An earlier reading put the palette after the count word, which moved
every palette by two entries.

CMPR is DXT1 in 8x8 tiles of four 4x4 blocks with big-endian RGB565 endpoints, and each row
byte holds its four 2-bit indices with the leftmost pixel in the top two bits (reading them
low bits first mirrors every block row). The two interpolated colors are 5/8 and 3/8 blends,
as the GX hardware does, and 5-, 6- and 3-bit channels widen by bit replication, so RGB5A3's
alpha 7 is 255, not 252. Checked against the PS2 copies of the same textures: CI4 2308 of
2308 and CI8 286 of 286 agree, CMPR 1837 of 1962, and the rest differ in content (darker PS2
skies, a re-drawn icon sheet), not in layout.

A PS2 dictionary (device 6) holds RW PS2 native textures (platform `PS2\0`): a struct with the
platform and filter, the name and mask strings, then a struct holding a 0x40-byte raster
header (width, height, depth, raster format, TEX0, TEX1, MIPTBP1/2, sizes) and a struct of
GIF packets. Each upload is an A+D packet setting TRXPOS, TRXREG and TRXDIR, then an IMAGE
packet. The packets never set BITBLTBUF, so the destination is rebuilt the way the runtime
sets it: the first upload is level 0 at TEX0's TBP0, the last is the CLUT, the upload format follows from bytes per transferred pixel (PSMT8 as
PSMCT32 and PSMT4 as PSMCT16 at half width and height, a 16x16 PSMT4 as PSMT8 at 16x8),
and the buffer width is TBW scaled by the two formats' page widths. Pixels are written to a
4 MB GS memory model in the upload format and read back in TEX0's format; alpha `0x80` is
opaque. The CLUT is read from its upload rectangle (PSMCT32, row-major, 8-bit CLUTs in CSM1
order), not from GS memory: half the textures store TBP and CBP as 0, with the CLUT placed
either by TRXPOS inside the texture's own buffer or at (0,0) on top of it, so only the
rectangle itself is unambiguous. All 5344 USA and 5425 EUR PS2 textures decode.

## Disgaea DS

Nippon Ichi's *Disgaea DS*, checked against the USA, Europe and Japan ROMs. The plugin opens nothing by itself. Open the ROM with the Nintendo plugin and its files gain children and panes as their bytes are recognised, at any depth.

- `DSARC FL` (`bgm.dat`, `se.dat`, `song.dat`, `voice.dat`, `msgvo.dat`) and `DSARCIDX` (every other `.dat`, and the `.dso` files inside `dspack.dat`) list their entries. Each entry can be extracted or replaced.
- `.msnd` files (DSEQ) split into their `.sseq`, `.sbnk` and `.swar`. The `.sseq` plays: a copy of the DS sound driver runs it through the file's own `.sbnk` and `.swar` as it plays, for two loops and a 10-second fade, and picks up any of the three parts that is replaced. Extract Audio renders the whole song to a WAV. A `.swar` lists its waves as `.swav` files that play, extract, and replace from a SWAV or a WAV.
- `.mpds` and `.dsm` files are LZ10. Each shows one decompressed child (`.mpd` or `.bin`), and replacing it compresses the new bytes.
- `.mpd` maps (inside each `.mpds`) open in the Map tab, a top-down editor: paint with Raise, Lower, Set height, Level and Fill height (walls are rebuilt around every changed tile, textured like the map's most common wall on that side, 4 half-texels per height unit and down to the neighbour's top), Add tile, Erase tile, Stamp look, Mobility, Geo panel (paint or fill), Place, Move and Remove unit, and Place or Remove event tile; the Layer box tints cells by height, mobility or Geo panel, and Select shows every field of the cell's tile, units and event tile beside the map. Each stroke is one undoable edit. The same maps also open in the 3D view with their textures from `maptex.dat`, unit markers coloured by alliance, Geo panels and event tiles. Under each map, Tiles, Actors and Chunks list every record. Selecting one highlights it on the map, and its properties are editable: corner heights, side bottoms, mobility, Geo panel and colour, every face's texture, UV and transform, every actor field, objects, event tiles, chunk offsets, and the raw bytes of any record. Tiles and actors can be added, duplicated and removed; the map's texture table and model list are editable on the map. Export map as JSON writes every record with its fields and raw bytes, and Replace map from JSON reads it back (fields win over raw bytes).
- `.imy` images show in the image pane and can be replaced from a PNG of the same size.
- `SWAV` and `STRM` sounds (`.se`, `.swav`, `.strm`) play. PCM8, PCM16 and IMA-ADPCM decode to 16-bit WAV, and **Replace sound from WAV** encodes a WAV back in the sound's own codec. Re-importing an extracted WAV gives back the original file byte for byte.

The `MAP` files (`.mpb`), `.amd`/`.amt` animation and the data tables stay raw bytes.

### Specification notes

- DSARCIDX: the entry table starts after the `u16` ID list, rounded up to 4 with `0xFF`. Both archive kinds place entries 512 bytes apart counted from the first entry, and the file ends on the same step. The gaps hold leftover junk, which a rebuild writes as zeros.
- DSARC FL entries can share data: the same name, offset and size appear more than once (`voice.dat`, `song.dat`, `msgvo.dat`). They stay one block, so replacing one replaces all of them. A rebuild keeps the header bytes before the first entry and changes only the size and offset words.
- DSEQ: the word at `0x10` is the SSEQ offset plus 16. The SBNK starts at the SSEQ end plus 16, rounded up to 32; the SWAR starts at the SBNK end rounded up to 32. The word at `0x2C` is kept as found. These rules rebuild every shipped `.msnd` byte for byte.
- IMY: a 32-byte header (`u32` unpacked size at 4, row bytes at 8, flags at 10, bits per pixel at 11, height at 12, colour count at 14), the BGR555 palette, then the pixels. Flag bit 0 means stored; otherwise a `u16` info-byte count, the info bytes and the data follow, and the scheme is the PS2 one documented on Netherworld Research, working in 16-bit units with look-backs of 2, the row, the row plus 2 and the row minus 2. 4bpp pixels are low nibble first. Palette bit 15 is opacity: transparent sprites clear it on index 0.
- IMY replacement keeps each pixel's old index when its colour has not changed, because palettes repeat colours. New colours must fit the palette size; otherwise the palette is rebuilt, and the image is refused when it uses more colours than the IMY holds.
- Sound import keeps the original header and codec. A block whose samples match the original keeps its original bytes, so an unedited WAV rebuilds the file exactly; a changed block is encoded (IMA-ADPCM picks the closest of the 16 nibbles at each step and starts from the old block's step index). Any PCM WAV (8, 16, 24 or 32-bit), float WAV (32 or 64-bit) or extensible WAV is accepted. Channels are mixed to the sound's own count (mono for SWAV and SWAR waves), averaging the source channels that fold onto each one. A new sample rate scales the timer word by the rate ratio; a rate the 16-bit rate or timer cannot hold is resampled linearly to the sound's original rate. The extracted WAV of a STRM stops at the sample count in its header; if the imported WAV is that length, the samples padding out the last block are taken from the original.
- Sequence playback is HLE in two layers. The driver layer follows the Nitro Composer driver as decompiled from Pokémon Diamond and ported by NCSF (see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)): 16 tracks and 16 voices, voice allocation order 4, 5, 6, 7, 2, 0, 3, 1, 8 to 15, the tempo counter stepping one tick per 240, the attack, decay, sustain and release envelope, LFO and sweep, and the SDK volume, pitch and sustain tables. It runs every 64 × 2728 ARM7 cycles and writes its register changes at the start of the next run, as the driver thread does. The hardware layer models the SPU: each channel timer counts from TMR at half the ARM7 clock and fetches a sample on overflow, a channel starts three samples late (ADPCM spends eight more on its header), ADPCM saves its state at the loop start and restores it on each loop, samples shorter than 16 bytes stay silent, and PSG and noise use the hardware duty table and LFSR. The mixer runs every 1024 cycles (32728 Hz): sample shifted by 4, 3, 2 or 0 for the volume divider, times volume, times 128 − pan and pan (127 counts as 128) shifted right 10, master volume 127, then clamped and cut to 10 bits as the DS output is. There is no interpolation; the DS has none.
- Streaming: when a sequence is opened, a fast pass runs the driver with the SPU channels advanced by arithmetic instead of sample by sample (a channel timer's overflows over n samples, and where its position lands after a loop or at the end of a one-shot), which finds the length to the fade in milliseconds. Seeking runs the same fast pass to the target, then rebuilds each channel by replaying only the steps that matter (to its first loop, then the remainder of the loop; modulo 8 for PSG and 32767 for noise). A seek lands on the same samples as playing from the start.
- The DSEQ word at `0x2C` is the length to the first loop point in milliseconds, entered by hand. The renderer reaches the first loop within 0.1% of it for most songs (song005: 362010 ms against 362000); bgm_33 stores two loops and the jingles, which do not loop, differ.
- Every PCM instrument in the shipped banks uses wave archive 0, which is the DSEQ's own SWAR; an instrument that names another archive is silent.
- SWAR: a 0x3C-byte header, a `u32` wave count at `0x38`, absolute `u32` offsets, then each wave as a SWAV without its 0x18-byte header. In every shipped SWAR the waves sit back to back in table order from the end of the table, each exactly 0x0C plus (loop + non-loop words) × 4 bytes long; a rebuild lays them out the same way and rewrites the file size and the DATA size (file size minus 0x10). A wave shows as a SWAV whose header copies the SWAR's byte-order and version words. All 138 SWARs rebuild byte for byte, also after every wave goes through WAV and back.
- Loops: an extracted WAV carries a `smpl` chunk with one loop (start, and the last sample as end) when the sound loops. On import, a WAV with a `smpl` loop sets the loop flag and start, and samples after the loop end are dropped, since the DS loops from the end of the data; a WAV without one clears the loop flag. A SWAV loop offset counts 4-byte words from the start of the data: the ADPCM header word plus the loop start divided by the samples per word (8 for IMA-ADPCM, 4 for PCM8, 2 for PCM16). A start between words rounds down. Every shipped non-looping ADPCM wave has offset 1, which is the same formula at sample 0. A STRM stores its loop start in samples at `0x20`.
- SWAV data is padded to whole 4-byte words. In a STRM, the last-block length at `0x38` does not include padding; the data as a whole is padded to 4 bytes at the end, and the DATA size and file size count that padding. Every SWAV and STRM in the three ROMs rebuilds byte for byte from its own WAV.
- LZ10 back-references start at distance 2, and among equally long matches the farthest one wins. That is the game's own compressor: it rebuilds all 432 shipped `.mpds` and `.dsm` files byte for byte.
- MPD layout on DS: a 16-byte header (`u16` chunk count, `u16` actor count), the chunks at 1408 bytes each (64-byte header with offset X and Z floats at 0 and 8, the tile count at `0x2C` and an id at `0x30`; 32 objects of 36 bytes; 16 event tiles of 4 bytes; a 128-byte base tile), then every chunk's tiles in chunk order at 128 bytes, the actors at 64 bytes, and a footer of two `u32` lists, each led by its count. The id is not a sequence: object-only chunks carry ids such as 2 or 3. The 56-byte maps are empty Item World placeholders.
- Tile (128 bytes): twelve 8-byte faces (u, v, v right, width, height, height right, texture, transform), corner heights at 96 (corners X0Z0, X1Z0, X0Z1, X1Z1; −value ÷ 12 is the height in tiles), side bottoms at 100, visible at 112, X and Z at 113, mobility at 119, Geo colour at 120, Geo panel (100) at 121. Faces 0 to 3 are the −Z, +X, +Z and −X sides (measured against which neighbour is lower over every map), 4 the top, 5 to 8 the side decals. A face's texture *n* is entry *n* − 1 of the footer's first list, an index into `maptex.dat`; 0, or an entry of `0xFFFF`, is a face the DS does not texture. These are the PS2 decal layer and edge faces the game keeps but never loads. In the saved battle the VRAM held exactly the textures that list names. UVs are in half texels. Tops sample 16 × 16 cells of the `prd` atlases; sides tile the `t` textures. The corner order, the side quads and the rotation and mirror bits follow pg_disatools' renderer, with its local X mirrored to match the tile X field.
- Every map in the three ROMs reads and writes byte for byte, and through JSON and back.
- Verified in the game code (USA ARM9, loaded at `0x02000000`, uncompressed, no overlays). The map loader is `0x02042518`: chunk and actor counts are signed 16-bit, the chunk count is clamped to 6, chunks are copied at `0x580` bytes, each chunk's tile count is read signed from `0x2C`, and header bytes 4 to 15 are copied into the map state and never read again (the word at 4 is 0 or 1 in the files, a PS2 leftover). `0x02042194` opens `maptex.dat` for the footer's first list and `mapobjs.dat` for its second, through `0x020426B4`, which skips entries of `0xFFFF`: the first list is the face textures, the second the object models.
- Tiles (`0x02042964`, the per-chunk pass, and the Geo routines `0x0205FDF8` and `0x02060E58`): byte 112 marks a tile the chunk counts (its centre is the average of those tiles); X and Z are signed bytes; a chunk's tile offset is `floor(offset / 12)`, and the game writes the chunk centre into the base tile at `0x570` and `0x574` at run time. Byte 121 is 100 for a Geo panel and 0 for none; byte 120 indexes the Geo colour table (32-byte records, built at run time at `0x020C6BBC`), and colour 0 is a real colour (8418 panels use it). Byte 123 is derived: the game sets it from the panel and its colour record, and in every map it is 1 only on panel tiles. Bytes 115 to 116 and 125 to 127 are run-time fields of the tile routines.
- Actors are read only by the spawner `0x0206028C`: class and level signed 16-bit (the level can be scaled by 5 and 60 more under a game flag), side signed (passed to unit creation `0x0205B6B4`), X and Z signed with a world position of tile × 12 + 6 and no chunk offset, facing signed × 90°, byte 8 an appearance condition (−1 always; otherwise the unit spawns only when that entry of a battle flag table equals 2), AI at 9 (forced to mode 3 when byte 38 is not zero), byte 10 multiplies the unit's stat at `+0x5B0` by value + 1, items 1 to 4 signed 16-bit at 12, the spawn rule at 20 (0 always; otherwise (value − 1) mod 3 chooses less-than, at-least or equal and (value − 1) / 3 + 1 the threshold), the Geo effect at 22 (value / 10 and value mod 10 − 1), one signed 16-bit weapon mastery at 24 for all seven weapon types, four spells at 26 and their levels at 34. Byte 11 and bytes 39 to 63 are never read (39 to 63 are zero in all 4545 actors).
- Still from the references rather than the code: the mobility names (the values are 0, 1 and 2 in the files), the names of the Geo colours (the table is built at run time), which game stat `+0x5B0` is, the face transform bits, and the object and chunk-header fields other than the offsets, the tile count and the id.

## PlayStation 3

Game discs (`.iso`, `.dec.iso`).

The disc is read through its own ISO 9660 file system. The root holds `PS3_DISC.SFB`,
`PS3_GAME`, and `PS3_UPDATE`, and every file under them opens as the disc's bytes. An
encrypted 3k3y image is decrypted sector by sector behind that, with the key read from the
image's own watermark or from a sidecar `.dkey` file beside it, which must hold 32 hex digits and be at most
4 KiB. PS3 images are read-only.

## Xbox 360

Game discs (`.iso`).

The game partition is walked as its own directory tree, wherever the image puts the
partition: XGD1, XGD2, XGD3, the trial disc's base, or an XISO's first sector. Xbox 360
images are read-only.

## Standard Formats

The Generic plugin has no archives of its own. It recognises OGG, WAV, PNG, JPEG, GIF, and
BMP payloads wherever they turn up, so a sound buried inside another container still gets an
Audio pane and an extract command. A BMP is recognised only with a plausible file header:
zero reserved words, a DIB header of 12 to 124 bytes, a pixel-data offset past both headers
and within the declared file size, and a declared size no larger than the payload, so a blob
that merely starts with `BM` is not shown as an image. The pool of recognisers spans every loaded plugin, so a
WAV inside a Harugeki archive plays without either plugin knowing about the other.

## Console disc images

Console disc images are read through a shared library, `Omega.Disc`, which the console
plugins reference. Everything there works the same way:

- The format's own endianness and framing are decoded with one bounded cursor, `SpanReader`,
  that names the offset of anything invalid.
- Containers are presented as windows, cluster tables, and streams rather than copies, so
  opening a multi-gigabyte disc costs the few kilobytes its tables hold.
- The reconstruction is byte-exact, which is what a same-length write-back depends on.

| Image | Read | Written back |
| --- | --- | --- |
| WBFS (`.wbfs`) | whole disc per slot, decrypted Wii partition data | same-length edits, patched into the stored clusters |
| 3DS (`.3ds`, `.cci`) | partitions from the NCSD table, NCCH headers | not yet |
| PS3 (`.iso`, `.dec.iso`) | the disc's ISO 9660 file system | not yet |
| Xbox 360 (`.iso`) | the game partition's directory tree | not yet |

`Omega.Disc` depends on nothing but `Omega.Plugins`, so the disc formats can be exercised
directly, without a plugin loading a file.

## Plugins that are not in this repository

A plugin is one assembly named `*.Formats.dll`, dropped into the `formats` folder beside the
executable. The host loads it on the next launch, and everything above applies to it exactly
as it applies to the in-tree plugins. [PLUGINS.md](PLUGINS.md) is the walkthrough;
[ARCHITECTURE.md](ARCHITECTURE.md) describes how the host uses what a plugin implements.