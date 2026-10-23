# Writing a plugin

A plugin is a single .NET assembly that teaches Omega Studio one or more file formats. It references `Omega.Plugins` and nothing else of the host's, so it can be built, shipped, and replaced without the application being rebuilt.

## Rules the loader enforces

1. The file is named `*.Formats.dll` and lives in the `formats` folder beside the executable, on its own: whatever else it needs travels inside it.
2. The assembly carries `[assembly: OmegaPlugin(typeof(YourPlugin))]`.
3. That type implements `IFormatPlugin` and has a parameterless constructor.
4. `Info.ContractVersion` matches the host's `PluginContract.Version`. It defaults to the value you compiled against, so leave it alone and rebuild when it changes.
5. `Info.Id` is non-blank and not already claimed by another loaded plugin.
6. When a `trusted-plugins.txt` sits beside the executable, the file's SHA-256 is on it.

Break any of the first five and the plugin is skipped, the reason printed in the Console pane and listed under **Formats ▸ Failed to load**. The others still load.

## Trust

**A plugin is code. Whatever you drop into `formats/` runs with the user's own permissions, reads the files it is given and the folder it is loaded from, and can do anything the user can.** That is the design: a plugin has to be that trusted to parse a format somebody reverse-engineered, and a sandbox would make most of them impossible to write. Trust here is an allowlist: only plugins whose digests appear in `trusted-plugins.txt` run.

If you ship the app so that a user might drop a file into `formats/`, name the plugins your build vouched for. Drop a `trusted-plugins.txt` beside the executable:

```
# one per line: a file name, then its SHA-256
Nintendo.Formats.dll  sha256:<the digest, lower-case hex>
```

Generate the digests from the assembled build, never from a download:

```sh
for f in formats/*.Formats.dll; do
    printf "%s  sha256:%s\n" "$(basename "$f")" "$(sha256sum "$f" | cut -d' ' -f1)"
done > trusted-plugins.txt
```

With that file present, a plugin whose digest is not on the list, or whose file name does not match the name written beside its digest, is refused and the reason is logged. A list that names no valid digest loads nothing rather than everything. Without the file, everything in `formats/` loads. Either way the digest of each file that does load is printed in the Console pane, so what ran is a matter of record.

Plugins built and shipped together with the app (a developer build, or a release you assemble yourself) are already trustworthy in the sense that matters, so the default of loading everything in the folder is right there. The list is for the case where the folder is reachable by something the publisher does not control.

A plugin cannot authenticate against a network, cannot be handed a credential, and is given no more than the path of the file it is currently parsing, the folder it was loaded from, and the codecs described under [Host services](#host-services).

Names that reach a filename are filtered: a row's name is untrusted text and may contain path separators, traversal and a drive letter, so `ImageIO.SafeName` collapses a name to one component and refuses one that still looks like a path. Where the bytes land is still decided by the file picker the user answered, so a plugin chooses neither the destination nor the name of what is written beyond what the row is called.

## Project setup

Copy what the in-tree plugins do. A `.csproj` under `src/Formats/` needs only:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OmegaPluginName>Yourgame</OmegaPluginName>
  </PropertyGroup>
  <Import Project="../../Plugin.props" />
</Project>
```

`Plugin.props` sets the assembly name to `Yourgame.Formats`, references `Omega.Plugins` with `Private="false"` so the contract is not copied beside your plugin, and copies the built DLL into the host's `formats` folder after every build. A plugin may take `PackageReference` dependencies of its own; `Plugin.props` embeds those libraries into your assembly as resources, and the host's load context reads them back out, privately to your plugin. You still ship one file, however many libraries you pull in: the Nintendo plugin carries [SceneGate Ekona](https://github.com/SceneGate/Ekona), Yarhl and Texim inside `Nintendo.Formats.dll`. Libraries your code never loads can be left out with `<OmegaUnusedDependency Include="Assembly.Name" />`; the Nintendo plugin leaves out the eight its code paths never load (ImageSharp, LibTiff, BouncyCastle, JetBrains.Annotations, System.Drawing.Common with System.Private.Windows.Core and .GdiPlus, and Microsoft.Win32.SystemEvents), which keeps it at about 620 KB instead of 10 MB.

Then add the project to `src/OmegaStudio.slnx`. Building outside this tree works the same way: reference `Omega.Plugins.dll`, do not copy it to your output, and put your DLL in `formats`.

## The entry point

```csharp
[assembly: OmegaPlugin(typeof(YourgamePlugin))]

namespace Yourgame.Formats;

public sealed class YourgamePlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "yourgame",
        DisplayName = "Your Game (2004)",
        Version = "1.0.0",
        Author = "You",
        Description = "Models and textures from the game's .pak archives.",
    };

    public IEnumerable<IArchiveFormat> Archives { get; } = [new PakFormat()];
}
```

`Archives`, `Recognizers` and `Containers` all default to empty, so implement only what you need. A plugin that only teaches the host a payload format implements `Recognizers` alone, which is all the Generic plugin does.

## Opening a container

```csharp
public sealed class PakFormat : IArchiveFormat
{
    public string Id => "yourgame.pak";
    public string DisplayName => "Your Game archive (.pak)";
    public FileFilter Filter { get; } = new("Your Game archive", "*.pak");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
    {
        return head.Length >= 4 && head[..4].SequenceEqual("PAK0"u8);
    }

