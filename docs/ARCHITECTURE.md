# Architecture

Omega Studio is an asset browser that knows no formats. A plugin supplies everything it can
open, draw, or edit, and the `Omega.Plugins` assembly is the whole boundary between the two.
This document describes how the host uses that boundary. For the other side, how to implement
a plugin, see [PLUGINS.md](PLUGINS.md).

## The three layers

```
                  ┌──────────────────────────────────────────┐
                  │  OmegaStudio                             │
                  │  Views, view models, rendering, audio,   │
                  │  glTF and PNG codecs, the plugin host    │
                  └────────────────────┬─────────────────────┘
                                       │ references
                  ┌────────────────────┴─────────────────────┐
                  │  Omega.Plugins                           │
                  │  The contract. Interfaces, node bases,   │
                  │  neutral geometry and image types.       │
                  └────────────────────┬─────────────────────┘
                                       │ references
       ┌───────────┬───────────┬───────┴───┬───────────┐
       │ Harugeki  │ Virtuos   │ Nintendo  │ Generic   │  …loaded at runtime
       │ .Formats  │ .Formats  │ .Formats  │ .Formats  │
       └───────────┴───────────┴───────────┴───────────┘
```

Plugins never reference `OmegaStudio`. The arrow only points one way, which is what lets a
plugin be built and dropped in without the host being rebuilt. `Omega.Plugins` is the only
assembly a plugin references and the only public API in the tree; [PLUGINS.md](PLUGINS.md) is
its documentation. Everything else in the host is an implementation detail that can change
between releases.

## Where the code lives

```
src/
  Omega.Plugins/             The plugin contract. The only public API.
  OmegaStudio/               The application: UI, rendering, audio, plugin host.
  Omega.Disc/                Disc formats: bounded readers, cluster mapping, decryption streams.
  Formats/
    Harugeki/                Suzumiya Haruhi no Chourantou / Gekitou
    Virtuos/                 Virtuos racing engine (Nintendo DS)
    Nintendo/                Nintendo DS ROM images, WBFS Wii containers, 3DS cart images
    Polarbit/                Polarbit racing engine (iOS)
    DWSSK/                   DreamWorks Super Star Kartz (PS3, Xbox 360, Wii, 3DS)
    Generic/                 OGG, WAV, PNG, JPEG, GIF, BMP recognisers
    PlayStation/             PlayStation 3 game discs
    Xbox/                    Xbox 360 game discs
```

Build and layout details, including how the plugin projects copy themselves into the host's
`formats` folder, are in [DEVELOPMENT.md](DEVELOPMENT.md).

## One memory figure

Every budget the app works to is drawn from `MemoryBudget`:

| Budget | Share | Of a 2 GB total |
| --- | --- | --- |
| Undo history | one eighth | 256 MB |
| Payload cache | one half | 1 GB |
| One export | one quarter | 512 MB |
| Deferred WBFS writes | one eighth | 256 MB |

They are proportions of a single figure rather than numbers of their own, so what a session
holds in total is a question with one answer rather than four that could all be full at once.

## Showing a row

When a row is picked, `SelectionPresenter` does the whole translation: read what the row
offers, put the scene, the image, the audio, the properties, and the commands in place, and
choose the pane that suits. It drives the view model through `ISelectionView` and runs its
commands through `ICommandRunner`, which is what lets it be tested without the archive, the
console, the search, or the undo history behind it. Everything a plugin can throw here is
contained, so a throwing node leaves the pane it was for empty rather than taking the
selection down.

## Loading

`PluginLoader` scans `formats/` beside the executable for `*.Formats.dll`, in ordinal
filename order. Each file is loaded into its own `AssemblyLoadContext`
(`PluginLoadContext`), which resolves that plugin's private dependencies itself. A plugin is
one file, so the libraries it depends on are embedded in it as resources named
`omega/deps/<assembly>.dll`, and the context loads them straight out of the plugin assembly;
that is how the Nintendo plugin reaches Ekona, Yarhl, and Texim without a single loose DLL
in `formats/`. Nothing it carries is visible to another plugin or to the host. A plugin
built elsewhere that ships its libraries beside itself still works: what the resources do
not answer falls through to an `AssemblyDependencyResolver` over the plugin's own folder.
One assembly is deliberately not isolated: `Omega.Plugins` is forced back to the host's
copy, because a plugin returning types from a second, separately loaded copy of the contract
would not be assignable to the interfaces the host holds.

A plugin is rejected, with the reason logged to the Console pane, when it:

