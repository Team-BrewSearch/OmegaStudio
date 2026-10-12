using System.Globalization;
using Omega.Plugins;
using SceneGate.Ekona.Containers.Rom;
namespace Nintendo.Formats.Nodes;
internal sealed class ProgramInfoNode : AssetNode, IDescribable
{
    private const byte AutoStartMask = 0x04;
    private static readonly string[] s_unitCodes = Enum.GetNames<DeviceUnitKind>();
    private readonly NdsRom _rom;
    private readonly ProgramInfo _information;
    public ProgramInfoNode(NdsRom rom, string name, string path, ProgramInfo information)
        : base(name, path, AssetKinds.Unknown, "ROM header")
    {
        _rom = rom;
        _information = information;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Editable(
            "Game title",
            _information.GameTitle,
            value => Set(() => _information.GameTitle = Ascii("Game title", value, 12)));
        yield return PropertyRow.Editable(
            "Game code",
            _information.GameCode,
            value => Set(() => _information.GameCode = Ascii("Game code", value, 4, exact: true)));
        yield return PropertyRow.Editable(
            "Maker code",
            _information.MakerCode,
            value => Set(() => _information.MakerCode = Ascii("Maker code", value, 2, exact: true)));
        yield return PropertyRow.Editable(
            "ROM version",
            _information.Version.ToString(CultureInfo.InvariantCulture),
            value => Set(() => _information.Version = Number("ROM version", value)));
        yield return PropertyRow.Options(
            "Unit code",
            _information.UnitCode.ToString(),
            s_unitCodes,
            value => Set(() => _information.UnitCode = UnitCode(value)));
        yield return PropertyRow.Switch(
            "Skip health screen",
            (_information.AutoStartFlag & AutoStartMask) != 0,
            value => Set(() => _information.AutoStartFlag = value
                ? (byte)(_information.AutoStartFlag | AutoStartMask)
                : (byte)(_information.AutoStartFlag & ~AutoStartMask)));
        yield return PropertyRow.Of("Region", _information.Region);
        yield return PropertyRow.Of("Cartridge size", Sizes.Describe(_information.CartridgeSize));
        yield return PropertyRow.Of("Encryption seed", _information.EncryptionSeed);
        yield return PropertyRow.Of("ARM9 entry", $"0x{_information.Arm9Executable.EntrypointAddress:X8}");
        yield return PropertyRow.Of("ARM7 entry", $"0x{_information.Arm7Executable.EntrypointAddress:X8}");
    }
    private void Set(Action edit)
    {
        edit();
        _rom.MarkChanged();
    }
    private DeviceUnitKind UnitCode(string value)
    {
        DeviceUnitKind kind = Enum.Parse<DeviceUnitKind>(value);
        return kind == DeviceUnitKind.DS || _rom.HasDsiProgram
            ? kind
            : throw new InvalidDataException(
                $"this ROM has no DSi program sections, so its unit code cannot be {kind}");
    }
    private static string Ascii(string field, string value, int length, bool exact = false)
    {
        string text = value.Trim();
        if (exact ? text.Length != length : text.Length > length)
        {
            throw new InvalidDataException(exact
                ? $"{field} is exactly {length} characters, but '{text}' is {text.Length}"
                : $"{field} is at most {length} characters, but '{text}' is {text.Length}");
        }
        foreach (char letter in text)
        {
            if (letter is < ' ' or > '~')
            {
                throw new InvalidDataException(
                    $"{field} holds plain ASCII only, and '{letter}' is not");
            }
        }
        return text;
    }
    private static byte Number(string field, string value)
    {
        return byte.TryParse(value.Trim(), out byte parsed)
            ? parsed
            : throw new InvalidDataException($"{field} is a number from 0 to 255, and '{value}' is not");
    }
}
internal sealed record BannerLanguage(
    string Language,
    Version Since,
    Func<Banner, string?> Read,
    Action<Banner, string> Write);
internal sealed class BannerNode : AssetNode, IDescribable
{
    private const int Lines = 3;
    private const int TitleLength = 128;
    private static readonly string[] s_lineLabels = ["Title", "Subtitle", "Publisher"];
    private static readonly BannerLanguage[] s_languages =
    [
        new("Japanese", new Version(0, 1), b => b.JapaneseTitle, (b, v) => b.JapaneseTitle = v),
        new("English", new Version(0, 1), b => b.EnglishTitle, (b, v) => b.EnglishTitle = v),
        new("French", new Version(0, 1), b => b.FrenchTitle, (b, v) => b.FrenchTitle = v),
        new("German", new Version(0, 1), b => b.GermanTitle, (b, v) => b.GermanTitle = v),
        new("Italian", new Version(0, 1), b => b.ItalianTitle, (b, v) => b.ItalianTitle = v),
        new("Spanish", new Version(0, 1), b => b.SpanishTitle, (b, v) => b.SpanishTitle = v),
        new("Chinese", new Version(0, 2), b => b.ChineseTitle, (b, v) => b.ChineseTitle = v),
        new("Korean", new Version(0, 3), b => b.KoreanTitle, (b, v) => b.KoreanTitle = v),
    ];
    private readonly NdsRom _rom;
    private readonly Banner _banner;
    public BannerNode(NdsRom rom, string name, string path, Banner banner)
        : base(name, path, AssetKinds.Unknown, "Banner")
    {
        _rom = rom;
        _banner = banner;
    }
    public IEnumerable<PropertyRow> Describe()
    {
        yield return PropertyRow.Of("Banner version", _banner.Version);
        foreach (BannerLanguage language in s_languages)
        {
            if (_banner.Version < language.Since)
            {
                continue;
            }
            string[] lines = Split(language.Read(_banner));
            for (int line = 0; line < Lines; line++)
            {
                yield return Line(language, lines, line);
            }
        }
    }
    private PropertyRow Line(BannerLanguage language, string[] current, int index)
    {
        return PropertyRow.Editable(
            $"{s_lineLabels[index]} ({language.Language})",
            current[index],
            value => Replace(language, index, value));
    }
    private void Replace(BannerLanguage language, int index, string value)
    {
        string[] lines = Split(language.Read(_banner));
        lines[index] = value.Trim();
        string title = string.Join('\n', lines).TrimEnd('\n');
        if (title.Length > TitleLength)
        {
            throw new InvalidDataException(
                $"the {language.Language} banner text holds at most {TitleLength} characters, " +
                $"and these three lines come to {title.Length}");
        }
        language.Write(_banner, title);
        _rom.MarkChanged();
    }
    private static string[] Split(string? title)
    {
        string[] lines = new string[Lines];
        string[] stored = (title ?? string.Empty).Split('\n');
        for (int i = 0; i < Lines; i++)
        {
            lines[i] = i < stored.Length ? stored[i] : string.Empty;
        }
        return lines;
    }
}
