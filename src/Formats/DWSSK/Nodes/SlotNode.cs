using System.Text;
using DWSSK.Formats.Core;
using DWSSK.Formats.Wad;
using Omega.Plugins;

namespace DWSSK.Formats.Nodes;

internal abstract class SlotNode : BinaryAssetNode, IDescribable, ICommandSource
{
    protected SlotNode(Slot slot, string name, string path, string kind = AssetKinds.Auto, string? detail = null)
        : base(name, path, kind, detail ?? Sizes.Describe(slot.Size))
    {
        Slot = slot;
    }

    public Slot Slot { get; }
    protected SlotGroup Group => Slot.Group;
    protected IHostServices Host => Slot.Group.Host;
    public override ReadOnlySpan<byte> Data => Slot.Data;
    public override int Size => Slot.Size;
    public override string Extension => Path.GetExtension(Name) is { Length: > 1 } e ? e : ".bin";
    public override string FileStem => Path.GetFileNameWithoutExtension(Name);
    public override bool CanReplaceBytes => Slot.CanWrite;

    protected override void Write(byte[] payload)
    {
        Check(payload);
        Slot.Replace(payload);
        Invalidate();
    }

    protected virtual void Check(byte[] payload)
    {
    }

    public void Validate(byte[] payload)
    {
        Check(payload);
    }

    protected virtual void Invalidate()
    {
    }

    protected void Store(byte[] payload)
    {
        ReplaceBytes(payload);
    }

    public virtual IEnumerable<PropertyRow> Describe()
    {
        return SlotRows();
    }

    protected IEnumerable<PropertyRow> SlotRows()
    {
        yield return PropertyRow.Of("File", Slot.File);
        if (Slot.PlatformKnown)
        {
            yield return PropertyRow.Of("Platform", Slot.Platform.Label());
        }
        yield return PropertyRow.Of("Size", Sizes.Describe(Slot.Data.Length));
        if (Slot is WadSlot w)
        {
            WadEntry e = w.Entry;
            yield return PropertyRow.Of("Container", w.Session.Name);
            yield return PropertyRow.Of("Source path", e.StoredPath is null ? e.Path : $"{e.Path} (stored cut to {e.StoredPath.Length} characters)");
            yield return PropertyRow.Of("Path hash", $"{e.Hash:X8}");
            yield return PropertyRow.Of("Entry type", $"{e.Type} ({TypeName(e.Type)})");
            yield return PropertyRow.Of("Entry index", w.Session.Archive.Entries.IndexOf(e));
            if (!w.Session.Writable)
            {
                yield return PropertyRow.Of("Writable", "no: the container it sits in cannot be written back");
            }
        }
    }

    public static string TypeName(int type)
    {
        return type switch
        {
            0 => "world",
            1 => "skydome",
            2 => "world collision",
            3 => "level database",
            5 => "asset",
            7 => "tie",
            8 => "instanced mesh",
            9 => "texture",
            10 => "shader",
            13 => "tuning",
            14 => "navigation",
            15 or 16 => "tuning / effect",
            WadArchive.AliasType => "alias",
            _ => "unknown",
        };
    }

    public virtual IEnumerable<AssetCommand> GetCommands()
    {
        return [];
    }

    protected AssetCommand ExportText(string title, string filterLabel, string pattern, string suggested, Func<string> text)
    {
        return new AssetCommand
        {
            Title = title,
            Kind = AssetCommandKind.SaveFile,
            Role = AssetCommandRole.Export,
            Filters = [new FileFilter(filterLabel, pattern)],
            SuggestedFileName = suggested,
            Execute = context =>
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text());
                AtomicSave.Write(context.Path!, bytes);
                context.Log($"Wrote {Path.GetFileName(context.Path!)} ({Sizes.Describe(bytes.Length)}) from {Slot.File}.");
            },
        };
    }

    protected AssetCommand ExportJson(Func<string> text)
    {
        return ExportText("Export JSON", "JSON document", "*.json", Name + ".json", text);
    }

    protected AssetCommand ImportJson(Func<JObj, byte[]> build, string what = "JSON")
    {
        return new AssetCommand
        {
            Title = $"Import {what}",
            Kind = AssetCommandKind.OpenFile,
            Role = AssetCommandRole.Replace,
            Filters = [new FileFilter("JSON document", "*.json")],
            Execute = context =>
            {
                object? doc = PyJson.Loads(File.ReadAllBytes(context.Path!));
                byte[] rebuilt = build(doc as JObj ?? throw new InvalidDataException("the JSON document is not an object"));
                bool same = rebuilt.AsSpan().SequenceEqual(Slot.Data);
                if (!same)
                {
                    Store(rebuilt);
                }
                context.Log(same
                    ? $"{Path.GetFileName(context.Path!)} rebuilds {Slot.File} byte for byte; nothing changed."
                    : $"Rebuilt {Slot.File} from {Path.GetFileName(context.Path!)} ({Sizes.Describe(rebuilt.Length)}). Use File ▸ Save to write it back.");
                context.Changed = same ? AssetChange.None : Everything;
            },
        };
    }

    public const AssetChange Everything = AssetChange.Bytes | AssetChange.Scene | AssetChange.Image
        | AssetChange.Children | AssetChange.Properties | AssetChange.Dirty;
}

internal sealed class RawNode(Slot slot, string name, string path)
    : SlotNode(slot, name, path, KindOf(slot))
{
    private static string KindOf(Slot slot)
    {
        return slot.Extension is ".txt" or ".csv" ? AssetKinds.Text : AssetKinds.Auto;
    }
}

internal sealed class AliasNode : SlotNode
{
    public AliasNode(WadSlot slot, string name, string path)
        : base(slot, name, path, AssetKinds.Unknown, DetailOf(slot))
    {
        Wad = slot;
    }

    private WadSlot Wad { get; }

    private static string DetailOf(WadSlot slot)
    {
        WadEntry e = slot.Entry;
        return e.AliasEntry >= 0 ? $"alias of {e.AliasFile}" : $"alias pointer 0x{e.AliasPointer ?? 0:X8} names no entry";
    }

    public override IEnumerable<PropertyRow> Describe()
    {
        WadEntry e = Wad.Entry;
        yield return PropertyRow.Of("Alias of", e.AliasEntry < 0 ? "no entry in this container"
            : e.AliasOffset is int legacy ? $"{e.AliasFile} (entry {e.AliasEntry}, +0x{legacy:X}, from an older manifest)"
            : $"{e.AliasFile} (entry {e.AliasEntry})");
        if (e.AliasPointer is { } p)
        {
            yield return PropertyRow.Of("Pointer", $"0x{p:X8}");
        }
        yield return PropertyRow.Of("Alias type", $"{e.AliasType} ({TypeName(e.AliasType ?? 0)})");
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
}