- has no `[assembly: OmegaPlugin(typeof(...))]` attribute;
- names a type that does not implement `IFormatPlugin` or cannot be constructed;
- reports a `ContractVersion` other than `PluginContract.Version`;
- has a blank `Id`, or an `Id` another loaded plugin already claimed;
- is not on the trusted list, when the build ships one.

A plugin is code and runs with the user's own permissions, so a `trusted-plugins.txt` beside
the executable names the digests this build vouches for and refuses everything else; its
absence is the developer default of loading what is in the folder. See the Trust section of
[PLUGINS.md](PLUGINS.md) for what that does and does not mean.

One bad plugin never stops the others. The result is a `PluginCatalog` of what loaded and a
list of `PluginFailure` for what did not, and both appear under the Formats menu. The SHA-256
of every plugin that loads is printed in the Console pane.

## Opening an archive

`ArchiveSession` owns exactly one open archive at a time.

1. Read the first 4 KB of the chosen file.
2. Ask each format in the catalog, in load order, whether it claims the file. The first
   `CanOpen` that returns true wins.
3. Call `Open` on that format, on a background thread, handing it an `IHostServices` scoped
   to the owning plugin.

`CanOpen` gets the head and the path, because not every container can be identified from its
opening bytes. Harugeki's `.bin` has no magic number at all; it starts with a count and a
table of offsets, so it validates that table against the file's real length. Virtuos checks
that the `.gdf` and `.gdt` siblings exist beside the `.gda`. A DS ROM carries no magic
number either, so the Nintendo plugin validates the header's section table, offsets, and
sizes against the real length of the file it is in.

## The asset tree

An archive exposes a single `Root` node, and the tree grows from it lazily: `HasChildren`
must answer without parsing anything, and `GetChildren` runs only when a row is expanded.
That is what lets a 133 MB container open instantly.

A node whose payload lives in a stream behind it does not read that payload to describe
itself. `IBinaryAsset.Head` answers with a window of the first bytes and
`IBinaryAsset.TryGetLength()` answers with a length from the node's own metadata, both with
backward-compatible defaults, so recognition and folder expansion read no bytes at all and
selecting a multi-gigabyte file costs a property read rather than a file. `Size` falls back
to reading the payload only for a node that knows no length and has no head of its own.

Nodes describe themselves through capability interfaces rather than a type hierarchy. A node
implements as many as apply:

| Interface | Earns |
| --- | --- |
| `IBinaryAsset` | Extract, and a raw file type under Replace when it says bytes can be swapped |
| `IImageAsset` | The Texture pane, and PNG, JPEG, and BMP under both Export and Replace |
| `IAudioAsset` | The Audio pane and Extract Audio |
| `IVideoAsset` | The Video pane, frame, soundtrack and `.mkv` under Export, and Export all frames |
| `ISceneAsset` | The Viewport, and glTF under Export |
| `IAnimatedAsset` | The Animation pane, its clip picker, and its dope sheet |
| `IDescribable` | Rows in the Properties pane, editable ones included |
| `ICommandSource` | Extra entries in the row's context menu |

`NodeCommands` builds a row's menu purely by testing which of these a node implements, so the
host needs no knowledge of what the node actually is.

A row gets at most one `Export…` and one `Replace…` entry, however many capabilities it has.
Each capability, and each plugin command that sets `AssetCommandRole`, contributes file types
rather than a menu entry of its own; the dialog offers all of them and the host then runs
whichever one claims the extension the user chose, falling back to the raw-bytes handler when
nothing claims it. That is why replacing a texture from a PNG and replacing a model from a
glTF are one command: the file the user picked says which.

### Recognition

Some payloads are worth understanding regardless of whose container they came out of. An
`IAssetRecognizer` claims bytes by content, and `AssetRecognition.Enrich` wraps any plain
byte node it claims in a `RecognizedAsset` that forwards identity and bytes to the original
while adding decoded behaviour. Recognisers are pooled across every loaded plugin, so the
Generic plugin's OGG recogniser gives an Audio pane to a sound found inside a Harugeki
archive without either plugin knowing about the other. A node that already carries content
of its own is left alone. The same pool answers `IHostServices.IsKnownPayload`, which lets a
container walk stop descending when a blob turns out to be a standalone file rather than
another table.

Some containers are not one blob at all. A Virtuos archive is three files (data, name table,
and entry table), so no single row can be recognised as one. `IContainerRecognizer` is
offered every folder's rows instead, after each row has been recognised on its own, and
returns the rows to show in their place: the three collapse into one archive row whose
children are the tracks and textures inside. Because the pool spans plugins, the same
recogniser opens a `.gda` sitting in a folder on disk and one sitting in `data/Publish.NDS`
of a DS ROM.

