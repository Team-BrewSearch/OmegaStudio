# Omega Studio

A game asset editor that knows no formats.

Browse an archive in the Outliner, view models and levels in a hardware 3D viewport,
textures in 2D, play back audio and animation, export to glTF and PNG, and replace
assets back into the archive.

Every format it can open comes from a plugin - a single `.dll` in a `formats/` folder
beside the executable. A plugin says what a node *is* and the window assembles itself
around that: a node holding an image gets a texture pane with zoom and pan, a node
holding audio gets a transport, a node holding neither gets neither. No host code knows
any format's name, and a plugin writes none of the panes.

## What ships with it

| Plugin | Game | Opens |
|---|---|---|
| **Harugeki** | *Suzumiya Haruhi no Chourantou / Gekitou* (PC, 2007) | `data/*.bin` - models, `ringtex2` textures, animation |
| **Kartz** | *DreamWorks Super Star Kartz* and *Madagascar Kartz* (NDS) | `.gda`/`.gdf`/`.gdt` - `TRK` tracks and `5TX` textures |
| **Generic** | any | recognises OGG, WAV, PNG, JPEG, GIF and BMP wherever they are found |

`Generic` is why neither game plugin contains a line of audio code: it recognises
standard payloads inside *anyone's* archive, so a WAV gets a transport whichever format
it was found in.

Both game plugins round-trip their archives byte for byte against the real retail
data - open one, save it untouched, and the file is unchanged.

## Build and run

```bash
dotnet build src/OmegaStudio.slnx
```

```bash
dotnet run --project src/OmegaStudio
```

The plugins build into `formats/` beside the executable automatically, one flat `.dll`
each. **Formats** in the menu bar lists every loaded plugin - what it opens, who wrote
it, what version - and anything that failed to load, with the reason.

For a release build, `src/publish.cmd` publishes single-file executables for win-x64,
linux-x64 and linux-arm64 into `src/publish/<runtime>/`, each with its plugins
alongside. It builds the solution first, because the app does not reference the
plugins and so cannot pull them into a publish on its own.

## Documentation

- **[Writing a format plugin](docs/PLUGINS.md)** - the guide. Start here if you want
  to add a game.
- **[Architecture](docs/ARCHITECTURE.md)** - the layers, what crosses the seam, and
  why it is shaped this way.

## A note on plugins and trust

.NET has no in-process sandbox: a DLL in `formats/` runs with the same privileges the app does. Nothing is ever downloaded or installed automatically, the folder is yours but treat a format plugin exactly as you would any other program you chose to run. [PLUGINS.md §9](docs/PLUGINS.md#9-security--what-this-does-and-does-not-protect-against) sets out what the loader does and does not guarantee.

## Licence

MIT. See [LICENSE](LICENSE).
