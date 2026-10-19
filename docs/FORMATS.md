# Formats

Eight plugins are built from this repository. The host knows nothing about any of them; each
plugin teaches it one or more formats, and the host draws the panes and the edit commands
from what the plugin says a row can do. To add your own, see [PLUGINS.md](PLUGINS.md).

| Plugin | Opens | Reads | Written back |
| --- | --- | --- | --- |
| Harugeki | PC game archives | models, textures, materials, skeletons, animation | textures, geometry, raw payloads |
| Virtuos | Nintendo DS game archives | tracks, textures, zones, collision, visibility, cars, models, skeletons, animation | textures, tracks, models, skeletons, animation, collision |
| Nintendo | DS ROMs, WBFS containers, 3DS images | whole file systems, disc images | DS ROMs in full, WBFS by same-length patch |
| Polarbit | iOS game archives | models, tracks, scenes, textures, fonts, sounds, music, strings | textures, sounds, scene geometry, strings |
| DWSSK | console game WADs, sound banks, Wwise audio, Scaleform movies, Bink videos | textures, models, animation, levels, collision, navigation, data tables, strings, shaders, audio, video | textures, models, data tables, strings, shaders, audio, movie images, whole WADs and banks |
| PlayStation 3 | game discs | ISO 9660 file system | nothing (read-only) |
| Xbox 360 | game discs | game partition directory tree | nothing (read-only) |
| Standard Formats | no archives of its own | OGG, WAV, PNG, JPEG, GIF, BMP payloads | PNG replacement where the container allows |

## Harugeki

*Suzumiya Haruhi no Chourantou* and *Gekitou* (PC).

Opens the `data/*.bin` containers. Models, embedded `ringtex2` textures, materials,
skeletons, and animation all appear in the tree; animation clips play in the Animation pane.

A texture can be replaced from a PNG. A model can be replaced from a glTF file, and its
joints are mapped onto the model's bones by name, so a skinned model re-binds to its own
skeleton. Any payload that is not understood further can still be extracted and replaced as
raw bytes. Saving rewrites the container in place.

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
because an image with a stale chain is rejected by Dolphin and by real hardware.

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

## DWSSK

*DreamWorks Super Star Kartz* on PlayStation 3, Xbox 360, Wii and 3DS. The DS version runs
on a different engine and is covered by the Virtuos plugin.

Opens the `HIG!` WAD containers. A level's `.wad`, `.wad.mem1` and `.wad.vram` are read
together as one tree. A texture a model names is looked up in its own WAD, then its other
parts, then the `global.wad` in a folder above it, and last in the other WADs of the same
game folder, which are indexed the first time a texture is missing. That last step works
the same inside a disc image, where the index covers the disc's game folder. The TJZIP
compression is rebuilt byte for byte, so an untouched WAD saves to the same file. Sound banks
(`.bnk`), Wwise audio (`.wem`), Scaleform movies (`.swf`), Bink videos (`.bik`) and level
name tables (`.nog`) also open on their own. A single file extracted from a WAD opens without
reading its sibling files; they are read only when a texture is missing. Inside a PS3, Xbox
360, Wii or 3DS disc image the same files open in place, and the WAD parts there are grouped
the same way; a WAD row is decompressed the first time it is expanded, and an edit inside it
is written back to the row when the disc allows it.

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

## PlayStation 3

Game discs (`.iso`, `.dec.iso`).

The disc is read through its own ISO 9660 file system. The root holds `PS3_DISC.SFB`,
`PS3_GAME`, and `PS3_UPDATE`, and every file under them opens as the disc's bytes. An
encrypted 3k3y image is decrypted sector by sector behind that, with the key read from the
image's own watermark or from a sidecar file beside it. PS3 images are read-only.

## Xbox 360

Game discs (`.iso`).

The game partition is walked as its own directory tree, wherever the image puts the
partition: XGD1, XGD2, XGD3, the trial disc's base, or an XISO's first sector. Xbox 360
images are read-only.

## Standard Formats

The Generic plugin has no archives of its own. It recognises OGG, WAV, PNG, JPEG, GIF, and
BMP payloads wherever they turn up, so a sound buried inside another container still gets an
Audio pane and an extract command. The pool of recognisers spans every loaded plugin, so a
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

`Omega.Disc` carries no dependency on the host's contract, so the disc formats can be tested
directly, without a plugin loading a file.

## Plugins that are not in this repository

A plugin is one assembly named `*.Formats.dll`, dropped into the `formats` folder beside the
executable. The host loads it on the next launch, and everything above applies to it exactly
as it applies to the in-tree plugins. [PLUGINS.md](PLUGINS.md) is the walkthrough;
[ARCHITECTURE.md](ARCHITECTURE.md) describes how the host uses what a plugin implements.