A nested container has no `Save` of its own; only the archive the host opened has one. It
writes each edit straight back into the rows it was read from, through
`IBinaryAsset.ReplaceBytes`, which leaves the outer archive dirty and lets the one Save
rebuild everything from the outside in.

## Editing and undo

Every edit, whether the host's own replace commands or a plugin's alike, arrives as an
`AssetCommand`, executed on a background thread with an `AssetCommandContext`. The command
reports what it invalidated by setting `Changed`, and the host refreshes only those views.
Undo is snapshot-based rather than operation-based, because the host does not know what any
given command does. Before running a command that could modify something,
`AssetSnapshot.Capture` reads the node's current bytes, or its current image for a format
that only swaps images. `EditHistory` holds a byte budget, 256 MB by default, rather than a
fixed count, because 24 whole payloads is 768 MB for 24 models of 32 MB; the oldest edits are
evicted as new ones push the total past the budget, and an edit larger than the whole budget
is refused with a message rather than stored silently where it cannot be undone. Two cases
clear the history rather than risk reverting the wrong thing: a command on a node with
neither restorable bytes nor a replaceable image, and a command that edits a node other than
the one it ran on.

An editable Properties row is the one edit that does not arrive as a command. A `PropertyRow`
carrying an `Apply` is shown as a text box, a check box, or a drop-down; committing it
(leaving a text box, or pressing Enter in one) calls the plugin straight back, on the UI
thread, and the plugin either stores the value or throws, in which case the message is logged
and the pane redrawn from the format. The row reports what it invalidated through the same
`AssetChange` flags a command uses. These edits are undoable like any other. The host cannot
read a plugin's header fields back generically, so the snapshot is the row's own text:
`PropertySnapshot` keeps the value and the delegate, and reversing the edit hands the old
text to that same delegate. An `Apply` that would reject a value it produced earlier is
therefore an edit that cannot be reversed, and the guarded call turns that into a cleared
history rather than a half-reverted format.

## Saving

Saving is one contract, and it is a mechanism rather than advice. `IArchive.BackupPaths`
names every file `Save` will overwrite, and the moment an archive is opened each of those is
copied to `<name>.bak`, never overwriting a backup that already exists, so the pristine copy
exists before anything can have changed rather than after the damage.

`IArchive.Save` then goes through `WriteGuard`, which does three things in order: it writes
the rebuilt bytes to a temporary file beside the original and re-parses them from there, so a
format that produces something it cannot read back is refused before anything moves; it skips
the write entirely when the replacement is byte-identical to what is on the disk; and it
moves the temporary over the original with `AtomicSave`, which flushes to the disk first and
then renames. A rename either happens or it does not, so a save that fails anywhere leaves
the original byte-for-byte intact, and a multi-file save, a Virtuos `.gda` and `.gdt`, takes
a rollback copy of each original and puts back the ones it had already replaced if a later
one fails.

Two formats do not rebuild at all and say so instead of writing something wrong. A WBFS or
Wii edit is same-length by construction, so the edit goes into a deferred overlay in memory
and `Save` commits it: the whole set of cluster writes is validated against the container's
own tables first, then written, then flushed once, then one cluster is read back to prove it.
A Wii patch also rebuilds the partition's H0 to H2 hash chain and the master table's H3
entry, because a patched image with a stale chain is an image Dolphin and real hardware
reject. A save that fails therefore leaves the original intact by construction: nothing had
been written. A PlayStation disc, an Xbox image, and a 3DS image are read-only and raise
rather than pretend.

Archive-level backups are the user's safety net; the atomic write is the mechanism. Both are
tested, including a forced mid-write failure.

## Reading bytes

One set of primitives serves every parser: `Omega.Plugins.BinaryRead`. Little-endian and
big-endian reads, fixed-point, NUL-terminated names, Latin-1, and hexadecimal. Every read
bounds-checks its span and refuses an out-of-range one with `InvalidDataException` naming the
offset and the span, so a truncated blob is a parse failure with a reason rather than an
indexer's own exception. The two cursor shapes built on it, `SpanReader` in Omega.Disc and
the `Le` / `BinaryRead` names each plugin kept, forward to the same set, so a read past the
end means the same thing and reads the same way wherever it is written.

## Containing plugin failures

