using System.Globalization;
using System.Numerics;
using DWSSK.Formats.Core;
using DWSSK.Formats.Data;
using DWSSK.Formats.Shaders;
using Omega.Plugins;
namespace DWSSK.Formats.Nodes;
internal sealed record DocCodec(
    string Label,
    Func<byte[], JObj> Read,
    Func<JObj, byte[]> Write,
    Func<JObj, string> Dumps,
    Func<JObj, string>? Obj = null)
{
    private static string Text(JObj doc)
    {
        return PyJson.Dumps(doc, 1, false);
    }
    private static string Ascii(JObj doc)
    {
        return PyJson.Dumps(doc, 1);
    }
    public static readonly DocCodec P2b = new("P2B data table", Data.P2b.Read, Data.P2b.Write, Text);
    public static readonly DocCodec Lcl = new("localised strings", Data.Lcl.Read, Data.Lcl.Write, Text);
    public static readonly DocCodec Fkd = new("font kerning", Data.Fkd.Read, Data.Fkd.Write, Text);
    public static readonly DocCodec Cam = new("camera settings", Data.Cam.Read, Data.Cam.Write, Text);
    public static readonly DocCodec Csd = new("collision surfaces", Data.Csd.Read, Data.Csd.Write, Text);
    public static readonly DocCodec Nav = new("navigation mesh", Data.Nav.Read, Data.Nav.Write, Text, Data.Nav.ToObj);
    public static readonly DocCodec Col = new("collision mesh", Data.Col.Read, Data.Col.Write, Text, Data.Col.ToObj);
    public static readonly DocCodec Shader = new("shader", Shaders.Shader.Describe, Shaders.Shader.Build, Ascii);
    public static readonly DocCodec Nog = new("level name table", Data.Nog.Read, Data.Nog.Write, Ascii);
    public static readonly DocCodec Bog = new("level database", Data.Bog.Read, Data.Bog.Write, Data.Bog.Dumps);
    public static DocCodec? Of(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.EndsWith(".col.piece", StringComparison.Ordinal) ? Col : Path.GetExtension(lower) switch
        {
            ".p2b" => P2b,
            ".lcl" => Lcl,
            ".fkd" => Fkd,
            ".cam" => Cam,
            ".csd" => Csd,
            ".nav" => Nav,
            ".col" => Col,
            ".vs_bin" or ".ps_bin" => Shader,
            ".nog" => Nog,
            ".bog" => Bog,
            _ => null,
        };
    }
}
internal class DocNode : SlotNode
{
    private (int Revision, JObj Doc)? _doc;
    public DocNode(Slot slot, string name, string path, DocCodec codec)
        : base(slot, name, path, codec.Obj is null ? AssetKinds.Text : AssetKinds.Model)
    {
        Codec = codec;
    }
    protected DocCodec Codec { get; }
    protected JObj Doc
    {
        get
        {
            if (_doc is not { } cached || cached.Revision != Slot.Revision)
            {
                JObj doc = Codec.Read(Slot.Data);
                if (ReferenceEquals(Codec, DocCodec.Bog))
                {
                    Bog.AddNames(doc, Group.LevelNames());
                }
                _doc = cached = (Slot.Revision, doc);
            }
            return cached.Doc;
        }
    }
    protected override void Check(byte[] payload)
    {
        _ = Codec.Read(payload);
    }
    protected override void Invalidate()
    {
        _doc = null;
    }
    public override IEnumerable<PropertyRow> Describe()
    {
        JObj doc = Doc;
        yield return PropertyRow.Of("Format", Codec.Label);
        if (ReferenceEquals(Codec, DocCodec.Shader))
        {
            yield return PropertyRow.Of("Shader format", Shaders.Shader.Format(Slot.Data));
        }
        foreach (KeyValuePair<string, object?> field in doc)
        {
            if (Summary(field.Value) is { } text)
            {
                yield return PropertyRow.Of(Title(field.Key), text);
            }
        }
        foreach (PropertyRow row in Extra(doc))
        {
            yield return row;
        }
        foreach (PropertyRow row in SlotRows())
        {
            yield return row;
        }
    }
    protected virtual IEnumerable<PropertyRow> Extra(JObj doc)
    {
        return [];
    }
    private static string Title(string key)
    {
        string spaced = key.Replace('_', ' ');
        return spaced.Length == 0 ? key : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
    private static string? Summary(object? value)
    {
        return value switch
        {
            null => "none",
            string s => s.Length > 120 ? s[..120] + "…" : s,
            bool b => b ? "yes" : "no",
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => Py.Repr(d),
            List<object?> list => list.Count == 0 ? "empty"
                : list.All(x => x is long or double) && list.Count <= 16 ? string.Join(' ', list.Select(x => x is double d ? Py.Repr(d) : Convert.ToString(x, CultureInfo.InvariantCulture)))
                : $"{list.Count} item{(list.Count == 1 ? "" : "s")}",
            JObj o => $"{o.Count} field{(o.Count == 1 ? "" : "s")}",
            _ => null,
        };
    }
    public override IEnumerable<AssetCommand> GetCommands()
    {
        yield return ExportJson(() => Codec.Dumps(Doc));
        if (Codec.Obj is { } obj)
        {
            yield return ExportText("Export OBJ", "Wavefront OBJ", "*.obj", Name + ".obj", () => obj(Doc));
        }
        if (ReferenceEquals(Codec, DocCodec.Bog))
        {
            yield return ExportText("Export placements glTF", "glTF scene of placements", "*.gltf", Name + ".gltf", () => PyJson.Dumps(Bog.ToGltf(Doc), 1));
        }
        if (Slot.CanWrite)
        {
            yield return ImportJson(Codec.Write);
        }
    }
    protected void Rewrite(Action<JObj> edit)
    {
        JObj doc = Codec.Read(Slot.Data);
        edit(doc);
        byte[] rebuilt = Codec.Write(doc);
        if (!rebuilt.AsSpan().SequenceEqual(Slot.Data))
        {
            Store(rebuilt);
        }
    }
}
internal sealed class LclNode(Slot slot, string name, string path) : DocNode(slot, name, path, DocCodec.Lcl)
{
    protected override IEnumerable<PropertyRow> Extra(JObj doc)
    {
        List<object?> strings = J.A(doc["strings"]);
        for (int i = 0; i < strings.Count; i++)
        {
            JObj s = J.O(strings[i]);
            string label = $"{J.S(s["key"])} (tag {J.L(s["tag"])})";
            string text = J.S(s["text"]);
            if (!Slot.CanWrite)
            {
                yield return PropertyRow.Of(label, text);
                continue;
            }
            int index = i;
            yield return PropertyRow.Editable(label, text, value => Rewrite(d => J.O(J.A(d["strings"])[index])["text"] = value))
                with { Changed = AssetChange.Bytes | AssetChange.Properties | AssetChange.Dirty };
        }
    }
}
internal sealed class MeshDocNode(Slot slot, string name, string path, DocCodec codec)
    : DocNode(slot, name, path, codec), ISceneAsset
{
    private (int Revision, SceneGeometry Scene)? _scene;
    public SceneGeometry? GetScene()
    {
        if (_scene is not { } cached || cached.Revision != Slot.Revision)
        {
            _scene = cached = (Slot.Revision, ObjScene.Build(Codec.Obj!(Doc), FileStem));
        }
        return cached.Scene.TriangleCount > 0 ? cached.Scene : null;
    }
}
internal static class ObjScene
{
    public static SceneGeometry Build(string obj, string name)
    {
        List<Vector3> v = [];
        List<float> positions = [];
        List<float> normals = [];
        List<SceneSubmesh> subs = [];
        string group = name;
        int first = 0;
        void Close()
        {
            int count = (positions.Count / 3) - first;
            if (count > 0)
            {
                subs.Add(new SceneSubmesh(group, first, count, -1));
            }
            first = positions.Count / 3;
        }
        foreach (string raw in obj.Split('\n'))
        {
            string[] p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0)
            {
                continue;
            }
            switch (p[0])
            {
                case "o" or "g" when p.Length > 1:
                    Close();
                    group = p[1];
                    break;
                case "v" when p.Length >= 4:
                    v.Add(new Vector3(Number(p[1]), Number(p[2]), Number(p[3])));
                    break;
                case "f" when p.Length >= 4:
                    int a = int.Parse(p[1], CultureInfo.InvariantCulture) - 1;
                    int b = int.Parse(p[2], CultureInfo.InvariantCulture) - 1;
                    int c = int.Parse(p[3], CultureInfo.InvariantCulture) - 1;
                    if ((uint)a >= v.Count || (uint)b >= v.Count || (uint)c >= v.Count)
                    {
                        break;
                    }
                    Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                    n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                    foreach (int k in (ReadOnlySpan<int>)[a, b, c])
                    {
                        positions.AddRange([v[k].X, v[k].Y, v[k].Z]);
                        normals.AddRange([n.X, n.Y, n.Z]);
                    }
                    break;
                default:
                    break;
            }
        }
        Close();
        SceneMesh mesh = new() { Name = name, Positions = [.. positions], Normals = [.. normals], Submeshes = subs };
        return new SceneGeometry { Meshes = [mesh] };
    }
    private static float Number(string s)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : 0f;
    }
}
