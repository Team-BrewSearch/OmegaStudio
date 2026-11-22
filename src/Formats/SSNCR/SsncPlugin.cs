using System.Linq;
using Omega.Plugins;
[assembly: OmegaPlugin(typeof(SSNCR.Formats.SsncPlugin))]
namespace SSNCR.Formats;

// ── plugin ───────────────────────────────────────────────────────────────────

public sealed class SsncPlugin : IFormatPlugin
{
    public FormatPluginInfo Info { get; } = new()
    {
        Id          = "ssncr",
        DisplayName = "Shrek Smash n' Crash Racing",
        Version     = "1.0.0",
        Author      = "Omega Studio",
        Description = "Packfile.dat container with textures, meshes, text assets and audio "
                    + "from the GameCube and PSP releases.",
    };
    public IEnumerable<IArchiveFormat>  Archives    { get; } = [new PackFormat()];
    public IEnumerable<IAssetRecognizer> Recognizers { get; } = [new PackRecognizer()];
}

// ── archive format ────────────────────────────────────────────────────────────

public sealed class PackFormat : IArchiveFormat
{
    public string Id          => "ssncr.pack";
    public string DisplayName => "SSNCR Packfile (.dat)";
    public FileFilter Filter  { get; } = new("SSNCR Packfile", "*.dat");

    public bool CanOpen(string path, ReadOnlySpan<byte> head)
        => PackFile.IsPack(head);

    public IArchive Open(string path, IHostServices host)
    {
        string full = Path.GetFullPath(path);
        (IReadOnlyList<PackEntry> entries, string platform) = PackFile.Read(full);
        host.Log($"Packfile.dat: {entries.Count} files, {platform}.");
        return new PackArchive(full, entries, platform);
    }
}

// ── archive ───────────────────────────────────────────────────────────────────

internal sealed class PackArchive : IArchive
{
    private readonly string _path;
    private readonly IReadOnlyList<PackEntry> _entries;
    private readonly string _platform;

    public PackArchive(string path, IReadOnlyList<PackEntry> entries, string platform)
    {
        _path     = path;
        _entries  = entries;
        _platform = platform;
        var textures = new TexLibrary(entries, e => PackFile.LoadEntry(path, e));

        Root = PathTree.Build(
            _entries,
            e => e.Path,
            (name, pathText, e) => PackEntryNode.Create(name, pathText, e, () => PackFile.LoadEntry(path, e), textures),
            Path.GetFileName(path),
            $"{entries.Count} files, {platform}");
    }

    public string DisplayName { get; } = "Packfile.dat";
    public IAssetNode Root    { get; }
    public bool IsDirty       => false;
    public IReadOnlyList<string> BackupPaths => [_path];

    public void Save()   => throw new InvalidOperationException("SSNCR Packfile is read-only.");
    public void Dispose() { }
}

// ── entry node ────────────────────────────────────────────────────────────────

internal class PackEntryNode : BinaryAssetNode, IDescribable, ICommandSource
{
    private readonly PackEntry    _entry;
    private readonly Func<byte[]> _load;
    private byte[]? _data;

    public static IAssetNode Create(string name, string pathText, PackEntry entry, Func<byte[]> load, TexLibrary textures)
        => entry.Path.EndsWith(".trm", StringComparison.OrdinalIgnoreCase) || entry.Path.EndsWith(".psp", StringComparison.OrdinalIgnoreCase)
            ? new TrmEntryNode(name, pathText, entry, load, textures)
            : new PackEntryNode(name, pathText, entry, load);

    protected PackEntryNode(string name, string pathText, PackEntry entry, Func<byte[]> load)
        : base(name, pathText, KindOf(entry.Path), Sizes.Describe(entry.StoredSize))
    {
        _entry = entry;
        _load  = load;
        string ext = Extension.ToLowerInvariant();
        if (ext == ".tex") ChildFactory = BuildTexChildren;
        if (ext == ".tpl") ChildFactory = BuildTplChildren;
        if (ext == ".fsb") ChildFactory = BuildFsbChildren;
    }

    private IEnumerable<IAssetNode> BuildTexChildren()
    {
        byte[] d = _data ??= _load();
        if (Ps2TexFile.IsPs2Tex(d))
            return Ps2TexFile.Read(d).Select(e => (IAssetNode)new Ps2TexEntryNode(e, d));
        if (PspTexFile.IsPspTex(d))
            return PspTexFile.Read(d).Select(e => (IAssetNode)new PspTexEntryNode(e, d));
        return TexFile.Read(d).Select(t => (IAssetNode)new TexTextureNode(t, d));
    }

