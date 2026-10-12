# Development

This guide covers building, running, and releasing Omega Studio from source. For the plugin
contract see [PLUGINS.md](PLUGINS.md); for how the host works see
[ARCHITECTURE.md](ARCHITECTURE.md).

## Prerequisites

The [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else: the app is Avalonia,
the plugins depend on no system libraries, and the Linux builds are self-contained.

## Build and run

```bash
git clone https://github.com/Team-BrewSearch/OmegaStudio.git
cd OmegaStudio
dotnet build src/OmegaStudio.slnx
dotnet run --project src/OmegaStudio/OmegaStudio.csproj
```

Build the solution, not just the host project. Building `OmegaStudio.csproj` on its own
produces an application with an empty `formats` folder, and an application with no plugins
opens nothing. Each plugin project copies its assembly into the host's `formats` folder after
every build, so a plain solution build produces a working application.

You can pass an archive on the command line to open it straight away:

```bash
dotnet run --project src/OmegaStudio/OmegaStudio.csproj -- path/to/archive.bin
```

## Repository layout

```
src/
  Omega.Plugins/             The plugin contract. The only public API, and the only
                             assembly a plugin references.
  OmegaStudio/               The application: UI, rendering, audio, plugin host.
  Omega.Disc/                Disc formats: bounded readers, cluster mapping,
                             decryption streams. Shared by the console plugins.
  Formats/
    Harugeki/                Suzumiya Haruhi no Chourantou / Gekitou (PC)
    Virtuos/                 Virtuos racing engine (Nintendo DS)
    Nintendo/                Nintendo DS ROM images, WBFS Wii containers, 3DS cart images
    Polarbit/                Polarbit racing engine (iOS)
    Generic/                 OGG, WAV, PNG, JPEG, GIF, BMP recognisers
    PlayStation/             PlayStation 3 game discs
    Xbox/                    Xbox 360 game discs
  Plugin.props               Build logic every plugin project imports
  Directory.Build.props      Settings shared by all projects
  Directory.Build.targets    Where the host's formats folder is
  OmegaStudio.slnx           The solution
  publish.cmd, publish.sh    Release publishers (Windows, Linux)
docs/
  ARCHITECTURE.md            How the host works
  DEVELOPMENT.md             This file
  FORMATS.md                 What each shipped format supports
  PLUGINS.md                 How to write a plugin
  USER-GUIDE.md              How to use the application
```

## Conventions the build enforces

Every project is built with warnings as errors, so a clean build is the bar. The settings
live in `src/Directory.Build.props`:

- `net10.0`, nullable reference types, and implicit usings are on everywhere.
- The full recommended analyzer set runs at `latest-recommended`, with code style enforced
  during the build.
- NuGet audit runs at every level, with five advisories currently suppressed in
  `Directory.Build.props`; a new advisory fails the build.
- Debug builds carry portable symbols; release builds embed theirs.

`Omega.Plugins` is the public surface, so it changes rarely and deliberately: the
`ContractVersion` a plugin reports must match `PluginContract.Version`, and a mismatch
refuses to load rather than misbehave at runtime.

## Adding a format plugin

Copy the shape of an in-tree plugin: a project under `src/Formats/` that sets
`OmegaPluginName` and imports `../../Plugin.props`, then add it to `src/OmegaStudio.slnx`.
`Plugin.props` names the assembly `<name>.Formats`, references `Omega.Plugins` without
copying it, embeds the plugin's own dependencies inside the assembly, and copies the built
DLL into the host's `formats` folder. The full walkthrough is
[PLUGINS.md](PLUGINS.md).

## Publishing

`src/publish.cmd` (Windows) and `src/publish.sh` (Linux) produce release binaries for four
runtimes:

| Runtime | Output | Kind |
| --- | --- | --- |
| `win-x86` | `src/publish/win-x86` | framework-dependent, single file |
| `win-x64` | `src/publish/win-x64` | framework-dependent, single file |
| `linux-x64` | `src/publish/linux-x64` | self-contained, compressed single file |
| `linux-arm64` | `src/publish/linux-arm64` | self-contained, compressed single file |

The Linux targets bundle the .NET runtime, so they run on a machine with nothing installed.
The Windows targets expect a .NET runtime to be present. Each output directory holds the host
beside its `formats` folder, and the publisher fails if a target ends up with no plugins,
because that build would open nothing.

## Continuous integration

`.github/workflows/dotnet-desktop.yml` runs on every push and pull request to `main`. It
restores the solution, then builds and publishes each of the four runtimes on its own
operating system. Every published build is checked for at least one format plugin before its
artifact is uploaded, so a release that opens nothing cannot land on `main`.
