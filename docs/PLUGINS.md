# Writing a plugin

A plugin is a single .NET assembly that teaches Omega Studio one or more file formats.
It references `Omega.Plugins` and nothing else of the host's, so it can be built, shipped, and replaced without the application being rebuilt.
For how the host uses what you implement, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Rules the loader enforces

1.  The file is named `*.Formats.dll` and lives in the `formats` folder beside the executable.
2.  The assembly carries `[assembly: OmegaPlugin(typeof(YourPlugin))]`.
3.  That type implements `IFormatPlugin` and has a parameterless constructor.
4.  `Info.ContractVersion` matches the host's `PluginContract.Version`. It defaults to the value you compiled against, so leave it alone and rebuild when it changes.
5.  `Info.Id` is non-blank and not already claimed by another loaded plugin.

Break any of these and the plugin is skipped with the reason printed in the Console pane and listed under **Formats ▸ Failed to load**. The others still load.

## Project setup

Copy what the in-tree plugins do.
A `.csproj` under `src/Formats/` needs only:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OmegaPluginName>Yourgame</OmegaPluginName>
  </PropertyGroup>
  <Import Project="../../Plugin.props" />
</Project>
```

`Plugin.props` sets the assembly name to `Yourgame.Formats`, references `Omega.Plugins` with `Private="false"` so the contract is not copied beside your plugin, and copies the built DLL into the host's `formats` folder after every build.
Then add the project to `src/OmegaStudio.slnx`.
Building outside this tree works the same way: reference `Omega.Plugins.dll`, do not copy it to your output, and put your DLL in `formats`.

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

Both `Archives` and `Recognizers` default to empty, so implement only the one you need.
A plugin that just teaches the host to understand a payload format implements `Recognizers` alone, that is all the Generic plugin does.

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

`CanOpen` receives the first 4 KB and the path.
Answer from the head where you can; reach for the path when the format genuinely cannot be identified without it, such as needing to compare a declared size against the real file length, or checking for sibling files.
Return `false` rather than throwing when unsure.
A format that throws while sniffing is treated as saying no, and the file may well belong to a plugin that works.
Claim narrowly. Claiming every `.bin` makes the extension a monopoly and shuts out any other plugin that also uses it.
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

Serialize completely in memory first.
A rebuild that fails halfway through a write leaves a truncated file where the user's archive used to be.
List every file `Save` touches in `BackupPaths`.
The host copies each to `.bak` before the first save of a session.

## Nodes

Derive from `AssetNode` for a plain row, or `BinaryAssetNode` when the row owns bytes.
Children come from a factory so nothing is parsed until a row is expanded:

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

Keep `HasChildren` cheap. 
it is asked for every visible row, and answering it must not parse anything.
`BinaryAssetNode` handles the replace plumbing for you.
Implement `Data` and `Write`, and override `CanReplaceBytes` when the slot cannot take arbitrary bytes:

```csharp
internal sealed class EntryNode : BinaryAssetNode
{
    public override ReadOnlySpan<byte> Data => _entry.Span;
    public override string Extension => ".dat";
    protected override void Write(byte[] payload) => _entry.Replace(payload);
}
```

## Capabilities

Add an interface and the matching pane and commands appear.
Implement only what the node can honestly do.

**`IImageAsset`** - a decoded picture. `ImageData` is RGBA8, top-down, no row padding. Build it with `ImageData.Create`, which rejects a buffer that does not match the dimensions, so a mistake is named at your boundary rather than surfacing as a torn preview. The host owns the swizzle into whatever the UI toolkit wants; do not do it yourself.

**`IAudioAsset`** -the clip still encoded, exactly as stored. The host decodes it.

**`ISceneAsset`** - geometry as `SceneGeometry`. Positions set the vertex count and every other array must match it or be empty. Submeshes are runs of triangles sharing a material, and their `TextureIndex` points into the scene's own texture list, or `-1` for untextured.

Hand over a right-handed, **Z-up** space. Convert handedness once, at parse time, so the viewport, the exporter, and the skinning cannot disagree. Call `SceneGeometry.Validate()` yourself while developing. The host calls it before every upload and export, and its messages name the offending mesh.

**`IAnimatedAsset`** - clips the transport can play, plus `TryPose` to sample one. Sampling is yours: the host asks for a time in seconds and expects posed positions and normals back.

**`IDescribable`** - rows for the Properties pane.

```csharp
public IEnumerable<PropertyRow> Describe()
{
    yield return PropertyRow.Of("Dimensions", $"{Width}x{Height}");
    yield return PropertyRow.Of("Entries", _entries.Count);
}
```

**`ICommandSource`** - extra context-menu entries, described below.

## Recognizers

An `IAssetRecognizer` claims a payload by content, wherever it turns up.
The host pools recognizers from every loaded plugin, so yours applies inside other people's archives too:

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

Nodes that already carry content of their own are never wrapped, so a recognizer cannot shadow a format's own richer node.
Recognizers also answer `IHostServices.IsKnownPayload`.
Use that while walking a container to decide whether a blob is a standalone file to stop at, rather than another nested table to descend into.

## Commands

`ICommandSource` adds entries to a node's context menu.
`Kind` decides whether the host prompts for a file first:

```csharp
public IEnumerable<AssetCommand> GetCommands()
{
    yield return new AssetCommand
    {
        Title = "Import Track from glTF…",
        Kind = AssetCommandKind.OpenFile,
        Filter = new FileFilter("glTF 2.0", "*.glb", "*.gltf"),
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

Three things matter here:

-   **Set `Changed`.** It tells the host which views to refresh, and an edit reported as `AssetChange.None` is not recorded in the undo history at all.
-   **Throw on failure.** The host catches, logs the message, and leaves the archive alone. Returning quietly makes a command that did nothing look like one that worked.
-   **`Execute` runs off the UI thread.** Report progress through `context.Log`.

Extract Raw, Replace Raw, Export PNG, Replace from PNG, and Export glTF are built in; they come from the capability interfaces. Add commands only for what is specific to your format.
Note the undo limit: a snapshot covers the node the command ran on, which is all the host can see. A command that also rewrites a _sibling_ node leaves that sibling as the command left it, and the host clears the history rather than offer an undo that would half-revert.

## Host services

`IHostServices` reaches you on `Open` and on every command context.

```csharp
ImageData png   = context.Host.Images.Load(path);
byte[] encoded  = context.Host.Images.EncodePng(image);
GltfDocument in = context.Host.Gltf.Read(path);
context.Host.Gltf.Write(scene, path);
context.Host.Log("something worth seeing in the Console pane");
```

`PluginDirectory` is where your DLL was loaded from.
read data files you ship alongside it from there, not from the working directory.

## Failure and diagnostics

The host wraps every call into your plugin.
A throw costs the user that one operation and never the session, and the message you throw is what they read, so make it say what was wrong with the file:

```csharp
throw new InvalidDataException(
    $"texture at 0x{offset:X} claims {size} bytes, past the end of the blob");
```

Validate on the way in, at the boundary where the offending data is still in hand.
Every parse boundary in the in-tree plugins bounds its allocations against the real file length, because a length read out of a corrupt header is otherwise an arbitrary allocation request.

## Checklist

- [ ] Assembly named `*.Formats.dll`, references only `Omega.Plugins`
- [ ] `[assembly: OmegaPlugin(typeof(...))]` present
- [ ] `Info.Id` unique, `ContractVersion` left at its default
- [ ] `CanOpen` claims narrowly and never throws
- [ ] `HasChildren` answers without parsing; `GetChildren` does the work
- [ ] `BackupPaths` lists every file `Save` overwrites
- [ ] `Save` serialises fully before writing
- [ ] Geometry is right-handed, Z-up, and passes `SceneGeometry.Validate()`
- [ ] Commands set `Changed` and throw on failure
- [ ] Parse errors name the offset and say what was wrong