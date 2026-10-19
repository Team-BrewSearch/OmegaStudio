using System.Text.RegularExpressions;
using DWSSK.Formats.Core;
using DWSSK.Formats.Models;
using DWSSK.Formats.Shaders;
using DWSSK.Formats.Textures;
using DWSSK.Formats.Wad;
using Omega.Plugins;
namespace DWSSK.Formats.Nodes;
internal abstract class Slot
{
    public abstract string File { get; }
    public string Name => File[(File.LastIndexOf('/') + 1)..];
    public string Extension => WadArchive.ExtensionOf(File);
    public abstract Platform Platform { get; }
    public abstract byte[] Data { get; }
    public abstract int Revision { get; }
    public abstract bool CanWrite { get; }
    public abstract uint? Hash { get; }
    public abstract SlotGroup Group { get; }
    public virtual bool PlatformKnown => true;
    public virtual int Size => Data.Length;
    public virtual bool Loaded => true;
    public abstract void Replace(byte[] data);
    public string Directory => File.Contains('/', StringComparison.Ordinal) ? File[..File.LastIndexOf('/')] : string.Empty;
}
internal abstract class SlotGroup(IHostServices host)
{
    private readonly Dictionary<uint, (int Revision, ImageData? Image)> _images = [];
    private Dictionary<uint, Slot>? _byHash;
    private int _hashRevision = -1;
    public IHostServices Host { get; } = host;
    public abstract Platform Platform { get; }
    public abstract IEnumerable<Slot> Parts(Slot from);
    public abstract int Revision { get; }
    public virtual SlotGroup? Global => null;
    public RegionIndex? Region { get; set; }
    public abstract IEnumerable<Slot> All();
    public virtual IEnumerable<IEnumerable<Slot>> Containers()
    {
        return [All()];
    }
    public Slot? MasterModel(Slot animpack)
    {
        string dir = animpack.Directory.Length == 0 ? "MASTER" : animpack.Directory + "/MASTER";
        foreach (IEnumerable<Slot> part in Containers())
        {
            Slot? hit = part.Where(s => s.Directory == dir && s.Name.EndsWith(".gds", StringComparison.Ordinal))
                .MinBy(s => s.Name, StringComparer.Ordinal);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
    public IEnumerable<Slot> AnimPacksFor(Slot model)
    {
        return All().Where(s => s.Extension == ".animpack" && ReferenceEquals(MasterModel(s), model));
    }
    public Slot? ByHash(uint hash, Slot? from = null)
    {
        if (from is not null)
        {
            foreach (Slot s in Parts(from))
            {
                if (s.Hash == hash && IsTexture(s))
                {
                    return s;
                }
            }
        }
        if (_hashRevision != Revision || _byHash is null)
        {
            _byHash = [];
            foreach (Slot s in All())
            {
                if (s.Hash is uint h && IsTexture(s))
                {
                    _byHash.TryAdd(h, s);
                }
            }
            _hashRevision = Revision;
        }
        return _byHash.TryGetValue(hash, out Slot? hit) ? hit : Global?.ByHash(hash) ?? Region?.Find(hash);
    }
    private static bool IsTexture(Slot s)
    {
        return s.Name.EndsWith(".htx", StringComparison.OrdinalIgnoreCase) && s is not WadSlot { Entry.IsAlias: true };
    }
    public ImageData? Texture(uint hash, Slot? from)
    {
        Slot? slot = ByHash(hash, from);
        if (slot is null)
        {
            return null;
        }
        if (_images.TryGetValue(hash, out (int Revision, ImageData? Image) cached) && cached.Revision == slot.Revision + Revision)
        {
            return cached.Image;
        }
        ImageData? image = null;
        try
        {
            Htx t = Htx.Parse(slot.Data);
            (byte[] rgba, int w, int h) = t.DecodeLevel(slot.Platform, 0);
            image = ImageData.Create(Path.GetFileNameWithoutExtension(slot.Name), w, h, rgba, t.Describe(slot.Platform));
        }
        catch (Exception ex) when (Failure.IsData(ex))
        {
            Host.Log($"{slot.File}: the texture could not be decoded: {ex.Message}");
        }
        _images[hash] = (slot.Revision + Revision, image);
        return image;
    }
    private (int Revision, List<string> Names)? _p2bStrings;
    public IEnumerable<string> PrintableRuns(int min)
    {
        if (_p2bStrings is not { } cached || cached.Revision != Revision)
        {
            List<string> names = [];
            foreach (Slot s in All())
            {
                if (s.Extension.EndsWith(".p2b", StringComparison.Ordinal))
                {
                    names.Add("\u0000stem:" + s.Name.Split('.')[0]);
                    foreach (Match m in Rx.Plain(@"[\x20-\x7e]{2,}").Matches(Bin.Latin1(s.Data)))
                    {
                        names.Add(m.Value);
                    }
                }
            }
            _p2bStrings = cached = (Revision, names);
        }
        foreach (string n in cached.Names)
        {
            if (n.StartsWith("\u0000stem:", StringComparison.Ordinal))
            {
                if (min == 3)
                {
                    yield return n[6..];
                }
            }
            else if (n.Length >= min)
            {
                yield return n;
            }
        }
    }
    public IEnumerable<string> AnimationNames()
    {
        return Anim.AnimPack.ExtraNames.Concat(PrintableRuns(3)).Concat(NameBook.Section("anim"));
    }
    public IEnumerable<string> LevelNames()
    {
        List<string> names = [];
        foreach (Slot s in All())
        {
            string[] parts = s.File.Split('/');
            names.AddRange(parts);
            names.Add(string.Join('\\', parts));
            names.Add(s.Name.Split('.')[0]);
            if (s.Extension == ".nog")
            {
                names.AddRange(Data.Nog.Strings(s.Data));
            }
        }
        names.AddRange(PrintableRuns(2));
        names.AddRange(NameBook.Section("bog"));
        return names;
    }
    public ITextureLookup Lookup(Slot from)
    {
        return new Finder(this, from);
    }
    private sealed class Finder(SlotGroup group, Slot from) : ITextureLookup
    {
        public ImageData? Find(uint wadHash)
        {
            return group.Texture(wadHash, from);
        }
    }
}
internal static class Paths
{
    public static Platform? PlatformOf(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/');
        foreach (string name in Platforms.Names)
        {
            if (parts.Contains(name))
            {
                return Platforms.Parse(name);
            }
        }
        return null;
    }
}
