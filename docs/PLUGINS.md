# Writing a format plugin

Omega Studio has no formats of its own. Everything it can open comes from a plugin -
a single .NET assembly named `<Name>.Formats.dll`, dropped into a `formats/` folder
beside the executable and discovered at startup.

**The shortest useful summary:** a plugin reads its container and describes what is
genuinely its own. The folder tree, the standard payloads inside it, the row colours,
the panes and the extract/export commands all belong to somebody else.

Three plugins ship with the app and are the best thing to read alongside this. In
order of size: [`Generic`](../src/Formats/Generic) recognises standard payloads and
opens no container at all; [`Kartz`](../src/Formats/Kartz) reads an archive stored as
a flat path list; [`Harugeki`](../src/Formats/Harugeki) reads a nested one and is the
only plugin here that does its own skinning.

---

## 1. The shape of a plugin

```
src/Formats/MyGame/
  MyGame.Formats.csproj     ← two lines and an import
  MyGamePlugin.cs           ← who you are, what you open
  MyGameArchive.cs          ← the format itself
  Nodes/…                   ← what the Outliner shows
```

The project file is almost empty, because `src/Plugin.props` does the rest:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OmegaPluginName>MyGame</OmegaPluginName>
  </PropertyGroup>
  <Import Project="../../Plugin.props" />
</Project>
```

That gives you an assembly called `MyGame.Formats.dll`, a reference to
`Omega.Plugins` (and nothing else), and a post-build copy into the `formats/` folder
beside the host. Add it to `src/OmegaStudio.slnx` and it builds with everything else.

**A plugin references exactly one assembly: `Omega.Plugins`.** No Avalonia, no
OpenGL, no image library, no glTF library, nothing from NuGet. Anything you need
from those, the host lends you - see [§7](#7-what-the-host-lends-you). That is
deliberate, and it is what keeps a plugin small enough to be worth writing.

### Registering it

One line, in any source file:

```csharp
[assembly: OmegaPlugin(typeof(MyGamePlugin))]
```

The host reads that attribute rather than scanning your assembly for a type that
implements an interface. Slower to write by one line; much easier to diagnose, since
a plugin that fails to load now fails for a reason that can be printed.

### The entry point

```csharp
public sealed class MyGamePlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id = "mygame",                          // unique across loaded plugins
        DisplayName = "My Game",
        Version = "1.0.0",
        Author = "you",
        Description = "Models and textures from My Game's .pak archives.",
    };

    public IEnumerable<IArchiveFormat> Archives { get; } = [new PakFormat()];
}
```

`Info` appears in the **Formats** menu. `Archives` lists the containers you can open;
each contributes its filter to the Open dialog, so your files become selectable the
moment the DLL is in place.

---

## 2. Opening a file

```csharp
public sealed class PakFormat : IArchiveFormat
{
    public string Id => "mygame.pak";
    public string DisplayName => "My Game archive (.pak)";
    public FileFilter Filter { get; } = new("My Game archive", "*.pak");

    public bool CanOpen(string path, ReadOnlySpan<byte> head) => head.StartsWith("PAK0"u8);