    public IArchive Open(string path, IHostServices host)
    {
        return new PakHandle(path, host);
    }
}
```

`CanOpen` receives the first 4 KB and the path. Answer from the head where you can; reach for the path when the format genuinely cannot be identified without it, such as needing to compare a declared size against the real file length, or checking for sibling files. Return `false` rather than throwing when unsure. A format that throws while sniffing is treated as saying no, and the file may well belong to a plugin that works. Claim narrowly: claiming every `.bin` turns the extension into a monopoly and shuts out any other plugin that also uses it.

`IArchive` is what the host holds while the file is open:

```csharp
internal sealed class PakHandle : IArchive
{
    public string DisplayName { get; }
    public IAssetNode Root { get; }
    public bool IsDirty => _pak.IsDirty;
    public IReadOnlyList<string> BackupPaths => [_path];

    public void Save()
    {
        byte[] bytes = _pak.Serialize();   // fully, before touching the file
        File.WriteAllBytes(_path, bytes);
        _pak.MarkSaved();
    }

    public void Dispose() { }
}
```

Serialize completely in memory first. A rebuild that fails halfway through a write leaves a truncated file where the user's archive used to be. List every file `Save` touches in `BackupPaths`. The host copies each to `.bak` before the first save of a session.

## Nodes

Derive from `AssetNode` for a plain row, or `BinaryAssetNode` when the row owns bytes. Children come from a factory so nothing is parsed until a row is expanded:

```csharp
internal sealed class FolderNode : AssetNode
{
    public FolderNode(PakDirectory directory)
        : base(directory.Name, directory.Path, AssetKinds.Container, $"{directory.Count} entries")
    {
        ChildFactory = () => directory.Entries.Select(Build);
    }
}
```

`Name` is the tree label, and a label often carries what a filename should not: a slot number, a prefix, an extension the payload already names. Override `FileStem` with the row's plain name where it does, because that is what the host offers when the row is extracted or exported. A row named `[00] haruhi00` with a `FileStem` of `haruhi00` extracts as `haruhi00.bin`, and one with no name of its own falls back to its slot number.

Keep `HasChildren` cheap: it is asked for every visible row, and answering it must not parse anything. `BinaryAssetNode` handles the replace plumbing for you. Implement `Data` and `Write`, and override `CanReplaceBytes` when the slot cannot take arbitrary bytes:

```csharp
internal sealed class EntryNode : BinaryAssetNode
{
    public override ReadOnlySpan<byte> Data => _entry.Span;
    public override string Extension => ".dat";
    protected override void Write(byte[] payload) => _entry.Replace(payload);
}
```

### Contract notes

- `MemoryBudget.Total`, `Undo`, `PayloadCache`, `Export`, and `DeferredWrites` are static readonly fields scaled to the machine, not constants. Read them at run time; they cannot appear in `const` fields, attribute arguments, `case` labels, or default parameter values. A plugin built against an older contract keeps the old inlined 2 GB-based figures until it is rebuilt.
- `StreamingAssetNode.Data` may call `IPayloadSource.ReadAll` again after its cached payload was evicted, possibly from another thread, so `ReadAll` must return the same bytes every call. A span already returned from `Data` stays valid, and `ReleasePayload` still drops the payload at once.
- Deprecated, and removed at the next contract version: `WriteGuard.Skipped` (unused); `AtomicSave.FlushToDisk` (`WriteToStream` already flushes); `MemoryBudget.Fits` (compare held plus wanted with the budget) and `MemoryBudget.Describe` (use `Sizes.Describe`); `StreamingAssetNode.ReleaseAll` (use `ReleasePayload`); `BinaryRead.Spells` (use `span.SequenceEqual("text"u8)`; it no longer matches non-ASCII text against `?` bytes).

## Capabilities

These are called on a thread-pool thread when a row is selected: `ISceneAsset.GetScene`, `IImageAsset.Image`, `IAudioAsset.EncodedAudio`, `IVideoAsset.Soundtrack`, `IDescribable.Describe`, and `ICommandSource.GetCommands`. One selection read runs at a time, but it can overlap tree builds, search, and commands on other nodes. `IAnimatedAsset.Clips`, `IVideoAsset.Video` and `OpenVideo`, and `PropertyRow.Apply` are still called on the UI thread.

Add an interface and the matching pane and commands appear. Implement only what the node can honestly do.

**`IImageAsset`**: a decoded picture. `ImageData` is RGBA8, top-down, no row padding. Build it with `ImageData.Create`, which rejects a buffer that does not match the dimensions, so a mistake is named at your boundary rather than surfacing as a torn preview. The host owns the swizzle into whatever the UI toolkit wants; do not do it yourself. If your format stores BGRA, `ImageData.SwapRedBlueInPlace(span)` converts it to RGBA in place (whole 4-byte pixels; a trailing partial pixel is left alone), and `ImageData.SwapRedBlue(span)` returns a converted copy. `SwapRedBlueInPlace` is an additive change, so the contract version is unchanged, but a plugin that calls it needs a host that has it.

**`IAudioAsset`**: the clip still encoded, exactly as stored. The host decodes it.

**`IVideoAsset`**: a movie. `Video` is a `VideoInfo` with the size, the frame rate as a fraction, the frame count and a codec label, all read from the header without decoding anything. `OpenVideo` hands back a fresh `IVideoReader` each call, so the player and an export can decode at the same time without sharing state. `ReadFrame` fills the span with the frame at `Position` as RGBA8, top-down like `ImageData`, and moves `Position` on by one; it returns `false` past the last frame. `Seek` may land anywhere: going back to the previous keyframe and decoding forward is the reader's job, not the host's. `Soundtrack` is the audio as a file the host can already play, normally a WAV, or empty for a silent movie. The host plays and exports it in step with the frames. It reads `Soundtrack` when the movie is selected, to offer its real format (WAV or Ogg Vorbis) under Export, and again for playback, so a plugin that synthesizes it should cache it, as DWSSK's `BinkNode` does.

```csharp
internal sealed class MovieNode(Slot slot) : AssetNode(...), IVideoAsset
{
    public VideoInfo Video => Movie.Info;
    public IVideoReader OpenVideo() => new MovieReader(Movie);
    public ReadOnlySpan<byte> Soundtrack => Movie.SoundtrackWav;
}
```

The Video tab plays, pauses and scrubs the movie, and the context menu offers the current frame as an image, every frame as numbered images, the soundtrack, and the whole movie as a Matroska file (Motion JPEG with PCM sound) that ordinary players open.

**`ISceneAsset`**: geometry as `SceneGeometry`. Positions set the vertex count and every other array must match it or be empty. Submeshes are runs of triangles sharing a material, and their `TextureIndex` points into the scene's own texture list, or `-1` for untextured. Hand over the space glTF uses: right-handed and **Y-up**, matrices row by row in System.Numerics' row-vector convention, texture v running down from the top of the image. Convert your format's axes and handedness once, at parse time, so the viewport, the exporter, and the skinning cannot disagree. Negating two axes is a half turn and keeps the handedness; changing it means negating one axis (or all three), which also reverses the triangle winding. Call `SceneGeometry.Validate()` yourself while developing. The host calls it before every upload and export, and its messages name the offending mesh.

**`IAnimatedAsset`**: clips the transport can play, plus `TryPose` to sample one. Sampling is yours: the host asks for a time in seconds and expects posed positions and normals back. Return them for every vertex of every mesh, concatenated in mesh order: `scene.VertexCount * 3` floats each. The viewport ignores a positions array of any other length, and normals beyond the end of a shorter array keep the value they last had.

**`IDescribable`**: rows for the Properties pane.

```csharp
public IEnumerable<PropertyRow> Describe()
{
    yield return PropertyRow.Of("Dimensions", $"{Width}x{Height}");
    yield return PropertyRow.Of("Entries", _entries.Count);
}
```

A row can also be editable. `PropertyRow.Editable` gives it a text box, `Switch` a check box and `Options` a drop-down, and the delegate you hand over is called with the committed value, once the user leaves the box or presses Enter:

```csharp
public IEnumerable<PropertyRow> Describe()
{
    yield return PropertyRow.Editable("Game title", _header.Title, value => _header.Title = Title(value));
    yield return PropertyRow.Switch("Widescreen", _header.Widescreen, value => _header.Widescreen = value);
    yield return PropertyRow.Options("Region", _header.Region, ["NTSC", "PAL"], value => _header.Region = value);
}
```

Three things matter:

- Validate, and throw to refuse. The message you throw is what the user reads, and the pane redraws from the format, so a rejected edit visibly reverts. Validate where the field still is what the format can hold, not later at save time, when the user has no idea which of their edits was the bad one.
- Mark the archive dirty. `Changed` defaults to `AssetChange.Properties | AssetChange.Dirty`; set it where an edit also invalidates the tree, the viewport or the pixels. `IsDirty` on your archive is what `File ▸ Save` reads.
- They land in the undo history. The host records the row's old and new text and reverses an edit by handing the old value back to the same delegate, so `Apply` must accept any value it previously produced. A row whose old value the plugin would now reject is a row that cannot be undone.

`Describe` is called again after every accepted edit, so build the rows from the format each time rather than caching them.

**`ICommandSource`**: extra context-menu entries, described below.

## Recognizers

An `IAssetRecognizer` claims a payload by content, wherever it turns up. The host pools recognizers from every loaded plugin, so yours applies inside other people's archives too:

```csharp
public sealed class TimRecognizer : IAssetRecognizer
{
    public string Id => "yourgame.tim";
    public bool Claims(ReadOnlySpan<byte> data) => TimTexture.IsTim(data);
    public IAssetNode Wrap(IAssetNode node, IHostServices host) => new TimView(node);
}
```

`Wrap` returns a `RecognizedAsset`, which forwards name, path, and bytes to the node it wraps and lets you add the decoded behavior on top:

```csharp
internal sealed class TimView(IAssetNode inner) : RecognizedAsset(inner), IImageAsset
{
    public override string Kind => AssetKinds.Texture;
    public override string Extension => ".tim";
    public ImageData Image => field ??= TimTexture.Decode(Data);
    public bool CanReplaceImage => CanReplaceBytes;
    public void ReplaceImage(ImageData image) => ReplaceBytes(TimTexture.Encode(image));
}
```

Nodes that already carry content of their own are never wrapped, so a recognizer cannot shadow a format's own richer node. Recognizers also answer `IHostServices.IsKnownPayload`. Use that while walking a container to decide whether a blob is a standalone file to stop at, rather than another nested table to descend into.

`Claims` receives the node's head: the first `StreamingAssetNode.HeadWindow` (4 KB) bytes of a streaming node, or the whole payload when it is shorter or the node has no head of its own. Treat a span of exactly 4 KB as possibly truncated.

## Containers split across files

Some archives are not one file. A Virtuos archive is three: the data, the name table and the entry table, and none of them can be recognised alone, so `IArchiveFormat` only covers the case where the user picks one off disk. `IContainerRecognizer` covers the rest. The host offers it the rows of every folder in every open archive, once those rows have each been recognised on their own:

```csharp
public sealed class DwkContainerRecognizer : IContainerRecognizer
{
    public string Id => "virtuos.gda.container";

