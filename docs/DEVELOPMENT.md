# Development

Building, running, and releasing Omega Studio from source.

## Prerequisites

The [.NET SDK](https://dotnet.microsoft.com/download), at whatever the latest installed version is — `global.json` rolls forward to the newest major and allows prerelease SDKs, so CI and local builds both move with the SDK. The language version is set to `latest` in `src/Directory.Build.props`, so it tracks whatever C# the SDK carries. Nothing else: the app is Avalonia, the plugins depend on no system libraries, and the Linux builds are self-contained.

## Build and run

```bash
git clone https://github.com/Team-BrewSearch/OmegaStudio.git
cd OmegaStudio
dotnet build src/OmegaStudio.slnx
dotnet run --project src/OmegaStudio/OmegaStudio.csproj
```

Build the solution, not just the host project.

Pass an archive on the command line to open it straight away:

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
    DWSSK/                   DreamWorks Super Star Kartz (PS3, Xbox 360, Wii, 3DS)
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

Every project builds with warnings as errors, so a clean build is the bar. The settings live in `src/Directory.Build.props`:

- `net10.0`, nullable reference types, and implicit using on everywhere.
- The full recommended analyzer set runs at `latest-recommended`, with code style enforced during the build.
- NuGet audit runs at every level, and a new advisory fails the build. The only suppressions are five ImageSharp advisories (GHSA-gwg2-r3hj-4w44, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w, GHSA-wmxv-xphr-5c9g), scoped to `src/Formats/Nintendo/Nintendo.Formats.csproj`. ImageSharp 3.1.12 reaches that project only as Texim's compile-time dependency; it is not embedded in the plugin and nothing the plugin runs loads it. Texim pins ImageSharp below 4.0, and ImageSharp 4 needs a Six Labors licence key for Release builds.
- Every project restores with a committed `packages.lock.json`. After changing any `PackageReference`, run `dotnet restore src/OmegaStudio.slnx --force-evaluate` and commit the changed lock files; CI restores in locked mode and fails on any drift (NU1004).
- The single-file analyzer is off (`EnableSingleFileAnalyzer=false`), so a publish restore matches the lock files. Host code must not rely on `Assembly.Location`, which is empty in the single-file app; use `AppContext.BaseDirectory`.
- Debug builds carry portable symbols; release builds embed theirs.

`Omega.Plugins` is the public surface, so it changes rarely and deliberately: the `ContractVersion` a plugin reports must match `PluginContract.Version`, and a mismatch refuses to load rather than misbehave at runtime.

## Adding a format plugin

Copy the shape of an in-tree plugin: a project under `src/Formats/` that sets `OmegaPluginName` and imports `../../Plugin.props`, then add it to `src/OmegaStudio.slnx`. `Plugin.props` names the assembly `<name>.Formats`, references `Omega.Plugins` without copying it, embeds the plugin's own dependencies inside the assembly, and copies the built DLL into the host's `formats` folder. A library your plugin never loads can be left out of the embedded set with `<OmegaUnusedDependency Include="Assembly.Name" />`; one left out that is needed after all fails at run time with "a dependency is missing (Assembly.Name)". The full walkthrough is [PLUGINS.md](PLUGINS.md).

## Publishing

`src/publish.cmd` (Windows) and `src/publish.sh` (Linux) produce release binaries for four runtimes:

| Runtime | Output | Kind |
| --- | --- | --- |
| `win-x86` | `src/publish/win-x86` | framework-dependent, single file |
| `win-x64` | `src/publish/win-x64` | framework-dependent, single file |
| `linux-x64` | `src/publish/linux-x64` | self-contained, compressed single file |
| `linux-arm64` | `src/publish/linux-arm64` | self-contained, compressed single file |

The Linux targets bundle the .NET runtime, so they run on a machine with nothing installed. The Windows targets expect a .NET runtime to be present. Each output directory holds the host beside its `formats` folder, and the publisher fails if a target ends up with no plugins, because that build would open nothing.

## Continuous integration

`.github/workflows/dotnet-desktop.yml` runs on every push and pull request to `main`. Its build job restores the solution in locked mode, with the NuGet cache keyed on the lock files, then builds and publishes each of the four runtimes on its own operating system. Every published build is checked for at least one format plugin before its artifact is uploaded, so a release that opens nothing cannot land on `main`.