    private IEnumerable<IAssetNode> BuildFsbChildren()
    {
        byte[] d = _data ??= _load();
        FsbInfo? fsb = FsbFile.TryRead(d);
        return fsb is null ? [] : fsb.Samples.Select(s => (IAssetNode)new FsbSampleNode(s));
    }

    private IEnumerable<IAssetNode> BuildTplChildren()
    {
        byte[] d = _data ??= _load();
        IReadOnlyList<TplTexture>? textures = TplFile.TryRead(d);
        return textures is null ? [] : textures.Select(t => (IAssetNode)new TplTextureNode(t, d));
    }

    public override ReadOnlySpan<byte> Data
    {
        get
        {
            _data ??= _load();
            return _data;
        }
    }

    public override int Size => _data?.Length ?? _entry.StoredSize;

    public override string Extension
        => Path.GetExtension(_entry.Path) is { Length: > 1 } e ? e : ".bin";

    public override string FileStem
        => Path.GetFileNameWithoutExtension(_entry.Path);

    public override bool CanReplaceBytes => false;
    protected override void Write(byte[] payload) =>
        throw new InvalidOperationException("SSNCR Packfile is read-only.");

    protected PackEntry Entry => _entry;

    public virtual IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Path",          _entry.Path);
        yield return PropertyRow.Of("Stored size",   Sizes.Describe(_entry.StoredSize));
        yield return PropertyRow.Of("Flags",         $"0x{_entry.Flags:X}  ({DescribeFlags(_entry.Flags)})");
        yield return PropertyRow.Of("Offset in PAK", $"0x{_entry.Offset:X}");
        if (_data is not null)
        {
            yield return PropertyRow.Of("Decoded size", Sizes.Describe(_data.Length));
            string ext = Extension.ToLowerInvariant();
            if (ext == ".col")
            {
                ColInfo? col = ColFile.TryRead(_data);
                if (col is not null)
                {
                    yield return PropertyRow.Of("Meshes",    col.NumMeshes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Vertices",  col.NumVerts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Triangles", col.NumTris.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else if (ext == ".wcol")
            {
                WcolInfo? wcol = WcolFile.TryRead(_data);
                if (wcol is not null)
                {
                    yield return PropertyRow.Of("Sectors",   wcol.NumSectors.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Vertices",  wcol.NumVerts.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Faces",     wcol.NumFaces.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Triangles", wcol.NumTris.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Bounds XZ", $"{wcol.MinX:F1} .. {wcol.MaxX:F1}  /  {wcol.MinZ:F1} .. {wcol.MaxZ:F1}");
                }
            }
            else if (ext == ".path")
            {
                PathInfo? path = PathFile.TryRead(_data);
                if (path is not null)
                {
                    yield return PropertyRow.Of("Waypoints", path.NumPoints.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Nodes",     path.NumNodes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else if (ext == ".tex" && Ps2TexFile.IsPs2Tex(_data))
            {
                IReadOnlyList<Ps2TexEntry> ps2 = Ps2TexFile.Read(_data);
                yield return PropertyRow.Of("PS2 textures", ps2.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (ext == ".tex" && PspTexFile.IsPspTex(_data))
            {
                yield return PropertyRow.Of("PSP textures", PspTexFile.Read(_data).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (ext == ".tpl" && TplFile.IsTpl(_data))
            {
                IReadOnlyList<TplTexture>? tpl = TplFile.TryRead(_data);
                if (tpl is not null)
                    yield return PropertyRow.Of("TPL textures", tpl.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (ext == ".fsb")
            {
                FsbInfo? fsb = FsbFile.TryRead(_data);
                if (fsb is not null)
                {
                    yield return PropertyRow.Of("FSB version",  fsb.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    yield return PropertyRow.Of("Sound tracks", fsb.NumSamples.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (FsbSample s in fsb.Samples.Take(8))
                    {
                        string dur = s.SampleRate > 0
                            ? $"{s.LengthSamples / (double)s.SampleRate:F2}s"
                            : $"{s.LengthSamples} smp";
                        yield return PropertyRow.Of(s.Name, $"{dur}  {s.Channels}ch  {s.SampleRate}Hz  {FsbFile.ModeName(s.Mode)}");
                    }
                }
            }
            else if (ext == ".thp")
            {
                ThpInfo? thp = ThpFile.TryRead(_data);
                if (thp is not null)
                {
                    yield return PropertyRow.Of("Video",    $"{thp.Width}×{thp.Height} @ {thp.Fps:F2} fps");
                    yield return PropertyRow.Of("Duration", $"{thp.NumFrames / thp.Fps:F2}s  ({thp.NumFrames} frames)");
                    if (thp.AudioChannels > 0)
                        yield return PropertyRow.Of("Audio", $"{thp.AudioChannels}ch  {thp.AudioRate}Hz");
                }
            }
            else if (ext == ".dat")
            {
                DatInfo? dat = DatFile.TryRead(_data);
                if (dat is not null)
                {
                    yield return PropertyRow.Of("Strings", dat.NumStrings.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    foreach (string s in dat.FirstStrings.Take(6))
                        yield return PropertyRow.Of("", $"\"{s}\"");
                }
            }
            else if (ext is ".at3" or ".wav")
            {
                RiffInfo? riff = RiffFile.TryRead(_data);
                if (riff is not null)
                {
                    string dur = riff.SampleRate > 0
                        ? $"{riff.NumSamples / (double)riff.SampleRate:F2}s"
                        : $"{riff.NumSamples} smp";
                    yield return PropertyRow.Of("Codec",    RiffFile.CodecName(riff.Codec));
                    yield return PropertyRow.Of("Audio",    $"{riff.Channels}ch  {riff.SampleRate}Hz  {dur}");
                }
            }
        }
    }

    public IEnumerable<AssetCommand> GetCommands()
    {
        yield return new AssetCommand
        {
            Title            = "Export file",
            Kind             = AssetCommandKind.SaveFile,
            Role             = AssetCommandRole.Export,
            Filters          = [new FileFilter("File", "*" + Extension)],
            SuggestedFileName = FileStem + Extension,
            Execute          = ctx =>
            {
                File.WriteAllBytes(ctx.Path!, Data.ToArray());
                ctx.Log($"Exported {_entry.Path}.");
            },
        };
    }

    private static string KindOf(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tex" or ".tpl" or ".dds" or ".png" => AssetKinds.Texture,
            ".trm" or ".psp"                     => AssetKinds.Mesh,
            ".col" or ".wcol"                    => AssetKinds.Unknown,
            ".ent" or ".txt" or ".sounddescriptor" or ".path" or ".uvanim" => AssetKinds.Text,
            ".fsb" or ".at3"                     => AssetKinds.Audio,
            _                                    => AssetKinds.Unknown,
        };
    }

    private static string DescribeFlags(int flags)
    {
        List<string> parts = [];
        if ((flags & 0x02) != 0) parts.Add("pak-compressed");
        if ((flags & 0x04) != 0) parts.Add("flag-2");
        if ((flags & 0x08) != 0) parts.Add("flag-3");
        if ((flags & 0x10) != 0) parts.Add("flag-4");
        int rest = flags & ~0x1F;
        if (rest != 0) parts.Add($"0x{rest:X}");
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }
}

// ── TEX texture node ──────────────────────────────────────────────────────────

internal sealed class TexTextureNode : BinaryAssetNode, IImageAsset, IDescribable
{
    private readonly TexTexture _tex;
    private readonly byte[]     _raw;
    private readonly byte[]     _pal;
    private ImageData? _image;

    public TexTextureNode(TexTexture tex, byte[] texFileData)
        : base(tex.Name, tex.Name, AssetKinds.Texture, Sizes.Describe(tex.DataSize))
    {
        _tex = tex;
        _raw = texFileData[tex.DataOffset..(tex.DataOffset + tex.DataSize)];
        _pal = tex.PaletteOffset >= 0
            ? texFileData[tex.PaletteOffset..(tex.PaletteOffset + tex.PaletteSize)]
            : [];
    }

    public override ReadOnlySpan<byte> Data    => _raw;
    public override int    Size                => _raw.Length;
    public override string Extension           => ".bin";
    public override string FileStem            => _tex.Name;
    public override bool   CanReplaceBytes     => false;
    protected override void Write(byte[] payload) => throw new NotSupportedException();

    public ImageData Image        => _image ??= Decode();
    public bool      CanReplaceImage => false;
    public void      ReplaceImage(ImageData image) => throw new NotSupportedException();

    private ImageData Decode() => Decode(_tex, _raw, _pal);

    public static ImageData Decode(TexTexture tex, byte[] texFileData)
        => Decode(tex, texFileData.AsSpan(tex.DataOffset, tex.DataSize),
            tex.PaletteOffset >= 0 ? texFileData.AsSpan(tex.PaletteOffset, tex.PaletteSize) : []);

    private static ImageData Decode(TexTexture tex, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> pal)
    {
        byte[] rgba = GcnTex.Decode(raw, tex.Width, tex.Height, (uint)tex.GcnFmt, pal, (uint)tex.PaletteFormat);
        return ImageData.Create(tex.Name, tex.Width, tex.Height, rgba, FmtName(tex.GcnFmt));
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name",   _tex.Name);
        yield return PropertyRow.Of("Size",   $"{_tex.Width} × {_tex.Height}");
        yield return PropertyRow.Of("Format", FmtName(_tex.GcnFmt));
        yield return PropertyRow.Of("Data",   Sizes.Describe(_raw.Length));
    }

    private static string FmtName(int f) => f switch
    {
        14 => "GX_TF_CMPR (DXT1-equivalent)",
        8  => "GX_TF_CI4 (4bpp palettized)",
        9  => "GX_TF_CI8 (8bpp palettized)",
        _  => $"unknown (0x{f:X})",
    };
}

// ── PS2 .tex entry node ───────────────────────────────────────────────────────

internal sealed class Ps2TexEntryNode : BinaryAssetNode, IImageAsset, IDescribable
{
    private readonly Ps2TexEntry _entry;
    private readonly byte[]      _file;
    private ImageData? _image;

    public Ps2TexEntryNode(Ps2TexEntry entry, byte[] texFileData)
        : base(entry.Name, entry.Name, AssetKinds.Texture, $"{entry.Width}×{entry.Height}")
    {
        _entry = entry;
        _file = texFileData;
    }

    public override ReadOnlySpan<byte> Data    => _file.AsSpan(_entry.PixelOffset, _entry.PixelSize);
    public override int    Size                => _entry.PixelSize;
    public override string Extension           => ".bin";
    public override string FileStem            => _entry.Name;
    public override bool   CanReplaceBytes     => false;
    protected override void Write(byte[] payload) => throw new NotSupportedException();

    public ImageData Image => _image ??= Decode(_entry, _file);
    public bool CanReplaceImage => false;
    public void ReplaceImage(ImageData image) => throw new NotSupportedException();

    public static ImageData Decode(Ps2TexEntry e, byte[] file)
        => ImageData.Create(e.Name, e.Width, e.Height, Ps2TexFile.Decode(e, file), Ps2TexFile.FormatName(e));

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name",   _entry.Name);
        yield return PropertyRow.Of("Size",   $"{_entry.Width} × {_entry.Height}");
        yield return PropertyRow.Of("Format", Ps2TexFile.FormatName(_entry));
        yield return PropertyRow.Of("Raster", $"0x{_entry.RasterFormat:X}, depth {_entry.Depth}");
        yield return PropertyRow.Of("Data",   Sizes.Describe(_entry.PixelSize));
    }
}

// ── PSP .tex entry node ───────────────────────────────────────────────────────

internal sealed class PspTexEntryNode : BinaryAssetNode, IImageAsset, IDescribable
{
    private readonly PspTexEntry _entry;
    private readonly byte[] _file;
    private ImageData? _image;

    public PspTexEntryNode(PspTexEntry entry, byte[] file)
        : base(entry.Name, entry.Name, AssetKinds.Texture, $"{entry.Width}×{entry.Height}")
    {
        _entry = entry;
        _file = file;
    }

    public override ReadOnlySpan<byte> Data    => [];
    public override int    Size                => 0;
    public override string Extension           => ".bin";
    public override string FileStem            => _entry.Name;
    public override bool   CanReplaceBytes     => false;
    protected override void Write(byte[] payload) => throw new NotSupportedException();

    public ImageData Image => _image ??= Decode(_entry, _file);
    public bool CanReplaceImage => false;
    public void ReplaceImage(ImageData image) => throw new NotSupportedException();

    public static ImageData Decode(PspTexEntry e, byte[] file)
        => ImageData.Create(e.Name, e.Width, e.Height, PspTexFile.Decode(e, file), PspTexFile.FormatName(e.Psm));

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name",     _entry.Name);
        yield return PropertyRow.Of("Size",     $"{_entry.Width} × {_entry.Height}");
        yield return PropertyRow.Of("Format",   PspTexFile.FormatName(_entry.Psm));
        yield return PropertyRow.Of("Swizzled", _entry.Swizzled ? "yes" : "no");
    }
}

// ── TPL texture node ──────────────────────────────────────────────────────────

internal sealed class TplTextureNode : BinaryAssetNode, IImageAsset, IDescribable
{
    private readonly TplTexture _tex;
    private readonly byte[]     _raw;
    private readonly byte[]     _tplFile;
    private ImageData? _image;

    public TplTextureNode(TplTexture tex, byte[] tplFile)
        : base($"Texture {tex.Index}", $"Texture {tex.Index}", AssetKinds.Texture,
               Sizes.Describe(GcnTex.DataSize(tex.GxFormat, tex.Width, tex.Height)))
    {
        _tex     = tex;
        _tplFile = tplFile;
        int dataSize = GcnTex.DataSize(tex.GxFormat, tex.Width, tex.Height);
        int end = Math.Min(tex.DataOffset + dataSize, tplFile.Length);
        _raw = tplFile[tex.DataOffset..end];
    }

    public override ReadOnlySpan<byte> Data    => _raw;
    public override int    Size                => _raw.Length;
    public override string Extension           => ".bin";
    public override string FileStem            => $"tpl_tex_{_tex.Index}";
    public override bool   CanReplaceBytes     => false;
    protected override void Write(byte[] payload) => throw new NotSupportedException();

    public ImageData Image        => _image ??= Decode();
    public bool      CanReplaceImage => false;
    public void      ReplaceImage(ImageData image) => throw new NotSupportedException();

    private ImageData Decode()
    {
        string label = $"TPL #{_tex.Index}";
        byte[] pal = [];
        uint palFmt = 1;
        if (_tex.PalHdrOffset > 0)
            (pal, palFmt) = TplFile.ReadPalette(_tplFile, _tex.PalHdrOffset);
        byte[] rgba = GcnTex.Decode(_raw, _tex.Width, _tex.Height, _tex.GxFormat, pal, palFmt);
        return ImageData.Create(label, _tex.Width, _tex.Height, rgba, TplFile.FormatName(_tex.GxFormat));
    }

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Index",  _tex.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return PropertyRow.Of("Size",   $"{_tex.Width} × {_tex.Height}");
        yield return PropertyRow.Of("Format", TplFile.FormatName(_tex.GxFormat));
        yield return PropertyRow.Of("Data",   Sizes.Describe(_raw.Length));
    }
}

// ── FSB sample node ────────────────────────────────────────────────────────────

internal sealed class FsbSampleNode : BinaryAssetNode, IDescribable
{
    private readonly FsbSample _sample;

    public FsbSampleNode(FsbSample sample)
        : base(sample.Name, sample.Name, AssetKinds.Audio,
               sample.SampleRate > 0
                   ? $"{sample.LengthSamples / (double)sample.SampleRate:F2}s"
                   : $"{sample.LengthSamples} smp")
    {
        _sample = sample;
    }

    public override ReadOnlySpan<byte> Data    => [];
    public override int    Size                => 0;
    public override string Extension           => ".bin";
    public override string FileStem            => _sample.Name;
    public override bool   CanReplaceBytes     => false;
    protected override void Write(byte[] payload) => throw new NotSupportedException();

    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Name",     _sample.Name);
        yield return PropertyRow.Of("Encoding", FsbFile.ModeName(_sample.Mode));
        yield return PropertyRow.Of("Channels", _sample.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return PropertyRow.Of("Rate",     $"{_sample.SampleRate} Hz");
        string dur = _sample.SampleRate > 0
            ? $"{_sample.LengthSamples / (double)_sample.SampleRate:F2}s  ({_sample.LengthSamples} samples)"
            : $"{_sample.LengthSamples} samples";
        yield return PropertyRow.Of("Duration", dur);
    }
}

// ── PAK recognizer (auto-detects embedded Packfile.dat inside disc images) ─────

internal sealed class PackRecognizer : IAssetRecognizer
{
    public string Id => "ssncr.pack";
    public bool Claims(ReadOnlySpan<byte> data) => PackFile.IsPack(data);
    public IAssetNode Wrap(IAssetNode node, IHostServices host)
    {
        byte[] packData = (node as IBinaryAsset)?.Data.ToArray() ?? [];
        (IReadOnlyList<PackEntry> entries, string platform) = PackFile.ReadFromBytes(packData);
        host.Log($"Packfile.dat (embedded): {entries.Count} files, {platform}.");
        return new PackRecognizedNode(node, packData, entries);
    }
}

internal sealed class PackRecognizedNode : RecognizedAsset
{
    private readonly Lazy<IEnumerable<IAssetNode>> _children;

    public PackRecognizedNode(IAssetNode inner, byte[] packData, IReadOnlyList<PackEntry> entries)
        : base(inner)
    {
        _children = new Lazy<IEnumerable<IAssetNode>>(() =>
        {
            var textures = new TexLibrary(entries, e => PackFile.LoadEntryFromBytes(packData, e));
            IAssetNode root = PathTree.Build(
                entries,
                e => e.Path,
                (name, pathText, e) => PackEntryNode.Create(name, pathText, e, () => PackFile.LoadEntryFromBytes(packData, e), textures),
                inner.Name,
                $"{entries.Count} files");
            return root.GetChildren();
        });
    }

    public override bool HasChildren => true;
    public override string Kind => AssetKinds.Container;
    public override IEnumerable<IAssetNode> GetChildren() => _children.Value;
}

// ── TRM model node ────────────────────────────────────────────────────────────

internal sealed class TrmEntryNode(string name, string pathText, PackEntry entry, Func<byte[]> load, TexLibrary textures)
    : PackEntryNode(name, pathText, entry, load), ISceneAsset, IAnimatedAsset
{
    private TrmModel? _model;
    private SceneGeometry? _scene;
    private List<TrmPlacement> _placements = [];

    private TrmModel? Model()
    {
        if (_model is not null) return _model;
        byte[] d = Data.ToArray();
        if (PspChunks.IsModel(d)) return _model = TrmFile.ReadPsp(d);
        return TrmFile.IsTrm(d) ? _model = TrmFile.Read(d) : null;
    }

    public SceneGeometry? GetScene()
    {
        if (_scene is not null) return _scene;
        TrmModel? m = Model();
        if (m is null || m.Atomics.Count == 0) return null;
        _scene = TrmScene.Build(m, names => textures.Resolve(names, Entry.Path), out List<TrmPlacement> placed);
        _placements = placed;
        return _scene;
    }

    public IReadOnlyList<AnimationClip> Clips => GetScene()?.Clips ?? [];

    public bool TryPose(AnimationClip clip, double seconds, out float[] positions, out float[] normals)
    {
        positions = []; normals = [];
        SceneGeometry? scene = GetScene();
        TrmModel? m = _model;
        if (scene is null || m is null) return false;
        int index = scene.Clips.ToList().IndexOf(clip);
        if (index < 0 || index >= m.Animations.Count) return false;
        TrmAnimation anim = m.Animations[index];
        float t = anim.Duration > 0 ? (float)(seconds % anim.Duration) : 0f;
        TrmScene.Pose(m, scene, _placements, anim, t, out positions, out normals);
        return true;
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        foreach (PropertyRow row in base.Describe()) yield return row;
        TrmModel? m;
        string? error = null;
        try { m = Model(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { m = null; error = ex.Message; }
        if (error is not null) { yield return PropertyRow.Of("Model", error); yield break; }
        if (m is null) yield break;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        yield return PropertyRow.Of("Model name", m.Name);
        yield return PropertyRow.Of("Byte order", m.BigEndian ? "big-endian (GameCube)" : "little-endian");
        yield return PropertyRow.Of("Frames",     m.Frames.Count.ToString(ci));
        yield return PropertyRow.Of("Atomics",    m.Atomics.Count.ToString(ci));
        yield return PropertyRow.Of("Instances",  m.Instances.Count.ToString(ci));
        yield return PropertyRow.Of("Skinned",    m.Atomics.Count(a => a.Skin is not null).ToString(ci));
        if (m.InstanceNames.Count > 0) yield return PropertyRow.Of("Named instances", string.Join(", ", m.InstanceNames.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:X8}")));
        yield return PropertyRow.Of("Fade from instance", m.FadeFrom.ToString(ci));
        int coloured = m.Instances.Count(x => x.Tint != 0);
        if (coloured > 0) yield return PropertyRow.Of("Instance colours", $"{coloured} of {m.Instances.Count} set (ARGB; 0 means default 0xFEFFFFFF)");
        yield return PropertyRow.Of("Animations", $"{m.NumAnimations} ({m.NumTracks} tracks, {m.NumKeys} keys, {Sizes.Describe(m.AnimationBytes)})");
        string tex = string.Join(", ", m.Atomics.SelectMany(a => a.Materials).Select(x => x.Texture).OfType<string>().Distinct().Take(12));
        if (tex.Length > 0) yield return PropertyRow.Of("Textures", tex);
    }
}