    public IReadOnlyList<IAssetNode>? Claim(IReadOnlyList<IAssetNode> children, IHostServices host)
    {
        // Return null to leave the folder alone, or the rows to show in place of the ones given.
        // Virtuos replaces the three rows of each triple with the archive row they make up.
    }
}
```

The rows you are handed are the ones the folder holds, so read their bytes through `IBinaryAsset` rather than reaching for a path: the folder may be inside somebody else's archive, and there may be no file to open. That is what makes the same recogniser work on a folder on disk and on `data/Publish.NDS` inside a DS ROM.

A container you open this way has no `Save`. Only the archive the host opened has one, and it knows nothing about you. Register a write-back into the rows you were given with `IHostServices.Defer` whenever you change:

```csharp
Action flush = () =>
{
    (byte[] data, byte[] table) = archive.Pack();
    gdaRow.ReplaceBytes(data);
    gdtRow.ReplaceBytes(table);
};
archive.Changed += () => host.Defer(archive, flush);
```

The host runs the flush once, on a worker thread, when **File ▸ Save** saves the outer archive, so the archive is rebuilt with your bytes inside. Until then it counts as unsaved; closing without saving discards it, and a flush that throws fails the save with nothing written. Undo keeps working: the host snapshots the node the user edited, and reversing that edit fires `Changed` and registers the flush again. On a host without deferral, `Defer` runs the flush at once. The DWSSK plugin does this for WADs inside disc images; Virtuos, the source of the sample, still writes back eagerly with `IBinaryAsset.ReplaceBytes` on every change.

## Commands

`ICommandSource` adds entries to a node's context menu. `Kind` decides whether the host prompts for a file first:

```csharp
public IEnumerable<AssetCommand> GetCommands()
{
    yield return new AssetCommand
    {
        Title = "Import Track from glTF…",
        Kind = AssetCommandKind.OpenFile,
        Filters = [new FileFilter("glTF 2.0", "*.glb", "*.gltf")],
        Execute = context =>
        {
            GltfDocument document = context.Host.Gltf.Read(context.Path!);
            Import(document);
            context.Log($"Imported {document.Meshes.Count} mesh(es).");
            context.Changed = AssetChange.Bytes | AssetChange.Scene | AssetChange.Dirty;
        },
    };
}
```

Four things matter here:

- Set `Changed`. It tells the host which views to refresh, and an edit reported as `AssetChange.None` is not recorded in the undo history at all.
- Throw on failure. The host catches, logs the message, and leaves the archive alone. Returning quietly makes a command that did nothing look like one that worked.
- `Execute` runs off the UI thread. Report progress through `context.Log`.
- `Role` decides where the entry lands. Left at `AssetCommandRole.Own` the command is its own menu entry, titled by `Title`. Set it to `Replace` or `Export` and the command instead becomes one of the file types the row's single `Replace…` or `Export…` entry offers; the host runs it when the file the user chose carries one of your `Filters` extensions, and `Title` is then only a label for the undo history. Use it for anything that rewrites the node from a file, so the user has one Replace command rather than three.

Extract, Extract Audio, Export (PNG, JPEG, BMP and glTF) and Replace (raw bytes and the same image formats) are built in. Extract Audio offers the clip's own format, and a movie's Export offers its soundtrack only in its real format, leaves it out for a silent movie, and writes exactly the path chosen in the dialog. These come from the capability interfaces. Add commands only for what is specific to your format. The undo limit is worth remembering: a snapshot covers the node the command ran on, which is all the host can see. A command that also rewrites a sibling node leaves that sibling as the command left it, and the host clears the history rather than offer an undo that would half-revert.

## Host services

`IHostServices` reaches you on `Open` and on every command context.

```csharp
ImageData png   = context.Host.Images.Load(path);
byte[] encoded  = context.Host.Images.Encode(image, ".bmp");
GltfDocument in = context.Host.Gltf.Read(path);
context.Host.Gltf.Write(scene, path);
context.Host.Log("something worth seeing in the Console pane");
```

`PluginDirectory` is where your DLL was loaded from. Read data files you ship alongside it from there, not from the working directory. The host loads your assembly from the bytes it hashed, not from the file, so `Assembly.Location` is empty for your plugin and every library embedded in it.

`host.Defer(key, flush)` postpones a nested container's write-back until the outer archive is saved; see Containers split across files.

## Failure and diagnostics

The host wraps every call into your plugin. A throw costs the user that one operation and never the session, and the message you throw is what they read, so make it say what was wrong with the file:

```csharp
throw new InvalidDataException(
    $"texture at 0x{offset:X} claims {size} bytes, past the end of the blob");
```

Validate on the way in, at the boundary where the offending data is still in hand. Every parse boundary in the in-tree plugins bounds its allocations against the real file length, because a length read out of a corrupt header is otherwise an arbitrary allocation request.

## Checklist

- [ ] Assembly named `*.Formats.dll`, references only `Omega.Plugins`
- [ ] `[assembly: OmegaPlugin(typeof(...))]` present
- [ ] `Info.Id` unique, `ContractVersion` left at its default
- [ ] `CanOpen` claims narrowly and never throws, and a container recogniser reads bytes through `IBinaryAsset` rather than paths
- [ ] `HasChildren` answers without parsing; `GetChildren` does the work
- [ ] `FileStem` overridden wherever `Name` carries decoration a filename should not
- [ ] `BackupPaths` lists every file `Save` overwrites
- [ ] `Save` serialises fully before writing
- [ ] Geometry is right-handed, Y-up (glTF's axes), and passes `SceneGeometry.Validate()`
- [ ] Editable rows validate their input, throw a message worth reading, and leave the archive dirty
- [ ] Commands set `Changed` and throw on failure, and anything that rewrites the node from a file sets `Role`
- [ ] Parse errors name the offset and say what was wrong
