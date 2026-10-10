using System.Text;
namespace Polarbit.Formats;

/// <summary>
/// Names the entries of the three older archives, which store hashes only (REPORT 2.3).
/// Every path-like string in the data is hashed as it stands and under data/, then joined with known and discovered
/// directories. A hit only counts when the payload's kind fits the extension, which keeps chance matches near zero.
/// Textures, sounds, modules and fonts are not scanned: they name nothing, and skipping them finds the same 995 names.
/// </summary>
internal static class NameRecovery
{
    private static readonly (string Directory, string Extension)[] s_directories =
    [
        ("data/Graphics/Textures/Tracks", ".pvr"), ("data/Graphics/Textures/Objects", ".pvr"),
        ("data/Graphics/Textures/Objects/Characters", ".pvr"), ("data/Graphics/Textures/Objects/Carts", ".pvr"),
        ("data/Graphics/Textures/Effects", ".pvr"), ("data/Graphics/Textures/Skymaps", ".pvr"),
        ("data/Audio/Music", ".psn"), ("data/textures/world", ".png"), ("data/textures/chars", ".png"),
        ("data/textures/Carts", ".png"), ("data/textures/Other", ".png"), ("data/pickups", ".png"),
    ];
    private static readonly string[] s_files =
    [
        "data/lang/lang.bin", "data/pickups/bomb.png", "data/pickups/missile.pof", "data/pickups/crate2.png",
        "data/textures/world/dirt.png", "data/textures/world/checkered.png", "data/textures/world/lightray.png",
        "data/textures/world/Bark02.png",
    ];
    private static readonly Dictionary<string, PayloadKind[]> s_kinds = new()
    {
        [".png"] = [PayloadKind.Png, PayloadKind.Pvr],
        [".pvr"] = [PayloadKind.Pvr],
        [".psn"] = [PayloadKind.Psnd],
        [".pof"] = [PayloadKind.Piff],
        [".trk"] = [PayloadKind.Piff],
        [".xml"] = [PayloadKind.Xml],
        [".bin"] = [PayloadKind.Lang],
        [".fnt"] = [PayloadKind.Pfnt],
        [".pxc"] = [PayloadKind.Pxc],
        [".plt"] = [PayloadKind.Plt, PayloadKind.Tlpb],
        [".txt"] = [PayloadKind.Text],
        [".vp"] = [PayloadKind.Text],
        [".fp"] = [PayloadKind.Text],
        [".hxx"] = [PayloadKind.Text],
        [".db"] = [PayloadKind.Cfb],
    };

    public static int Apply(VfsArchive archive)
    {
        Dictionary<uint, string> found = Recover(archive);
        foreach ((uint hash, string name) in found)
        {
            VfsEntry entry = archive.Find(hash)!;
            entry.Name = name;
            entry.NameRecovered = true;
        }
        return found.Count;
    }

    public static Dictionary<uint, string> Recover(VfsArchive archive)
    {
        Dictionary<uint, PayloadKind> kinds = archive.Entries.ToDictionary(e => e.Hash, e => e.Kind);
        HashSet<string> strings = new(StringComparer.Ordinal);
        foreach (VfsEntry entry in archive.Entries.Where(e => e.Kind is not (PayloadKind.Pvr or PayloadKind.Png or PayloadKind.Psnd or PayloadKind.Pmod or PayloadKind.Pfnt)))
        {
            Collect(entry.Payload, strings);
        }
        List<string> files = [.. strings
            .Select(s => s.Trim().Replace('\\', '/'))
            .Where(s => s_kinds.ContainsKey(ExtensionOf(s).ToLowerInvariant()))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        Dictionary<uint, string> found = [];
        HashSet<string> tried = new(StringComparer.Ordinal);
        void Try(string candidate)
        {
            if (!tried.Add(candidate.ToUpperInvariant()))
            {
                return;
            }
            uint hash = VfsArchive.NameHash(candidate);
            if (kinds.TryGetValue(hash, out PayloadKind kind)
                && s_kinds[ExtensionOf(candidate).ToLowerInvariant()].Contains(kind))
            {
                _ = found.TryAdd(hash, candidate);
            }
        }
        foreach (string candidate in s_files)
        {
            Try(candidate);
        }
        foreach (string file in files)
        {
            Try(file);
        }
        foreach (string file in files)
        {
            Try("data/" + file.TrimStart('/'));
        }
        foreach ((string directory, string extension) in s_directories)
        {
            foreach (string file in files)
            {
                Try($"{directory}/{Stem(file)}{extension}");
            }
        }
        HashSet<string> extensions = [.. found.Values.Select(ExtensionOf)];
        HashSet<(string, string)> known = [.. s_directories];
        List<(string Directory, string Extension)> discovered = [.. found.Values
            .SelectMany(name => extensions.Select(e => (DirectoryOf(name), e)))
            .Distinct()
            .Where(pair => !known.Contains(pair))
            .OrderBy(pair => pair.Item1, StringComparer.Ordinal)
            .ThenBy(pair => pair.Item2, StringComparer.Ordinal)];
        foreach ((string directory, string extension) in discovered)
        {
            foreach (string file in files)
            {
                Try($"{directory}/{Stem(file)}{extension}");
            }
        }
        return found;
    }

    /// <summary>Runs of path characters at least three long, as single bytes and as UTF-16LE.</summary>
    private static void Collect(ReadOnlySpan<byte> data, HashSet<string> strings)
    {
        int run = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && IsPathChar(data[i]))
            {
                run++;
                continue;
            }
            if (run >= 3)
            {
                _ = strings.Add(Encoding.Latin1.GetString(data.Slice(i - run, run)));
            }
            run = 0;
        }
        StringBuilder wide = new();
        for (int i = 0; i + 1 < data.Length;)
        {
            int end = i;
            while (end + 1 < data.Length && IsPathChar(data[end]) && data[end + 1] == 0)
            {
                end += 2;
            }
            if (end - i >= 6)
            {
                _ = wide.Clear();
                for (int k = i; k < end; k += 2)
                {
                    _ = wide.Append((char)data[k]);
                }
                _ = strings.Add(wide.ToString());
                i = end;
            }
            else
            {
                i++;
            }
        }
    }

    private static bool IsPathChar(byte c)
    {
        return c is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'_' or (byte)'.' or (byte)'/' or (byte)'\\' or (byte)' ' or (byte)'-';
    }

    /// <summary>What Python's os.path.splitext calls the extension: from the last dot of the file name, unless the name starts there.</summary>
    private static string ExtensionOf(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 && name[..dot].Any(c => c != '.') ? name[dot..] : string.Empty;
    }

    private static string Stem(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        string extension = ExtensionOf(name);
        return name[..(name.Length - extension.Length)];
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash].TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : path[..(slash + 1)];
    }
}
