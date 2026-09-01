# Architecture

Omega Studio is an asset browser that knows no formats.
A plugin supplies everything it can open, draw, or edit, and the whole boundary between the two is the `Omega.Plugins` assembly.
This document describes how the host uses that boundary. For the other side of it, how to implement a plugin, see [PLUGINS.md](PLUGINS.md).

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
      ┌───────────────┬───────────────┴───────────────┐
      │  Harugeki     │  Kartz        │  Generic      │  …loaded at runtime
      │  .Formats     │  .Formats     │  .Formats     │
      └───────────────┴───────────────┴───────────────┘
```

Plugins never reference `OmegaStudio`.
The arrow only points one way, which is what lets a plugin be built and dropped in without the host being rebuilt.
`Omega.Plugins` is the only assembly in the tree that is fully documented, and it is built with `DocumentEveryMember` so a public member without an XML comment fails the build.
Everything else exempts itself in `Directory.Build.targets`.

## Loading

`PluginLoader` scans `formats/` beside the executable for `*.Formats.dll`, in ordinal filename order.
Each file is loaded into its own `AssemblyLoadContext` (`PluginLoadContext`), which resolves that plugin's private dependencies from its own folder.
One assembly is deliberately not isolated: `Omega.Plugins` is forced back to the host's copy, because a plugin returning types from a second, separately loaded copy of the contract would not be assignable to the interfaces the host holds.

A plugin is rejected, with the reason logged to the Console pane, when it:
-   has no `[assembly: OmegaPlugin(typeof(...))]` attribute;
-   names a type that does not implement `IFormatPlugin` or cannot be constructed;
-   reports a `ContractVersion` other than `PluginContract.Version`;  
-   has a blank `Id`, or an `Id` another loaded plugin already claimed.

One bad plugin never stops the others.
The result is a `PluginCatalog` of what loaded and a list of `PluginFailure` for what did not, and both appear under the Formats menu.

## Opening an archive

`ArchiveSession` owns exactly one open archive at a time.
1.  Read the first 4 KB of the chosen file.
2.  Ask each format in the catalog, in load order, whether it claims the file. The first `CanOpen` that returns true wins.
3.  Call `Open` on that format, on a background thread, handing it an `IHostServices` scoped to the owning plugin.

`CanOpen` gets the head and the path, because not every container can be identified from its opening bytes.
Harugeki's `.bin` has no magic number at all; it starts with a count and a table of offsets, so it validates that table against the file's real length.
Kartz checks that the `.gdf` and `.gdt` siblings exist beside the `.gda`.

## The asset tree

An archive exposes a single `Root` node, and the tree grows from it lazily: `HasChildren` must answer without parsing anything, and `GetChildren` runs only when a row is expanded.
That is what lets a 133 MB container open instantly.
Nodes describe themselves through capability interfaces rather than a type hierarchy. A node implements as many as apply:

| Interface | Earns |
| --- | --- |
| `IBinaryAsset` | Extract, and a raw file type under Replace when it says bytes can be swapped |
| `IImageAsset` | The Texture pane, and PNG, JPEG and BMP under both Export and Replace |
| `IAudioAsset` | The Audio pane and Extract Audio |
| `ISceneAsset` | The Viewport, and glTF under Export |
| `IAnimatedAsset` | The Animation pane, its clip picker and dope sheet |
| `IDescribable` | Rows in the Properties pane |
| `ICommandSource` | Extra entries in the row's context menu |

`NodeCommands` builds a row's menu purely by testing which of these a node implements, so the host needs no knowledge of what the node actually is.

A row gets at most one `Export…` and one `Replace…` entry, however many capabilities it has.
Each capability, and each plugin command that sets `AssetCommandRole`, contributes file types rather than a menu entry of its own; the dialog offers all of them and the host then runs whichever one claims the extension the user chose, falling back to the raw-bytes handler when nothing claims it.
That is why replacing a texture from a PNG and replacing a model from a glTF are one command: the file the user picked says which.

### Recognition

Some payloads are worth understanding regardless of whose container they came out of.
An `IAssetRecognizer` claims bytes by content, and `AssetRecognition.Enrich` wraps any plain byte node it claims in a `RecognizedAsset` that forwards identity and bytes to the original while adding decoded behaviour.
Recognizers are pooled across every loaded plugin, so the Generic plugin's OGG recognizer gives an Audio pane to a sound found inside a Harugeki archive without either plugin knowing about the other.
A node that already carries content of its own is left alone.
The same pool answers `IHostServices.IsKnownPayload`, which lets a container walk stop descending when a blob turns out to be a standalone file rather than another table.

## Editing and undo

Every edit, whether the host's own replace commands and a plugin's alike, arrives as an `AssetCommand`, executed on a background thread with an `AssetCommandContext`. The command reports what it invalidated by setting `Changed`, and the host refreshes only those views.
Undo is snapshot-based rather than operation-based, because the host does not know what any given command does. Before running a command that could modify something, `AssetSnapshot.Capture` reads the node's current bytes, or its current image for a format that only swaps images. `EditHistory` keeps up to 24 of those, as one list with a cursor.
Two cases clear the history rather than risk reverting the wrong thing: a command on a node with neither restorable bytes nor a replaceable image, and a command that edits a node other than the one it ran on.

## Saving

Saving is explicit. `IArchive.BackupPaths` names every file `Save` will overwrite, and on the first save of a session each of those is copied to `<name>.bak`, never overwriting a backup that already exists.
Only then does `Save` run.
Formats are expected to serialise fully in memory before touching the file, so a failure part-way through a rebuild cannot leave a truncated archive where the original was.

## Containing plugin failures

A plugin is third-party code parsing formats somebody reverse-engineered, so it will eventually throw.
`PluginGuard` wraps every call from the host into a plugin.
The rule is that a failure costs the user the one thing they were doing and never the session: an unreadable node shows an error row, a throwing sniff means that format simply does not claim the file, a throwing property leaves the pane empty.
The catches are broad on purpose, guessing which exception types a plugin author will use, and guessing wrong turns a contained failure back into a crash.
`OutOfMemoryException` is the single exception that is not a plugin failure; every later call will fail the same way, and logging one line per node while continuing turns a clean crash into a hang.

## Host services

What the host lends a plugin, through `IHostServices`:
-   **`Images`** - decode PNG, JPEG, GIF, and BMP; encode PNG, JPEG and BMP. No plugin carries a codec.
-   **`glTF`** - read a glTF or GLB in full, open one for selective reading, or write geometry out. One writer serves every format: a plugin produces neutral `SceneGeometry` and gets export for free.
-   **`Log`** - a line in the Console pane, prefixed with the plugin's display name.
-   **`PluginDirectory`** - where this plugin was loaded from, for reading data files shipped beside it.
-   **`IsKnownPayload`** - described under Recognition above.

## Rendering

The viewport is an Avalonia `OpenGlControlBase`.
`SceneGeometry` arrives already in a right-handed, Z-up space; converting handedness is the plugin's job, done once at parse time so the viewport, the exporter, and the skinning all agree.
`SceneGeometry.Validate` runs before an upload and before an export: array lengths against the vertex count, indices and joint indices in range, every submesh covering real entries with a real texture.
A plugin bug is named at that boundary rather than surfacing later as a torn draw or corrupt output.
Shaders carry no `#version` line; the preamble is supplied at compile time from the context Avalonia actually provided, so one copy of each shader serves both desktop GL and GLES.

## Audio

`AudioPlaybackEngine` drives a single OpenAL source fed by one static buffer holding the whole clip.
Because the buffer is complete, the driver owns the play position: seeking is a property set on the source, and the position it reports back is the real one, so the UI clock and the audio cannot disagree.
`AudioPlaybackService` adds the transport on top, one `PlaybackState` rather than several booleans kept consistent by hand, and polls the source every 25 ms, since OpenAL offers no completion callback.

## Threading

The UI thread owns the view models and the tree.
Archive opening, command execution and saving all run on the thread pool.
The search walk is debounced by 200 ms and carries a cancellation token, so typing cancels the previous walk rather than queuing another.
When a background thread reaches an archive's alias table, the audio transport's state is locked at the object that owns it.
The audio poll timer fires on the pool and raises `StateChanged` without marshalling, so subscribers post to the UI thread themselves.