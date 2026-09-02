# Omega Studio

Omega Studio is a desktop application for browsing and editing game asset archives.
It lets users navigate, extract, and modify assets across formats.
The application itself knows nothing about any file format; every format it can open arrives as a plugin dropped into a `formats` folder beside the executable.


Built with .NET 10 and [Avalonia](https://avaloniaui.net/), the application is cross-platform, so the same codebase runs on both Windows and Linux.

## What it does

Open an archive and you get a tree of everything inside it.
Select a row and the panes that make sense for it appear:

| Pane | Appears for | What it gives you |
| --- | --- | --- |
| **Viewport** | anything with geometry | Orbit camera, five shading modes; textured, shaded, wireframe, normals, UV |
| **Texture** | anything that decodes to an image | Zoom, fit, actual size |
| **Audio** | anything that decodes to sound | Play, pause, stop and a seek bar |
| **Animation** | anything with clips | Clip picker, transport and a dope sheet |
| **Console** | always | Every load, edit and failure, timestamped |

The Properties pane lists what the selected row knows about itself, and a plugin can make a row editable: a text box, a check box or a drop-down beside the label, written straight back into the format.

Alongside the panes, each row carries a context menu built from what it can actually do: extract the raw bytes, replace them from a file, export a PNG or a glTF, replace an image from a PNG, plus whatever commands the owning plugin adds.

Editing is undoable. The history keeps the last 24 edits as before-and-after snapshots, so `Ctrl+Z` works across formats the host has never heard of.

Nothing is written until you ask. `File ▸ Save` rewrites the archive in place, and the first save of a session copies each affected file to `<name>.bak` first.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/Team-BrewSearch/OmegaStudio.git
cd OmegaStudio
dotnet build src/OmegaStudio.slnx
dotnet run --project src/OmegaStudio/OmegaStudio.csproj
```

Building the solution also copies each plugin into the host's `formats` folder, so a
plain `dotnet build` produces a working application. Building only
`OmegaStudio.csproj` produces one that opens nothing.

You can also pass an archive on the command line, or associate the file type with the
executable:

```bash
dotnet run --project src/OmegaStudio/OmegaStudio.csproj -- path/to/archive.bin
```

### Shortcuts

| Key | Action |
| --- | --- |
| `Ctrl+O` | Open |
| `Ctrl+S` | Save |
| `Ctrl+Z` | Undo |
| `Ctrl+Y` | Redo |

## Formats it ships with

Four plugins are built from this repository.

**Harugeki** - *Suzumiya Haruhi no Chourantou / Gekitou* (PC).
Reads the `data/*.bin` containers: models, embedded `ringtex2` textures, materials, skeletons and animation. Textures and raw payloads can be replaced and written back.

**Kartz** - *Madagascar Kartz* and *DreamWorks Super Star Kartz* (Nintendo DS).
Reads the GDA/GDF/GDT archive triple: track geometry and textures, with the resource database picked up automatically when it sits beside the archive. The triple is recognised wherever it turns up, so the archive opens and edits in place inside a DS ROM as well as from a folder on disk.

**Nintendo DS** - any DS ROM image (`.nds`, `.srl`).
Reads the ROM's file system, its header and its banner through [SceneGate Ekona](https://github.com/SceneGate/Ekona): every file can be extracted or replaced, the banner icon shows in the Texture pane and can be replaced from a PNG, and the header and the banner text, in every language the banner holds, are edited in the Properties pane. Saving rebuilds the whole image.

**Standard Formats** - no archives of its own.
It recognises OGG, WAV, PNG, JPEG, GIF and BMP payloads wherever they turn up, so a sound buried inside another container still gets an Audio pane and an extract command.

## Layout

```
src/
  Omega.Plugins/       The plugin contract. Fully documented; the only public API.
  OmegaStudio/         The application: UI, rendering, audio, plugin host.
  Formats/
    Harugeki/          Suzumiya Haruhi no Chourantou / Gekitou
    Kartz/             Madagascar Kartz / Super Star Kartz
    Nintendo/          Nintendo DS ROM images
    Generic/           OGG, WAV, PNG, JPEG, GIF, BMP recognisers
docs/
  ARCHITECTURE.md      How the pieces fit together
  PLUGINS.md           How to write a plugin
```

## Writing a plugin

A plugin is one assembly named `*.Formats.dll`, referencing `Omega.Plugins` and nothing else of the host's; libraries it depends on are embedded in it, so it stays a single file.
Implement `IFormatPlugin`, name it with an assembly attribute, drop the file in `formats`, and it loads on the next launch.

See [docs/PLUGINS.md](docs/PLUGINS.md) for the walkthrough and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the host uses what you implement.

## Publishing

`src/publish.cmd` builds release binaries for `win-x86`, `win-x64`, `linux-x64` and
`linux-arm64` into `src/publish/<runtime>`, each a single file with the plugins
copied alongside.
The Linux targets are self-contained; the Windows ones expect a .NET runtime to be installed.

## Licence

See [LICENSE](LICENSE).