    public IArchive Open(string path, IHostServices host) => new PakArchive(path, host);
}
```

`CanOpen` is handed the first 4 KB of the file, already read, so a magic-number check
costs nothing. The host asks every loaded format in turn and the first to say yes
gets the file.

**Be honest in `CanOpen`.** A format that claims everything with a given extension
makes every other plugin using that extension unreachable. If your container has no
magic number, check its structure - the Harugeki plugin has no magic number at all
and validates its offset table against the file's length instead
([`HarugekiPlugin.cs`](../src/Formats/Harugeki/HarugekiPlugin.cs)).

`Open` returns an `IArchive`:

```csharp
public interface IArchive : IDisposable
{
    string DisplayName { get; }          // the root row's label
    IAssetNode Root { get; }             // the tree
    bool IsDirty { get; }                // are there unsaved edits?
    IReadOnlyList<string> BackupPaths { get; }
    void Save();
}
```

Throwing from `Open` is fine and expected for a malformed file: the host catches it,
shows your message, and leaves the workspace empty. Write the message for someone who
has never seen your format.

**You do not write the backup logic.** List in `BackupPaths` every file your `Save`
will overwrite, and the host copies each to `.bak` once per session - and only if no
backup is already there, so repeated saves can never overwrite the pristine copy with
an edited one. A format stored across several files (Kartz uses three) lists them all.

---

## 3. The tree

Every row is an `IAssetNode`. Derive from `AssetNode`, which carries the plumbing:

```csharp
internal sealed class FolderNode : AssetNode
{
    public FolderNode(string name, string path, List<Entry> entries)
        : base(name, path, AssetKinds.Container, $"{entries.Count} entries")
    {
        ChildFactory = () => entries.Select(e => new FileNode(e));
    }
}
```

- **`name`** - the row's label.
- **`path`** - a stable, filename-safe identifier, used to name extracted files.
- **`kind`** - picks the row's colour dot. Usually omit it; see
  [§4](#kinds-are-derived-so-usually-say-nothing).
- **`detail`** - the grey text after the label.
- **`ChildFactory`** - set it and the row gets an expander. It is called **once**, on
  first expand, which is what keeps a 133 MB archive instant to open. Do your parsing
  there, not in the constructor.

Throwing from `ChildFactory` is contained: the host shows the message as a single
child row and leaves the rest of the tree usable.

### If your archive is a flat list of paths

Do not build the folder tree yourself - `PathTree` does it, and copes with the noise
real archives carry (a leading `./`, backslashes, trailing slashes, doubled
separators):

```csharp
Root = PathTree.Build(
    archive.Entries,
    entry => entry.Path,                                   // where it lives
    (name, path, entry) => new FileNode(name, path, entry), // what a leaf is
    archive.Name);
```

### If your node owns bytes

Derive from `BinaryAssetNode` rather than implementing `IBinaryAsset`. Two members and
you are done:

```csharp
internal sealed class FileNode(Entry entry) : BinaryAssetNode(entry.Name, entry.Path)
{
    public override ReadOnlySpan<byte> Data => entry.Bytes;
    protected override void Write(byte[] payload) => entry.Replace(payload);
}
```

`Write` is also the hook for anything that has to happen when the bytes change -
re-parsing a decoded form, invalidating a cache.

---

## 4. Capabilities - the whole extension mechanism

A node on its own is just a label. What it can *do* is expressed by which optional
interfaces it also implements. The host tests each with `is` and lights up exactly the
panes and menu items those imply, hiding the rest.

| Implement | And you get, for free |
|---|---|
| `IBinaryAsset` | **Extract Raw…**, **Replace Raw…**, a size row in Properties |
| `ISceneAsset` | the 3D viewport, shading modes, camera, **Export glTF…** (and see `Lighting`/`Filtering` in [§6](#6-the-types-that-cross-the-seam) - the scene decides how it is lit and sampled) |
| `IImageAsset` | the Texture pane, **Export PNG…**, **Replace from PNG…** |
| `IAudioAsset` | the Audio pane - transport, seeking, duration, format readout - and **Extract Audio…** |
| `IAnimatedAsset` | the Animation pane: clip picker, play/stop, dope sheet, live posing |
| `IDescribable` | your own rows in the Properties pane |
| `ICommandSource` | extra context-menu entries, for format-specific work |

**Nothing needs registering, and no host code changes.** A node that implements
`IImageAsset` gets a texture viewer - with zoom, pan and nearest-neighbour magnifying -
and a PNG round-trip, without you writing any of it. A node that does not implement
`IAudioAsset` causes the Audio tab to be absent, which is how the Kartz plugin has no
audio UI while the Harugeki plugin does, with neither of them knowing the other exists.

The smallest genuinely useful node is `BinaryAssetNode`, which is already a working
archive browser with extract and replace.

### Kinds are derived, so usually say nothing

The row's colour dot comes from its `Kind`, and leaving it alone is normally right: the
host works it out from the capabilities. An `ISceneAsset` draws as a model, an
`IImageAsset` as a texture, a node with children as a container. Name a kind only for
something no capability implies - a bone, a material, an animation clip:

```csharp
: base(bone.Name, path, AssetKinds.Bone, $"{bone.Weights.Length} weights")
```

Any string works. One the palette has no colour for simply draws neutral, so inventing
`"Shader"` is fine; giving it a colour is one entry in `Themes/Palette.axaml`.

### Only add commands for things capabilities do not already give you

`ICommandSource` is for format-specific work with no generic equivalent - importing
geometry into a game's own model blob, say. Extract, replace, and the glTF and PNG
exports all come free; adding your own versions just puts two of each in the menu.

A command states what it needs and the host runs the file picker for you:

```csharp
public IEnumerable<AssetCommand> GetCommands()
{
    yield return new AssetCommand
    {
        Title = "Replace Geometry from glTF…",
        Kind = AssetCommandKind.OpenFile,
        Filter = new FileFilter("glTF model", "*.gltf", "*.glb"),
        Execute = context =>
        {
            GltfDocument gltf = context.Host.Gltf.Read(context.Path!);   // never null
            // … rewrite your blob …
            context.Log("Imported 4,213 triangles.");
            context.Changed = AssetChange.Bytes | AssetChange.Scene | AssetChange.Dirty;
        },
    };
}
```

`Execute` runs on a background thread, so it may block. `context.Path` is the file the
user picked, already null-checked - a cancelled dialog means `Execute` is never
called. Set `context.Changed` to tell the host what to refresh; anything you do not
name stays as it was, which is what keeps the lazy tree intact after an edit.

---

## 5. Standard payloads are not your problem

Your archive will contain OGG, WAV, PNG and JPEG files. **Do not write nodes for
them.** The `Generic` plugin recognises them by content, wherever they are found, and
attaches the capabilities they imply - so a WAV inside your archive gets a transport
and a PNG gets the texture pane, from a plugin that has never heard of your format.

This is what keeps a plugin small, and it is the reason both game plugins here have no
audio code at all.

### Contributing recognisers of your own

A plugin can add to that list instead of, or as well as, opening a container. Implement
`IAssetRecognizer` and return it from `IFormatPlugin.Recognizers`:

```csharp
public sealed class ShaderRecognizer : IAssetRecognizer
{
    public string Id => "mygame.shader";

