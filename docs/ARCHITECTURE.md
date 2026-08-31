# Architecture

## Why this shape

Omega Studio began as two applications: an editor for *Suzumiya Haruhi no Gekitou*
and a fork of it for *DreamWorks Super Star Kartz*. Comparing them file by file is
what produced this design.

The **format layers** had barely diverged - 11 of 15 shared files were byte-identical
after a namespace rename. The **frontends** had diverged everywhere they touched a
format: the tree builder, the property inspector, the view models, the vertex packer,
the image codec, the window. And yet the two 3D viewports, 733 and 738 lines, differed
almost entirely in one thing: `StyledProperty<RingModel?>` against
`StyledProperty<SskTrack?>`, and which overload of `MeshBuffer.Build` they called. The
vertex layout, the shaders, the camera and the upload path were identical, down to the
36-byte stride.

That is the whole argument. The frontend was never Harugeki-specific or
Kartz-specific; it was generic, and had been copy-pasted twice because there was no
seam to hang a format on. Forking it a third time for a third game is the failure mode
this exists to prevent.

## The layers

Strictly one-directional. Each knows only the layer above it.

```
src/Omega.Plugins/     the contract. Plain .NET: no Avalonia, no UI, no game knowledge.
src/OmegaStudio/       the Avalonia host. Knows the contract; knows no format.
src/Formats/Generic/   recognisers for standard payloads. Opens no container.
src/Formats/<Name>/    a format plugin. Knows the contract; knows one game.
```

A plugin references `Omega.Plugins` and nothing else - not the host, not Avalonia, not
OpenGL, not a glTF library. Anything it needs from those arrives as a service on
`IHostServices`. That constraint is load-bearing: it is why a plugin is small enough
to be worth writing, and it is what stops a format from quietly acquiring an opinion
about the UI.

## The seam

The host walks a tree of `IAssetNode`. What a node *can do* is expressed by which
optional interfaces it implements, and the host lights up exactly the UI those imply.
`IBinaryAsset` earns extract and replace; `ISceneAsset` earns the viewport and glTF
export; `IImageAsset` earns the texture pane and PNG round-trip; `IAudioAsset` earns
the transport; `IAnimatedAsset` earns the dope sheet. Panes and menu items with no
capability behind them hide themselves.

This is the only extension mechanism, and there is no registration step. It is also
why the Kartz plugin has no Audio tab and the Harugeki plugin does, with neither
plugin - nor the host - containing a line of code about the difference.

`docs/PLUGINS.md` is the working guide to all of it.

## The generic layer

A plugin should not have to know what an OGG file looks like. `Generic.Formats`
contributes `IAssetRecognizer`s rather than an archive format: the host offers it every
leaf that holds bytes and claims no content capability, and it hands back a view
wearing the capabilities the content implies. So a WAV gets a transport and a PNG gets
the texture pane in *every* archive, from a plugin that has heard of none of them.

Three rules make that safe. A node that already claims a content capability is skipped,
because the plugin knows its own format better than a magic number does. Recognition is
magic-number only, because it runs for every row as a folder expands. And
`RecognizedAsset` forwards `IDescribable` and `ICommandSource`, so a wrapped node keeps
its Properties rows and its plugin's own commands.

The one cost is a dependency between plugins: remove `Generic.Formats.dll` and every
other plugin loses audio and image previews. It degrades to raw blobs rather than
failing, and it is the price of recognising a PNG once instead of per format.

Because `Generic` also answers `IHostServices.IsKnownPayload`, a container walk that
must stop at a payload can ask rather than carrying its own copy of the magic numbers -
which is how the Harugeki walk still refuses to descend into an OGG despite no longer
knowing what one is.

## What crosses the seam, and why those types

| Type | Why it is shaped this way |
|---|---|
| `SceneGeometry` / `SceneMesh` | `Indices` is nullable. Null means the vertex arrays are already an expanded triangle list, which is exactly the Harugeki draw stream; non-null means indexed, which is exactly a Kartz track. One packer serves both, replacing two near-identical copies of the same walk. |
| `ImageData` | RGBA8, validated at construction. The BGRA swizzle both formats used to perform lives in the host now, so it exists once. |
| `AnimationClip` | The host owns the clock, the transport and the dope sheet, because those are the same for every game. The plugin owns `TryPose`, because how a vertex follows a bone is not. |
| `AssetCommand` | The host's own built-ins are expressed in the same type a plugin returns, so the context menu has one shape and one execution path. |
| `SceneSubmesh.Extras` | An escape hatch for source detail no exporter can re-derive. Written verbatim into glTF `extras`; the host never looks inside. It is what keeps a byte-exact track reconversion possible. |
| `BinaryAssetNode`, `PathTree` | Not contract types but shared implementations, in the SDK because two plugins had each written them. A format that stores a path list now writes no tree code at all. |

## Things that are deliberate

**The plugin loader shares one assembly on purpose.** Each plugin gets its own
`AssemblyLoadContext` so dependencies stay isolated, but `Omega.Plugins` always
resolves from the host. If a plugin loaded its own copy, its `IFormatPlugin` would be a
different type with an identical name, and every cast across the boundary would fail
while the error message looked correct.

**Every call into a plugin is guarded.** A plugin is third-party code parsing files it
has reverse engineered, so it will eventually throw. The rule is that this costs the
user the one thing they were doing and never the session.

**A node's kind is derived, not declared.** `AssetKinds.Resolve` reads it off the
capabilities - an `ISceneAsset` is a model, an `IImageAsset` a texture - so a plugin
only names a kind for something no capability implies. The brush key is derived in turn
(`Model` → `AssetModelBrush`), so adding a kind is a constant and a colour rather than
three edits in three files.

**Two panes are asked of what is on screen, not of the selection.** `HasScene` reads
the viewport, not the selected node - so clicking a bone under a model changes the
Properties pane and the context menu and leaves the model where it is.

**A scene says where its lighting comes from.** The two viewports this replaced had
quietly different fragment shaders: one multiplied by vertex colour and one did not.
Unifying them needed that stated rather than implied, and stating it exposed a real
bug - the merged shader did *both*, shading a DS track whose vertex colours are
already a baked lightmap, which darkened every course. `SceneGeometry.Lighting` now
picks one or the other and the shader can never apply both.

**Texture filtering is the scene's call too.** `SceneGeometry.Filtering` reaches the
viewport's sampler and the glTF export's, so a DS track's hand-placed texels stay
crisp in both rather than being interpolated into mush.

## Where to look

| | |
|---|---|
| The contract | [`src/Omega.Plugins`](../src/Omega.Plugins) - the assembly is the reference, and the compiler enforces it: CS1591 is an error there, so every public member carries a comment. |
| Plugin loading | [`src/OmegaStudio/Plugins`](../src/OmegaStudio/Plugins) |
| Capability-driven UI | [`MainViewModel.cs`](../src/OmegaStudio/ViewModels/MainViewModel.cs), [`NodeCommands.cs`](../src/OmegaStudio/ViewModels/NodeCommands.cs), [`MainWindow.axaml`](../src/OmegaStudio/Views/MainWindow.axaml) |
| The generic layer | [`src/Formats/Generic`](../src/Formats/Generic), applied by [`AssetRecognition.cs`](../src/OmegaStudio/Plugins/AssetRecognition.cs) |
| The game formats | [`docs/formats/harugeki.md`](formats/harugeki.md). The Kartz formats are documented in the source instead — `SskTrack`, `SskTexture` and `TrkWriter` carry the measurements. |
| Writing your own | [PLUGINS.md](PLUGINS.md) |
