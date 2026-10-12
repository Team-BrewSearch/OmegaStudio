# Formats

Seven plugins are built from this repository. The host knows nothing about any of them; each
plugin teaches it one or more formats, and the host draws the panes and the edit commands
from what the plugin says a row can do. To add your own, see [PLUGINS.md](PLUGINS.md).

| Plugin | Opens | Reads | Written back |
| --- | --- | --- | --- |
| Harugeki | PC game archives | models, textures, materials, skeletons, animation | textures, geometry, raw payloads |
| Virtuos | Nintendo DS game archives | tracks, textures, zones, collision, visibility, cars, models, skeletons, animation | textures, tracks, models, skeletons, animation, collision |
| Nintendo | DS ROMs, WBFS containers, 3DS images | whole file systems, disc images | DS ROMs in full, WBFS by same-length patch |
| Polarbit | iOS game archives | models, tracks, scenes, textures, fonts, sounds, music, strings | textures, sounds, scene geometry, strings |
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
