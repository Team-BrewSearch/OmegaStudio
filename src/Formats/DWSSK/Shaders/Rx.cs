using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
namespace DWSSK.Formats.Shaders;
internal static class Rx
{
    private static readonly ConcurrentDictionary<string, Regex> s_full = new();
    private static readonly ConcurrentDictionary<string, Regex> s_plain = new();
    public static Match? Full(string pattern, string s)
    {
        Regex r = s_full.GetOrAdd(pattern, p => new Regex(@"\A(?:" + p + @")\z", RegexOptions.CultureInvariant));
        Match m = r.Match(s);
        return m.Success ? m : null;
    }
    public static Match? Search(string pattern, string s)
    {
        Match m = Plain(pattern).Match(s);
        return m.Success ? m : null;
    }
    public static Match? Prefix(string pattern, string s)
    {
        Regex r = s_full.GetOrAdd("^" + pattern, p => new Regex(@"\A(?:" + pattern + ")", RegexOptions.CultureInvariant));
        Match m = r.Match(s);
        return m.Success ? m : null;
    }
    public static Regex Plain(string pattern)
    {
        return s_plain.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant));
    }
    public static string? G(this Match m, int i)
    {
        return m.Groups[i].Success ? m.Groups[i].Value : null;
    }
    public static long Int(string s, int radix = 10)
    {
        string t = s.Trim();
        if (radix == 16)
        {
            bool neg = t.StartsWith('-');
            string body = neg ? t[1..] : t;
            if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                body = body[2..];
            }
            long v = long.Parse(body, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return neg ? -v : v;
        }
        return long.Parse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
    public static bool IsDigits(string s)
    {
        return s.Length > 0 && s.All(char.IsDigit);
    }
    public static string Digits(string s)
    {
        return string.Concat(s.Where(char.IsDigit));
    }
}
internal static class Bits
{
    public static long B(long v, int lo, int n)
    {
        return (v >> lo) & ((1L << n) - 1);
    }
    public static long Put(long v, int lo, int n, long x)
    {
        long m = ((1L << n) - 1) << lo;
        return (v & ~m) | ((x << lo) & m);
    }
    public static long Put(long v, int lo, int n, bool x)
    {
        return Put(v, lo, n, x ? 1L : 0L);
    }
    public static string Hex(long v, int width)
    {
        return v.ToString("x" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
    public static string Hex(long v)
    {
        return v.ToString("x", CultureInfo.InvariantCulture);
    }
    public static string D(long v)
    {
        return v.ToString(CultureInfo.InvariantCulture);
    }
    public static string D2(long v)
    {
        return v.ToString("00", CultureInfo.InvariantCulture);
    }
}
