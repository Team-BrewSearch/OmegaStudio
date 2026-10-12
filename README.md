# Omega Studio

Omega Studio is a desktop application for browsing and editing game asset archives. It lets
you navigate, extract, and modify assets across formats. The application itself knows
nothing about any file format; every format it can open arrives as a plugin dropped into a
`formats` folder beside the executable.

Built with [.NET 10](https://dotnet.microsoft.com/) and
[Avalonia](https://avaloniaui.net/), the application is cross-platform, so the same codebase
runs on both Windows and Linux.

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/Team-BrewSearch/OmegaStudio.git
cd OmegaStudio
dotnet build src/OmegaStudio.slnx
dotnet run --project src/OmegaStudio/OmegaStudio.csproj
```

Building the solution also copies each plugin into the host's `formats` folder, so a plain
`dotnet build` produces a working application. Building only `OmegaStudio.csproj` produces
one that opens nothing. You can also pass an archive on the command line:

```bash
dotnet run --project src/OmegaStudio/OmegaStudio.csproj -- path/to/archive.bin
```

The [user guide](docs/USER-GUIDE.md) covers the application in full.
[DEVELOPMENT.md](docs/DEVELOPMENT.md) covers building from source and the release publisher.

## What it does

Open an archive and you get a tree of everything inside it. Select a row and the panes that
make sense for it appear:

| Pane | Appears for | What it gives you |
| --- | --- | --- |
| Viewport | anything with geometry | Orbit camera, five shading modes: textured, shaded, wireframe, normals, UV |
| Texture | anything that decodes to an image | Zoom, fit, actual size |
| Audio | anything that decodes to sound | Play, pause, stop, and a seek bar |
| Animation | anything with clips | Clip picker, transport, and a dope sheet |
| Console | always | Every load, edit, and failure, timestamped |

The Properties pane lists what the selected row knows about itself. A plugin can also make a
row editable: a text box, a check box, or a drop-down beside the label, written straight back
into the format.

Each row also carries a context menu built from what it can actually do: extract the raw
bytes, replace them from a file, export a PNG or a glTF, replace an image from a PNG, plus
whatever commands the owning plugin adds.

Editing is undoable. The history keeps up to 256 MB of before-and-after snapshots, so
`Ctrl+Z` works across formats the host has never heard of.

Nothing is written until you ask. `File ▸ Save` rewrites the archive in place, and the first
save of a session copies each affected file to `<name>.bak` first.

## Formats it ships with

Seven plugins are built from this repository:

| Plugin | Games |
| --- | --- |
| Harugeki | Suzumiya Haruhi no Chourantou / Gekitou (PC) |
| Virtuos | Hot Wheels: Battle Force 5 and four other DS racing games |
| Nintendo | DS ROMs, WBFS Wii containers, 3DS cart images |
| Polarbit | Crash Bandicoot Nitro Kart 2 and 3D and three other iOS games |
| PlayStation 3 | Game discs |
| Xbox 360 | Game discs |
| Standard Formats | OGG, WAV, PNG, JPEG, GIF, BMP payloads, anywhere |

[FORMATS.md](docs/FORMATS.md) says exactly what each one opens and what it can write back.

## Writing a plugin

A plugin is one assembly named `*.Formats.dll`, referencing `Omega.Plugins` and nothing else
of the host's; libraries it depends on are embedded in it, so it stays a single file.
Implement `IFormatPlugin`, name it with an assembly attribute, drop the file in `formats`,
and it loads on the next launch.

See [docs/PLUGINS.md](docs/PLUGINS.md) for the walkthrough and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the host uses what you implement.

## Documentation

| Document | Covers |
| --- | --- |
| [USER-GUIDE.md](docs/USER-GUIDE.md) | Using the application: panes, editing, saving |
| [FORMATS.md](docs/FORMATS.md) | What each shipped format opens and writes back |
| [PLUGINS.md](docs/PLUGINS.md) | Writing a plugin |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the host works |
| [DEVELOPMENT.md](docs/DEVELOPMENT.md) | Building, publishing, and continuous integration |

## Licence

Released under the MIT License. See [LICENSE](LICENSE).
