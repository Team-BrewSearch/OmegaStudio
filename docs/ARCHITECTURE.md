# Architecture

Omega Studio is an asset browser that knows no formats. A plugin supplies everything it can open, draw, or edit, and the `Omega.Plugins` assembly is the whole boundary between the two.

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

Plugins never reference `OmegaStudio`. The arrow only points one way, which is what lets a plugin be built and dropped in without the host being rebuilt. `Omega.Plugins` is the only assembly a plugin references and the only public API in the tree. Everything else in the host is an implementation detail that can change between releases.

## Where the code lives

```
src/
  Omega.Plugins/             The plugin contract. The only public API.
  OmegaStudio/               The application: UI, rendering, audio, plugin host.
  Omega.Disc/                Disc formats: bounded readers, cluster mapping,
                              decryption streams. Shared by the console plugins.
  Formats/
    Harugeki/                Suzumiya Haruhi no Chourantou / Gekitou (PC)
    Virtuos/                 Virtuos racing engine (Nintendo DS)
    Nintendo/                Nintendo DS ROM images, WBFS Wii containers, 3DS cart images
    Polarbit/                Polarbit racing engine (iOS)
    DWSSK/                   DreamWorks Super Star Kartz (PS3, Xbox 360, Wii, 3DS)
    Generic/                 OGG, WAV, PNG, JPEG, GIF, BMP recognisers
    PlayStation/             PlayStation 3 game discs
    Xbox/                    Xbox 360 game discs
```

## One memory figure