    // Cheap: this is asked for every row while a folder is expanding.
    public bool Claims(ReadOnlySpan<byte> data) => data.StartsWith("SHDR"u8);

    public IAssetNode Wrap(IAssetNode node, IHostServices host) => new ShaderView(node);
}

internal sealed class ShaderView(IAssetNode inner) : RecognizedAsset(inner)
{
    public override string Kind => "Shader";
    public override string Extension => ".shdr";
}
```

`RecognizedAsset` forwards everything to the node it wraps, so the wrapped node keeps
its Properties rows and its own commands. The host only offers a node that holds bytes
and claims no content capability, so a format that has already decoded something is
never second-guessed.

> **One command-authoring note.** A command listed by a node that ends up wrapped
> receives the *wrapper* as `context.Node`. Capture what you need when you build the
> command rather than casting that back to your own type.

### Knowing when to stop walking

A container walk often has to stop at a payload rather than descend into it. Ask the
host rather than carrying your own copy of every magic number:

```csharp
if (host.IsKnownPayload(blob)) return;   // it is a clip or a picture, not a directory
```

It answers false when no recogniser is loaded, so your walk still has to be safe on its
own terms - treat it as a refinement, not a guarantee.

## 6. The types that cross the seam

All plain records, in [`src/Omega.Plugins`](../src/Omega.Plugins). The two worth
explaining:

### `SceneGeometry`

```csharp
new SceneGeometry
{
    Meshes = [ new SceneMesh {
        Name       = "body",
        Positions  = xyz,          // 3 floats per vertex
        Normals    = nrm,          // 3, or empty
        Uvs        = uv,           // 2, or empty - normalised 0..1
        Colors     = rgba,         // 4 bytes, or empty
        Indices    = null,         // ← see below
        Submeshes  = [ new SceneSubmesh("skin", first: 0, count: 900, textureIndex: 0) ],
    }],
    Textures  = [ ImageData.Create("skin", 256, 256, pixels) ],
    Lighting  = SceneLighting.Shaded,        // ← see below
    Filtering = TextureFiltering.Smooth,     // ← see below
}
```

`Indices` is what makes one type fit formats that have nothing in common. Leave it
**null** and the vertex arrays are read as a flat triangle list - which is exactly how
the Harugeki draw stream is already stored. Set it and the mesh is indexed - which is
exactly how a Kartz track is. A submesh's `First`/`Count` then index whichever of the
two is in play, and the host packs both through one path.

`SceneGeometry.Validate()` checks it all hangs together, and the host runs it before
anything reaches the GPU. A submesh running off the end of its arrays is a plugin bug
that would otherwise surface as a driver crash with nothing pointing at the cause.

Skinning is optional: fill `SceneGeometry.Joints` and a mesh's `Joints`/`Weights` and
your model exports as a rigged glTF.

#### `Lighting` - where the light comes from

Two answers, and picking the wrong one is the most visible mistake you can make here:

| | |
|---|---|
| `Shaded` | The viewport lights it from the vertex normals. Right for a model the game itself lit at run time. **The default.** |
| `VertexBaked` | `SceneMesh.Colors` *already contains* the lighting, so the viewport adds none of its own and simply modulates the texture by it. |

The viewport applies exactly one of them, never both - and that rule exists because
this codebase got it wrong. A DS track's vertex colours are a baked lightmap, and
shading them a second time darkened every course: worst on terrain, where surfaces sit
at grazing angles to the camera, so the ground went dark while the walls facing you
stayed bright. If your format bakes its lighting into vertex colours, say
`VertexBaked` and let them do the whole job.

#### `Filtering` - how the texels are sampled

`Smooth` interpolates (the default); `Nearest` does not. Choose `Nearest` for a format
whose textures are low-resolution and hand-placed - an older console's, typically -
where interpolation reads as smeared rather than smooth and seeing the actual texels is
usually the point.

It reaches both the viewport's sampler and the glTF exporter's, so a track exported and
opened in Blender looks as it does here.

### `ImageData`

RGBA8, top-down, no row padding, created through `ImageData.Create` so a buffer that
does not match its dimensions is caught at the boundary. The BGRA swizzle the UI needs
lives in the host; you never think about it.

> **One limitation to know.** PNG encoding premultiplies. Fully opaque pixels survive
> an export and re-import exactly; a pixel with alpha below 255 comes back with its
> colour scaled by that alpha, and the colour behind a fully transparent pixel is
> lost. This is why an importer must never identify a texture by comparing pixels
> alone - match on the material name first, as both shipped plugins do.

---

## 7. What the host lends you

`IHostServices`, handed to `Open` and to every command:

| | |
|---|---|
| `Log(string)` | a line in the console. Thread-safe. Tagged with your plugin's name automatically. |
| `Images` | PNG in and out, as `ImageData`. |
| `Gltf.Read(path)` | a glTF as a neutral `GltfDocument` - merged vertex arrays, joints, weights, materials with their images decoded. What almost every importer wants. |
| `Gltf.Open(path)` | the same file at the container level: parsed JSON, resolved buffers, accessor decoding. For the importer that needs the file *as authored* - unmerged vertex blocks, or `extras` it wrote itself. |
| `Gltf.Write(scene, path)` | export, `.gltf` or `.glb` by extension. |
| `IsKnownPayload(bytes)` | whether any loaded recogniser calls these bytes a standard payload. For a container walk that must stop at one. |
| `PluginDirectory` | your own folder, for data files you ship. |

If you find yourself wanting a dependency, check this list first: anything genuinely
generic belongs to the host, and adding a format should never mean re-solving a
problem another format already solved.

### Round-trip hints

If your format needs to preserve detail no exporter can re-derive, attach it to a
submesh:

```csharp
new SceneSubmesh("kartz_tex_3", 0, count, 3)
{
    Extras = new Dictionary<string, JsonNode> { ["kartzBlocks"] = runs },
}
```

The host writes these verbatim into the primitive's glTF `extras` and never looks
inside. The Kartz plugin uses this to record which runs of triangles the original file
shipped as strips - source information that cannot be recovered from geometry, and
without which an unmodified round trip could not rebuild a byte-identical file.

---

## 8. Building and installing

```bash
dotnet build src/OmegaStudio.slnx
```

The plugin lands in `src/OmegaStudio/bin/<Configuration>/net10.0/formats/`
automatically. To install one anywhere else, copy the DLL into the `formats/` folder
beside the executable:

```
formats/
  Generic.Formats.dll
  Harugeki.Formats.dll
  MyGame.Formats.dll     ← yours
