# Using Omega Studio

This guide covers the application itself: opening archives, the panes, editing, and saving.
For what each shipped format supports, see [FORMATS.md](FORMATS.md). For how the pieces work
internally, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Opening an archive

Choose **File ▸ Open** (or `Ctrl+O`) and pick a file. You can also pass one on the command
line:

```bash
dotnet run --project src/OmegaStudio/OmegaStudio.csproj -- path/to/archive.bin
```

One archive is open at a time. **File ▸ Close** closes it.

The host asks each loaded format, in load order, whether it claims the file, and the first
one that says yes opens it. If no format claims the file you get a message saying so, and the
Console pane holds the detail. A format that throws while looking at the file is logged in the
Console and counted as a no, so one bad sniffer does not stop the rest of the check.

Opening is lazy. The tree reads no payload to describe itself, so even a large container
opens instantly and rows expand on demand.

## The interface

The window has three columns and a lower tray. The left column is the outliner with a search
box above it. The middle column holds the panes; the lower tray holds the Console and, when
the selected row has clips, the Animation pane. The right column is Properties. A status bar
runs along the bottom.

### The outliner

Every row inside the archive appears here with its name, a colour dot for its kind, and a
short detail. Folders expand only when you open them.

The search box above the tree filters the rows and highlights the part of each name that
matched. Typing restarts the walk rather than queueing behind the previous one, and the
result set is capped; when the cap trims matches, the status bar says so.

### Viewport

The Viewport appears for anything with geometry. Pick a shading mode from the toolbar:
Textured, Shaded, Wireframe, Normals, or UV. **Reset Camera** puts the view back where it
started.

Drag to orbit, Shift-drag to pan, and use the wheel to zoom.

### Texture

The Texture pane appears for anything that decodes to an image. **Fit** scales the image to
the pane, **1:1** shows it at its true size, and the toolbar reports the current zoom. The
wheel zooms and dragging pans.

### Audio

The Audio pane appears for anything that decodes to sound. Clips in OGG or WAV play. The
pane has a time readout, a seek bar, and Play, Pause / Resume, and Stop buttons. The
transport follows the real play position, so the clock and the sound never disagree.

### Video

The Video pane appears for anything that decodes to a movie. It shows the codec, size, frame
rate and frame count, and plays the movie with its sound. The picture follows the sound's
clock, so frames that cannot keep up are dropped rather than letting the two drift apart.
Drag the seek bar to scrub; the frame under the bar shows when you let go, playing or paused.
The readout gives the time and the frame number.

### Animation

The Animation pane appears for anything with clips. Choose a clip, set the playback rate in
frames per second, and play or stop it. The time readout counts seconds and the dope sheet
below shows where you are in the clip.

### Properties

The Properties pane lists what the selected row knows about itself. Some rows are editable,
and the plugin decides how: a text box, a check box, or a drop-down. A text box commits when
you leave it or press Enter.

A value the format cannot hold is refused. The message you see is the one the format threw,
and the pane redraws from the format, so a rejected edit visibly reverts. Accepted edits are
undoable like any other.

### Console

The Console pane is a timestamped log of everything that happens: what loaded, what opened,
what failed, and what was written. Every plugin that loads prints its SHA-256 here, so what
actually ran is on the record. When something goes wrong, read this first.

### Menus

| Menu | Holds |
| --- | --- |
| File | Open, Save, Close, Exit |
| Edit | Undo, Redo |
| Formats | Loaded, Failed to load, and the path of the formats folder |
| Help | About |

The Formats menu is also the plugin report. **Loaded** lists each plugin with its version,
description, the formats it contributes, and its id and author. **Failed to load** lists the
ones that were rejected, each with the reason.

## What a row can do

Right-click a row. The menu is built from what the row can actually do, not from a fixed
list:

- **Extract** writes the row's raw bytes to a file of your choosing.
- **Extract Audio** copies the row's sound out in the form it is stored in: an OGG clip keeps
  its extension, and anything else is offered as a WAV.
- **Export…** turns the row into a file: a PNG, JPEG, or BMP for an image, a glTF for a
  scene, and for a movie the frame on screen as an image, the soundtrack as a WAV, or the
  whole movie as an `.mkv` file (Motion JPEG with PCM sound). One entry offers every type the
  row supports, and the extension you pick in the dialog decides which one runs.
- **Export all frames…** writes every frame of a movie to a folder as numbered PNG files.
- **Replace…** writes a file back into the row: raw bytes, a PNG over an image, or a glTF
  over a model, wherever the format supports it. Again one entry, decided by the file you
  pick.
- Plugin commands: anything specific to the format, such as a tool for one of its structures.

## Editing and history

Every edit is undoable. `Ctrl+Z` steps back, `Ctrl+Y` steps forward.

The history works from snapshots rather than from reversing operations, because the host
does not know what any given edit did. Before an edit that can change something, it keeps
the row's bytes as they were. The history holds up to 256 MB of snapshots; the oldest ones
are dropped as new edits push past that, and an edit too large to snapshot is refused with a
message instead of being stored where it cannot be undone.

Two situations clear the history rather than offer a broken undo: an edit to something with
no restorable bytes, and an edit that changed a row other than the one it ran on.

Editable Properties rows use the same history. The row's old text is handed back to the
plugin to reverse the edit.

## Saving

Nothing is written until you save. **File ▸ Save** (`Ctrl+S`) rewrites the archive in place.

The first save of a session copies every file the save will touch to `<name>.bak` first. A
backup that already exists is never overwritten, so the pristine copy exists before anything
has changed rather than after the damage.

The save itself is guarded. The rebuilt bytes go to a temporary file beside the original,
are re-parsed from there, and a format that cannot read its own output is refused before
anything moves. A replacement identical to what is already on disk is skipped. The final
write flushes to disk and then renames, so a failed save leaves the original byte for byte
intact. A multi-file archive takes a rollback copy of each original and restores the ones it
already replaced if a later one fails.

Formats that only make same-length writes, such as a WBFS container, hold their edits in a
deferred overlay until you save, then validate the whole set against the container's own
tables, write, flush, and read one cluster back to prove it. A Wii image also has its hash
chain rebuilt so that Dolphin and real hardware accept the patched file.

PlayStation 3, Xbox 360, and 3DS images are read-only. Saving one raises an error rather than
writing something wrong.

Closing the archive or exiting the app with unsaved changes asks whether to save, discard,
or cancel.

## Shortcuts

| Key | Action |
| --- | --- |
| `Ctrl+O` | Open an archive |
| `Ctrl+S` | Save |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |
| `Alt+F4` | Exit |

## When something goes wrong

**An archive will not open.** No loaded format claims it. The Console pane holds the failure
message, along with any format that threw while looking at the file. If the file comes from a
game this build does not ship a plugin for, that is the reason; a new plugin is all it takes
to teach the host the format.

**A plugin failed to load.** The reason is listed under **Formats ▸ Failed to load**, and the
other plugins still load.

**A save failed.** The original file is untouched; the message in the Console pane says what
refused it.

**A row shows an error.** A plugin threw while reading that row. The rest of the application
keeps working, and the failure costs you only that row.