Every budget the app works to is drawn from `MemoryBudget`. The figure is measured at startup as a quarter of the memory the .NET GC reports as available (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`: physical RAM, or a container or `GCHeapHardLimit` cap). It is clamped to 512 MB to 4 GB in a 64-bit process and 512 MB to 1 GB in a 32-bit (win-x86) one, and is 2 GB (1 GB on 32-bit) when the runtime reports nothing.

| Budget | Share | 8 GB machine | 16 GB or more |
| --- | --- | --- | --- |
| Undo history | one eighth | 256 MB | 512 MB |
| Payload cache | one half | 1 GB | 2 GB |
| One export | one quarter | 512 MB | 1 GB |
| Deferred WBFS writes | one eighth, below 1 GB | 256 MB | 512 MB |

The payload cache is enforced: every `StreamingAssetNode` that has read its payload keeps it in one shared least-recently-used cache. Once the kept payloads pass the budget, the least recently read are dropped and read again from the image on next use. A payload larger than the whole budget is served but not kept, and the cache holds nodes weakly, so closing an archive frees its payloads.

They are proportions of a single figure rather than numbers of their own, so what a session holds in total is a question with one answer rather than four that could all be full at once.

## Showing a row

When a row is picked, `SelectionPresenter` reads what the row offers, places the scene, image, audio, properties, and commands, and chooses the pane. It drives the view model through `ISelectionView` and runs commands through `ICommandRunner`, which lets it be tested without the archive, console, search, or undo history behind it. Everything a plugin can throw here is contained, so a throwing node leaves the pane empty rather than taking the selection down.

Everything the row's plugin provides (the scene and its validation, the image decode, the encoded audio, the soundtrack's format, `Describe` rows, and the command list) is read in one background step per selection, stamped with a generation number. Steps are chained so only one runs at a time, and a step whose selection was superseded before it started is skipped. Results are applied on the UI thread only while their generation is current, so a row the user has already left is never shown. The `WriteableBitmap` is made on the UI thread from the decoded `ImageData`, and the texture status line comes from that same decode; a row that is not an image clears the Texture pane. `AnimationPlayer.Bind` and `VideoPlayer.BindAsync` still run on the UI thread when the row is shown. While a read is in flight the panes keep the previous row and its command list is empty. Edits (`ApplyChanges`, `CommitProperty`, a clip change refreshing commands) still rebuild synchronously, except that properties of a row being re-presented wait for the read.

## Loading

`PluginLoader` scans `formats/` beside the executable for `*.Formats.dll`, in ordinal filename order. Each file is loaded into its own `AssemblyLoadContext` (`PluginLoadContext`). The file is read once, its SHA-256 is computed over those bytes and checked against `trusted-plugins.txt`, and the same bytes are loaded with `LoadFromStream`, so a file swapped on disk between the check and the load cannot get past the allowlist. The context resolves that plugin's private dependencies itself. A plugin is one file, so the libraries it depends on are embedded in it as resources named `omega/deps/<assembly>.dll`, and the context loads them straight out of the plugin assembly; that is how the Nintendo plugin reaches Ekona, Yarhl, and Texim without a single loose DLL in `formats/`. Nothing it carries is visible to another plugin or to the host. A plugin built elsewhere that ships its libraries beside itself still works: what the resources do not answer falls through to an `AssemblyDependencyResolver` over the plugin's own folder. One assembly is deliberately not isolated: `Omega.Plugins` is forced back to the host's copy, because a plugin returning types from a second, separately loaded copy of the contract would not be assignable to the interfaces the host holds.

A plugin is rejected, with the reason logged to the Console pane, when it:

- has no `[assembly: OmegaPlugin(typeof(...))]` attribute;
- names a type that does not implement `IFormatPlugin` or cannot be constructed;
- reports a `ContractVersion` other than `PluginContract.Version`;
- has a blank `Id`, or an `Id` another loaded plugin already claimed;
- is not on the trusted list, when the build ships one.

A plugin is code and runs with the user's own permissions, so a `trusted-plugins.txt` beside the executable names the digests this build vouches for and refuses everything else; its absence is the developer default of loading what is in the folder. See the Trust section of PLUGINS.md for what that does and does not mean.

One bad plugin never stops the others. The result is a `PluginCatalog` of what loaded and a list of `PluginFailure` for what did not, and both appear under the Formats menu. The SHA-256 of every plugin that loads is printed in the Console pane.

## Opening an archive

`ArchiveSession` owns exactly one open archive at a time.

1. Read the first 4 KB of the chosen file.
2. Ask each format in the catalog, in load order, whether it claims the file. The first `CanOpen` that returns true wins.
3. Call `Open` on that format, on a background thread, handing it an `IHostServices` scoped to the owning plugin.

`CanOpen` gets the head and the path, because not every container can be identified from its opening bytes. Harugeki's `.bin` has no magic number at all; it starts with a count and a table of offsets, so it validates that table against the file's real length. Virtuos checks that the `.gdf` and `.gdt` siblings exist beside the `.gda`. A DS ROM carries no magic number either, so the Nintendo plugin validates the header's section table, offsets, and sizes against the real length of the file it is in.

## The asset tree

An archive exposes a single `Root` node, and the tree grows from it lazily: `HasChildren` must answer without parsing anything, and `GetChildren` runs only when a row is expanded or a search walks it. That is what lets a 133 MB container open instantly.

A node whose payload lives in a stream behind it does not read that payload to describe itself. `IBinaryAsset.Head` answers with a window of the first bytes and `IBinaryAsset.TryGetLength()` answers with a length from the node's own metadata, both with backward-compatible defaults, so recognition and folder expansion read no bytes at all and selecting a multi-gigabyte file costs a property read rather than a file. `Size` falls back to reading the payload only for a node that knows no length and has no head of its own. Reading a streaming node's whole payload keeps it only while it is among the most recently read within the payload-cache budget (see One memory figure).

Nodes describe themselves through capability interfaces rather than a type hierarchy. A node implements as many as apply:

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

`NodeCommands` builds a row's menu purely by testing which of these a node implements, so the host needs no knowledge of what the node is.

A row gets at most one `Export…` and one `Replace…` entry, however many capabilities it has. Each capability, and each plugin command that sets `AssetCommandRole`, contributes file types rather than a menu entry of its own; the dialog offers all of them and the host then runs whichever one claims the extension the user chose, falling back to the raw-bytes handler when nothing claims it. That is why replacing a texture from a PNG and replacing a model from a glTF are one command: the file the user picked says which.

### Recognition

Some payloads are worth understanding regardless of whose container they came out of. An `IAssetRecognizer` claims bytes by content, and `AssetRecognition.Enrich` wraps any plain byte node it claims in a `RecognizedAsset` that forwards identity and bytes to the original while adding decoded behaviour. Recognisers are pooled across every loaded plugin, so the Generic plugin's OGG recogniser gives an Audio pane to a sound found inside a Harugeki archive without either plugin knowing about the other. A node that already carries content of its own is left alone. The same pool answers `IHostServices.IsKnownPayload`, which lets a container walk stop descending when a blob turns out to be a standalone file rather than another table.

Some containers are not one blob at all. A Virtuos archive is three files (data, name table, and entry table), so no single row can be recognised as one. `IContainerRecognizer` is offered every folder's rows instead, after each row has been recognised on its own, and returns the rows to show in their place: the three collapse into one archive row whose children are the tracks and textures inside. Because the pool spans plugins, the same recogniser opens a `.gda` sitting in a folder on disk and one sitting in `data/Publish.NDS` of a DS ROM.

A nested container has no `Save` of its own; only the archive the host opened has one. Whenever it changes, it registers a write-back through `IHostServices.Defer`. Save runs every pending write-back once, each calling `IBinaryAsset.ReplaceBytes` on the rows it was read from, and then the outer archive's Save, so the one Save still rebuilds everything from the outside in. A write-back that throws, such as a rebuilt WAD that no longer fits its slot in a Wii partition, stops the save with "Save failed: an edited container could not be written back into <file>: ..." and nothing written, and the edit stays pending. Host services are routed to the archive they belong to, so a write-back from an archive that has since closed is dropped.

## Editing and undo

Every edit, whether the host's own replace commands or a plugin's alike, arrives as an `AssetCommand`, executed on a background thread with an `AssetCommandContext`. The command reports what it invalidated by setting `Changed`, and the host refreshes only those views. Undo is snapshot-based rather than operation-based, because the host does not know what any given command does. Before running a command that could modify something, `AssetSnapshot.Capture` reads the node's current bytes, or its current image for a format that only swaps images. `EditHistory` holds a byte budget, an eighth of the memory figure (256 MB on an 8 GB machine), rather than a fixed count, because 24 whole payloads is 768 MB for 24 models of 32 MB; the oldest edits are evicted as new ones push the total past the budget, and an edit larger than the whole budget is refused with a message rather than stored silently where it cannot be undone. Two cases refuse an edit rather than risk reverting the wrong thing: a command on a node with neither restorable bytes nor a replaceable image clears the history, and a command whose node type changed mid-execution clears it too; an edit that changed nothing on the expected node is skipped outright.

An editable Properties row is the one edit that does not arrive as a command. A `PropertyRow` carrying an `Apply` is shown as a text box, a check box, or a drop-down; committing it (leaving a text box, or pressing Enter in one) calls the plugin straight back, on the UI thread, and the plugin either stores the value or throws, in which case the message is logged and the pane redrawn from the format. The row reports what it invalidated through the same `AssetChange` flags a command uses. These edits are undoable like any other. The host cannot read a plugin's header fields back generically, so the snapshot is the row's own text: `PropertySnapshot` keeps the value and the delegate, and reversing the edit hands the old text to that same delegate. An `Apply` that would reject a value it produced earlier is therefore an edit that cannot be reversed, and the guarded call turns that into a cleared history rather than a half-reverted format.

When a command throws, the host compares the node with its before-snapshot. If the node changed, it restores the snapshot, keeps the history, and logs "Rolled back <title>, so <node> is as it was before." If there was no snapshot or the restore fails, it refreshes the row and the unsaved-changes state, clears the history, and logs "<title> may have changed <node> before it failed and cannot be undone; the edit history was cleared." Save-to-file and Export-role commands are treated as read-only: no snapshot and no rollback. A command that finishes after its archive was closed is not applied.

## Saving

Saving is one contract, and it is a mechanism rather than advice. Pending nested write-backs (see Recognition) run first, and the host writes exactly one line per save, "Saved <file>."; plugins do not log their own. `IArchive.BackupPaths` names every file `Save` will overwrite, and before the first save of a session writes anything each of those is copied to `<name>.bak`, never overwriting a backup that already exists. Every edit is held in memory until that save, so the file on disk is still pristine when it is copied, and opening an archive from a read-only folder or a multi-gigabyte image costs no copy at all.

`IArchive.Save` then goes through `WriteGuard`, which does three things in order: it writes the rebuilt bytes to a temporary file beside the original and re-parses them from there, so a format that produces something it cannot read back is refused before anything moves; it skips the write entirely when the replacement is byte-identical to what is on the disk; and it moves the temporary over the original with `AtomicSave`, which flushes to the disk first and then renames. A rename either happens or it does not, so a save that fails anywhere leaves the original byte for byte intact, and a multi-file save, a Virtuos `.gda` and `.gdt`, takes a rollback copy of each original and puts back the ones it had already replaced if a later one fails.

Two formats do not rebuild at all and say so instead of writing something wrong. A WBFS or Wii edit is same-length by construction, so the edit goes into a deferred overlay in memory and `Save` commits it: the whole set of cluster writes is validated against the container's own tables first, then written, then flushed once, then one cluster is read back to prove it. The container is held read-only while browsing and opened for writing only for that commit; if it cannot be (a read-only attribute, permissions, another program holding it), the save fails naming the file, nothing is written, and the edits stay pending for a retry. A Wii patch also rebuilds the partition's H0 to H2 hash chain and the master table's H3 entry, because a patched image with a stale chain is an image Dolphin and real hardware reject. A save that fails therefore leaves the original intact by construction: nothing had been written. A PlayStation disc, an Xbox image, and a 3DS image are read-only and raise rather than pretend.

Archive-level backups are the user's safety net; the atomic write is the mechanism. Each temporary is written and flushed once, compared against the original in 1 MB chunks rather than read whole, and deleted on every path out of the save, so a failure leaves no `.omega-save.tmp` behind.

## Reading bytes

One set of primitives serves every parser: `Omega.Plugins.BinaryRead`. Little-endian and big-endian reads, fixed-point, NUL-terminated names, Latin-1, and hexadecimal. Every read bounds-checks its span and refuses an out-of-range one with `InvalidDataException` naming the offset and the span, so a truncated blob is a parse failure with a reason rather than an indexer's own exception. The two cursor shapes built on it, `SpanReader` in Omega.Disc and `Le`, a local alias kept by the Polarbit plugin that forwards to `BinaryRead`, reach the same set, so a read past the end means the same thing and reads the same way wherever it is written.

## Containing plugin failures

A plugin is third-party code parsing formats somebody reverse-engineered, so it will eventually throw. `PluginGuard` wraps every call from the host into a plugin. The rule is that a failure costs the user the one thing they were doing and never the session: an unreadable node shows an error row, a throwing sniff means that format does not claim the file, a throwing property leaves the pane empty. The catches are broad on purpose, since guessing which exception types a plugin author will use, and guessing wrong, turns a contained failure back into a crash. `OutOfMemoryException` is the single exception that is not a plugin failure; every later call will fail the same way, and logging one line per node while continuing turns a clean crash into a hang. That is why parsers check every size they read against the bytes actually present, or a format ceiling, before allocating, so a corrupt header ends as a contained `InvalidDataException` rather than an OOM. The ceilings: a Wii FST of at most 128 MiB; the Wii partition table within its 64 KiB information area; WBFS clusters of 2^15 to 2^30 bytes and no larger than the file; HTX textures of at most 16384x16384 with 32 mips; 4096 texture slots in a Virtuos model; 1 to 255 Wwise Vorbis channels; PS3 `.dkey` files of at most 4 KiB; TJZIP, PLZP, and SWF zlib output limited to what the compressed bytes can produce; and one node's payload at most `Array.MaxLength` bytes.

## Host services

What the host lends a plugin, through `IHostServices`:

- **`Images`**: decode PNG, JPEG, GIF, and BMP; encode PNG, JPEG, and BMP. No plugin carries a codec.
- **`glTF`**: read a glTF or GLB in full, open one for selective reading, or write geometry out. One writer serves every format: a plugin produces neutral `SceneGeometry` and gets export for free.
- **`Log`**: a line in the Console pane, prefixed with the plugin's display name.
- **`PluginDirectory`**: where this plugin was loaded from, for reading data files shipped beside it.
- **`IsKnownPayload`**: described under Recognition above.
- **`Defer(key, flush)`**: a container nested inside the open archive registers its write-back here instead of writing into its parent rows on every change. The host keeps one flush per key (compared by reference; registering again replaces the earlier one) and runs them once, on the pool, at the start of File ▸ Save, before `IArchive.Save`. Pending flushes count as unsaved changes, and closing without saving drops them. The member has a default implementation that runs the flush at once, so it is a binary-compatible addition and `PluginContract.Version` stays 7.

## Rendering

The viewport is an Avalonia `OpenGlControlBase`. `SceneGeometry` arrives in the space glTF uses: right-handed and Y-up, with matrices row by row in System.Numerics' row-vector convention, and texture v running down from the top of the image. The viewport draws that space as it stands: the grid lies on the XZ plane, the camera's up vector is +Y, the default camera looks from the +Z side, and Reset Camera frames the model's height along Y. The glTF writer copies positions, normals, and bind matrices through untransformed, and the reader hands imports over the same way, so an export opens upright in other glTF tools. Converting a format's own axes is the plugin's job, done once at parse time so the viewport, the exporter, and the skinning all agree. Back faces are not culled and the light is a two-sided headlight, so neither winding nor handedness changes what the viewport shows: a left-handed source that is not converted draws, and exports, as its mirror image. Units are not rescaled. FORMATS.md#geometry-space lists what each plugin hands over. `SceneGeometry.Validate` runs before an upload and before an export: array lengths against the vertex count, indices and joint indices in range, every submesh covering real entries with a real texture. A plugin bug is named at that boundary rather than surfacing later as a torn draw or corrupt output. Each mesh vertex is uploaded once: positions and normals sit in one dynamic buffer, uvs and colours in a static one, and every submesh draws as a range of a shared index buffer. Wireframe is a second index buffer of triangle edges over the same vertices, built the first time it is shown. An animated frame replaces only the positions and normals, written straight from the arrays `TryPose` returned, so playing a clip costs no repacking and the wireframe follows the pose for free. GL entry points Avalonia's wrapper lacks or only exposes through pointers (`glUniformMatrix4fv`, `glBufferSubData`) are resolved once per context with `GetProcAddress`. Shaders carry no `#version` line; the preamble is supplied at compile time from the context Avalonia provides, so one copy of each shader serves both desktop GL and GLES.

## Audio

`AudioPlaybackEngine` drives a single OpenAL source fed by one static buffer holding the whole clip. Because the buffer is complete, the driver owns the play position: seeking is a property set on the source, and the position it reports back is the real one, so the UI clock and the audio cannot disagree. One OpenAL context (a device handle) and the AL API are opened when the first clip loads and kept for the session; each clip only deletes and regenerates its source and buffer, and the service releases the context when it is disposed, so switching clips never reopens the device. `AudioPlaybackService` adds the transport on top, one `PlaybackState` rather than several booleans kept consistent by hand, and polls the source every 25 ms, since OpenAL offers no completion callback.

The poll loop owns its timer, and the only thing that happens under the transport lock is cancelling the loop's token; cancelling never waits for the loop to notice. Stopping, pausing, or unloading a clip can therefore never block on a tick that is waiting for the lock the tick itself needs, which is what used to hang the process on a close. OpenAL contexts are current on one thread at a time, so every engine call, including generating, uploading, and deleting a clip's source and buffer, makes the context current first. Loads are serialised with a generation counter, so a clip the user has already left is never played over the newer one. The clip's bytes come from the selection's single read and are reused for the info line, the Extract Audio format, and playback; Play reads again, off the UI thread, only when nothing is loaded.

## Video

`VideoPlayer` decodes ahead on the thread pool: a producer task reads frames from the plugin's `IVideoReader` into a bounded channel six frames deep and swaps them to BGRA there (with `ImageData.SwapRedBlueInPlace`), so the UI thread only copies rows into a `WriteableBitmap`. A render-priority timer, running only while the movie plays or while it waits for the first frame after a bind or seek, picks the newest decoded frame not later than the clock and drops the ones it has passed. The clock is the audio position whenever the soundtrack plays and a stopwatch otherwise, so picture and sound share one time base. A seek cancels the producer, drains the channel back into a buffer pool, and starts a new producer at the target frame without waiting on the UI thread: the new producer waits for the old one to finish before it touches the reader, so the reader is never used by two producers at once, and a superseded producer exits without decoding. Leaving a movie disposes its reader only after its last producer has finished. When paused, the first frame the new producer delivers is shown. The soundtrack goes through the same audio path as any clip, under the same generation counter, so a movie the user has left never starts its sound late.

`MatroskaWriter` exports a movie without outside tools. It writes Motion JPEG frames, encoded in parallel batches, and the decoded soundtrack as interleaved PCM blocks, with a seek head, cues every second and the segment size patched in at the end. The file is written beside the target and moved over it only when complete.

## Threading

The UI thread owns the view models and the tree. Archive opening, command execution, and saving all run on the thread pool, as do selecting a row (see Showing a row) and expanding one: a folder with ten thousand rows builds its children on the pool and publishes them on the UI thread, and a build that is superseded before it finishes is abandoned rather than published. A row builds its children at most once, behind a `Lazy`, and expanding a row a search has already resolved into reuses that build. A right-click on a row still being read opens its menu once its commands are ready.

The outliner is virtualised: it is a `ListBox` over a flat list of the visible rows (`OutlinerRows`), indented by depth, so only the rows on screen have containers. Expanding or collapsing a row splices its visible descendants in or out of that list rather than rebuilding it. Rows carry no per-row menu or segment list: one `ContextMenu` sits on the outliner, each header is one text block, and highlight runs exist only on search-result rows.

A disc image is read through `SharedStream`, one file handle per archive with every access gated. Per-reader handles would cost an open and a close per cluster, which is a syscall storm across a multi-gigabyte scan, and memory-mapped views do not fix the write side; the gate serialises a position-and-read instead of letting two readers serve each other's bytes. A WBFS container is browsed through a read-only handle; `SharedStream.Reopen` swaps it to read-write only while Save writes, flushes, and reads back the deferred clusters, then swaps back. The swap closes and reopens the handle under the same gate, because Windows share modes do not allow upgrading a handle in place, and reads that arrive meanwhile wait for it. The read-only images (Xbox 360 and 3DS) hand out `WindowStream`s over one `FileStream`; each window reads with positional I/O (`RandomAccess.Read`), so windows never share a position and need no gate at all. A PlayStation disc goes through DiscUtils, which is not thread-safe, so its listings and reads take the archive's own lock.

The search walk is debounced by 200 ms and carries a cancellation token, so typing cancels the previous walk rather than queueing another. The walk runs on the pool through the same child-building logic as expansion (`TreeBuilder.Children`: recognition, then container grouping). It reuses a row's children where they are already built and otherwise enumerates throwaway rows that it drops as it goes. It records each hit's position, then resolves hits to the tree's own rows by building only their ancestors, so result rows carry the tree's rows and nodes, and an edit, command, or undo through a result changes what the tree shows. The walk is capped at 5,000 matches and 256 levels, and the cap is reported, so memory stays proportional to the matches plus the child lists of their ancestors. Overlapping opens are refused by a single slot rather than racing. The audio transport's state is locked at the object that owns it. The audio poll timer fires on the pool and raises `StateChanged` without marshalling, so subscribers post to the UI thread themselves.