```

Flat, so a plugin is one file you add or delete by hand and the folder is a readable
list of what is installed. A plugin that ships its own dependencies puts them in the
same folder, where its `.deps.json` still resolves them - the cost being that every
plugin shares one namespace of filenames.

The **Formats** menu lists what loaded, what failed and why, and where it looked.

### One dependency between plugins

`Generic.Formats.dll` is what gives *every* plugin its audio and image previews. Remove
it and those payloads fall back to raw blobs - every archive still opens, every format
still works, there is just nothing to play or look at. It is the one case where
deleting a plugin changes what another one can do, and it is deliberate: recognising a
PNG in one place beats every plugin recognising it separately.

### Contract versions

Your `FormatPluginInfo.ContractVersion` defaults to the version of `Omega.Plugins` you
compiled against, and the host refuses a plugin whose value does not match its own.
Leave the default alone. Without this check, the first symptom of a mismatch is a
`MissingMethodException` thrown from somewhere unrelated; with it, you get a sentence
naming both numbers.

Adding a new capability interface does **not** bump the version: an older plugin
simply does not implement it, and the host's `is` test comes back false, which is the
right answer.

---

## 9. Security - what this does and does not protect against

Stated plainly, because the honest answer is narrower than "plugins are sandboxed":

**.NET has no in-process sandbox.** A DLL in `formats/` runs with exactly the
privileges Omega Studio has. It can read and write any file you can, open sockets, and
start processes. Treat a format plugin exactly as you would any other program you
chose to run - because that is what it is.

What the loader **does** provide:

- The folder is **opt-in and yours**. Nothing is ever downloaded, fetched or installed
  automatically, and there is no plugin registry.
- **Dependency isolation.** Each plugin loads into its own `AssemblyLoadContext`, so
  two plugins may ship different versions of the same library without one silently
  getting the other's.
- **A shared contract.** `Omega.Plugins` always resolves from the host, so
  `IFormatPlugin` is one type on both sides of the boundary and not two identically
  named ones.
- **A version gate**, refusing a plugin built against a contract the host does not
  speak.
- **Exception containment.** Every call into a plugin is guarded. A plugin that throws
  loses you the one thing you were doing, never the session: an unreadable node shows
  an error row, a broken sniff means that format does not claim the file, a failed
  command logs and stops.
- **Traversal-safe filenames.** Names and paths a plugin supplies are sanitised before
  they reach a save dialog.

---

## 10. When something does not load

The **Formats** menu shows the reason for each failure. What they mean:

| Message | What to do |
|---|---|
| *it has no `[assembly: OmegaPlugin(typeof(...))]` attribute…* | Add the attribute. This is also what you see if a non-plugin DLL - a dependency, say - is sitting loose in `formats/` under a `*.Formats.dll` name. |
| *it was built against plugin contract version N, but this build speaks M* | Rebuild against the matching `Omega.Plugins`. |
| *another plugin already claims the id 'x'* | Two plugins share a `FormatPluginInfo.Id`. Change one. |
| *it is not a valid .NET assembly for this platform* | Wrong architecture, or the file is not an assembly. |
| *a dependency is missing (…)* | Ship it beside the plugin, in the `formats/<Name>/` folder form. |
| *'X' does not implement IFormatPlugin* | The attribute names the wrong type. |

If your archive opens but nothing appears, the file was claimed by a different
format's `CanOpen`. The console line says which one - formats are asked in load order,
and the first yes wins.

---

## 11. Checking your plugin works

There is no test project in this repository, so verification is what you do at the
keyboard. Three checks are worth making every time, in this order:

- **Byte-exact round trip.** Open an untouched archive, save it, and compare the file
  against a copy. This is the strongest check there is for a reverse-engineered
  container: it fails on anything your writer understands less well than your reader
  does. Do this before anything else - a format that cannot round-trip untouched
  cannot safely be edited.
- **Walk the whole tree.** Expand everything and watch the console. A blob that will
  not parse shows as an error row rather than taking the app down, so the console is
  where you find out.
- **Edit, save, re-open.** A splice that gets an offset wrong still writes a file;
  only re-opening catches it.

The **Formats** menu tells you whether your plugin loaded at all, and
[§10](#10-when-something-does-not-load) covers what each failure means.