A plugin is third-party code parsing formats somebody reverse-engineered, so it will
eventually throw. `PluginGuard` wraps every call from the host into a plugin. The rule is
that a failure costs the user the one thing they were doing and never the session: an
unreadable node shows an error row, a throwing sniff means that format simply does not claim
the file, a throwing property leaves the pane empty. The catches are broad on purpose, since
guessing which exception types a plugin author will use, and guessing wrong, turns a
contained failure back into a crash. `OutOfMemoryException` is the single exception that is
not a plugin failure; every later call will fail the same way, and logging one line per node
while continuing turns a clean crash into a hang.

## Host services

What the host lends a plugin, through `IHostServices`:

- **`Images`**: decode PNG, JPEG, GIF, and BMP; encode PNG, JPEG, and BMP. No plugin carries
  a codec.
- **`glTF`**: read a glTF or GLB in full, open one for selective reading, or write geometry
  out. One writer serves every format: a plugin produces neutral `SceneGeometry` and gets
  export for free.
- **`Log`**: a line in the Console pane, prefixed with the plugin's display name.
- **`PluginDirectory`**: where this plugin was loaded from, for reading data files shipped
  beside it.
- **`IsKnownPayload`**: described under Recognition above.

## Rendering

The viewport is an Avalonia `OpenGlControlBase`. `SceneGeometry` arrives already in a
right-handed, Z-up space; converting handedness is the plugin's job, done once at parse time
so the viewport, the exporter, and the skinning all agree. `SceneGeometry.Validate` runs
before an upload and before an export: array lengths against the vertex count, indices and
joint indices in range, every submesh covering real entries with a real texture. A plugin bug
is named at that boundary rather than surfacing later as a torn draw or corrupt output.
Shaders carry no `#version` line; the preamble is supplied at compile time from the context
Avalonia actually provided, so one copy of each shader serves both desktop GL and GLES.

## Audio

`AudioPlaybackEngine` drives a single OpenAL source fed by one static buffer holding the
whole clip. Because the buffer is complete, the driver owns the play position: seeking is a
property set on the source, and the position it reports back is the real one, so the UI clock
and the audio cannot disagree. The context is created on demand and released when the clip is
unloaded, because a context is a device handle and one per clip leaves them all open for the
session. `AudioPlaybackService` adds the transport on top, one `PlaybackState` rather than
several booleans kept consistent by hand, and polls the source every 25 ms, since OpenAL
offers no completion callback.

The poll loop owns its timer, and the only thing that happens under the transport lock is
cancelling the loop's token; cancelling never waits for the loop to notice. Stopping, pausing,
or unloading a clip can therefore never block on a tick that is waiting for the lock the tick
itself needs, which is what used to hang the process on a close. OpenAL contexts are current
on one thread at a time, so the query the poll loop makes carries the context over. Loads are
serialised with a generation counter, so a clip the user has already left is never played
over the newer one.

## Video

`VideoPlayer` decodes ahead on the thread pool: a producer task reads frames from the
plugin's `IVideoReader` into a bounded channel six frames deep and swaps them to BGRA there,
so the UI thread only copies rows into a `WriteableBitmap`. A render-priority timer picks the
newest decoded frame not later than the clock and drops the ones it has passed. The clock is
the audio position whenever the soundtrack plays and a stopwatch otherwise, so picture and
sound share one time base. A seek cancels the producer, drains the channel back into a
buffer pool, and starts a new producer at the target frame; when paused, the first frame it
delivers is shown. The soundtrack goes through the same audio path as any clip, under the
same generation counter, so a movie the user has left never starts its sound late.

`MatroskaWriter` exports a movie without outside tools. It writes Motion JPEG frames, encoded
in parallel batches, and the decoded soundtrack as interleaved PCM blocks, with a seek head,
cues every second and the segment size patched in at the end. The file is written beside the
target and moved over it only when complete.

## Threading

The UI thread owns the view models and the tree. Archive opening, command execution, and
saving all run on the thread pool, as does expanding a row: a folder with ten thousand rows
builds its children on the pool and publishes them on the UI thread, and a build that is
superseded before it finishes is abandoned rather than published.

A disc image is read through `SharedStream`, one file handle per archive with every access
gated. Per-reader handles would cost an open and a close per cluster, which is a syscall
storm across a multi-gigabyte scan, and memory-mapped views do not fix the write side; the
gate serialises a position-and-read instead of letting two readers serve each other's bytes.

The search walk is debounced by 200 ms and carries a cancellation token, so typing cancels
the previous walk rather than queuing another. The walk reads nodes rather than the view
models the UI thread owns, and the result set is capped and the cap reported, so memory stays
proportional to the matches. Overlapping opens are refused by a single slot rather than
racing. The audio transport's state is locked at the object that owns it. The audio poll
timer fires on the pool and raises `StateChanged` without marshalling, so subscribers post to
the UI thread themselves.
