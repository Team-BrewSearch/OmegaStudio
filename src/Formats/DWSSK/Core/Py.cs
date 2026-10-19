using System.Globalization;
using System.Numerics;
using System.Text;
namespace DWSSK.Formats.Core;
internal static class Py
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;
    public static string Upper(string s)
    {
        StringBuilder sb = new(s.Length);
        foreach (char c in s)
        {
            if (c == 'ß')
            {
                sb.Append("SS");
            }
            else
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }
    public static string Lower(string s)
    {
        return s.ToLowerInvariant();
    }
    public static readonly double PositiveNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);
    public static float ToSingle(double v)
    {
        float f = (float)v;
        return float.IsInfinity(f) && !double.IsInfinity(v)
            ? throw new StructException("float too large to pack with f format")
            : f;
    }
    public static double Round(double v)
    {
        return Math.Round(v, MidpointRounding.ToEven);
    }
    public static string Hex8(uint v)
    {
        return v.ToString("x8", s_inv);
    }
    public static string Str(long v)
    {
        return v.ToString(s_inv);
    }
    public static string Repr(double x)
    {
        if (double.IsNaN(x))
        {
            return "nan";
        }
        if (double.IsInfinity(x))
        {
            return x > 0 ? "inf" : "-inf";
        }
        (string digits, int decpt, bool negative) = Shortest(x);
        string sign = negative ? "-" : string.Empty;
        if (decpt is > -4 and <= 16)
        {
            return sign + Fixed(digits, decpt, true);
        }
        return sign + Exponent(digits, decpt - 1, false);
    }
    public static string JsonFloat(double x)
    {
        return double.IsNaN(x) ? "NaN" : double.IsPositiveInfinity(x) ? "Infinity" : double.IsNegativeInfinity(x) ? "-Infinity" : Repr(x);
    }
    private static (string Digits, int Decpt, bool Negative) Shortest(double x)
    {
        bool negative = double.IsNegative(x);
        if (x == 0)
        {
            return ("0", 1, negative);
        }
        string r = Math.Abs(x).ToString("R", s_inv);
        int e = r.IndexOfAny(['E', 'e']);
        int exp = 0;
        string mant = r;
        if (e >= 0)
        {
            exp = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, s_inv);
            mant = r[..e];
        }
        int dot = mant.IndexOf('.', StringComparison.Ordinal);
        string intPart = dot < 0 ? mant : mant[..dot];
        string frac = dot < 0 ? string.Empty : mant[(dot + 1)..];
        string all = intPart + frac;
        int decpt = intPart.Length + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }
        all = all[lead..];
        decpt -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            all = "0";
        }
        return (all, decpt, negative);
    }
    private static string Fixed(string digits, int decpt, bool addDotZero)
    {
        string s;
        if (decpt <= 0)
        {
            s = "0." + new string('0', -decpt) + digits;
        }
        else if (decpt >= digits.Length)
        {
            s = digits + new string('0', decpt - digits.Length) + (addDotZero ? ".0" : string.Empty);
        }
        else
        {
            s = digits[..decpt] + "." + digits[decpt..];
        }
        return s;
    }
    private static string Exponent(string digits, int exp, bool keepZeros)
    {
        string mant = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        if (!keepZeros && mant.Contains('.', StringComparison.Ordinal))
        {
            mant = mant.TrimEnd('0').TrimEnd('.');
        }
        string es = Math.Abs(exp).ToString("00", s_inv);
        return mant + "e" + (exp < 0 ? "-" : "+") + es;
    }
    public static string FormatG(double x, int precision)
    {
        if (double.IsNaN(x))
        {
            return "nan";
        }
        if (double.IsInfinity(x))
        {
            return x > 0 ? "inf" : "-inf";
        }
        if (precision == 0)
        {
            precision = 1;
        }
        bool negative = double.IsNegative(x);
        string sign = negative ? "-" : string.Empty;
        if (x == 0)
        {
            return sign + "0";
        }
        (string digits, int decpt) = RoundDigits(Math.Abs(x), precision);
        int exp = decpt - 1;
        if (exp >= -4 && exp < precision)
        {
            string s = Fixed(digits, decpt, false);
            if (s.Contains('.', StringComparison.Ordinal))
            {
                s = s.TrimEnd('0').TrimEnd('.');
            }
            return sign + s;
        }
        return sign + Exponent(digits.TrimEnd('0') is { Length: > 0 } t ? t : "0", exp, false);
    }
    private static (string Digits, int Decpt) RoundDigits(double x, int precision)
    {
        (string shortest, int sdec, _) = Shortest(x);
        if (shortest.Length <= precision)
        {
            return (shortest.PadRight(precision, '0'), sdec);
        }
        if (!(shortest.Length == precision + 1 && shortest[^1] == '5'))
        {
            string e = x.ToString("E" + (precision - 1).ToString(s_inv), s_inv);
            int ei = e.IndexOf('E', StringComparison.Ordinal);
            string mant = e[..ei].Replace(".", string.Empty, StringComparison.Ordinal);
            int exp = int.Parse(e[(ei + 1)..], NumberStyles.AllowLeadingSign, s_inv);
            return (mant, exp + 1);
        }
        return ExactRound(x, precision);
    }
    private static (string Digits, int Decpt) ExactRound(double x, int precision)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int rawExp = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (rawExp == 0)
        {
            rawExp = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }
        int e2 = rawExp - 1075;
        BigInteger num = mantissa;
        BigInteger den = BigInteger.One;
        if (e2 > 0)
        {
            num <<= e2;
        }
        else
        {
            den <<= -e2;
        }
        int decpt = (int)Math.Floor(Math.Log10(x)) + 1;
        BigInteger scaledNum = num;
        BigInteger scaledDen = den;
        int shift = precision - decpt;
        if (shift > 0)
        {
            scaledNum *= BigInteger.Pow(10, shift);
        }
        else if (shift < 0)
        {
            scaledDen *= BigInteger.Pow(10, -shift);
        }
        BigInteger q = BigInteger.DivRem(scaledNum, scaledDen, out BigInteger rem);
        BigInteger limit = BigInteger.Pow(10, precision);
        BigInteger lower = BigInteger.Pow(10, precision - 1);
        if (q >= limit)
        {
            decpt++;
            scaledDen *= 10;
            q = BigInteger.DivRem(scaledNum, scaledDen, out rem);
        }
        else if (q < lower)
        {
            decpt--;
            scaledNum *= 10;
            q = BigInteger.DivRem(scaledNum, scaledDen, out rem);
        }
        int cmp = (rem * 2).CompareTo(scaledDen);
        if (cmp > 0 || (cmp == 0 && !q.IsEven))
        {
            q += 1;
            if (q >= limit)
            {
                q /= 10;
                decpt++;
            }
        }
        return (q.ToString(s_inv), decpt);
    }
    public static double ParseFloat(string s)
    {
        string t = s.Trim();
        string low = t.ToLowerInvariant();
        switch (low)
        {
            case "nan" or "+nan":
                return PositiveNaN;
            case "-nan":
                return -PositiveNaN;
            case "inf" or "+inf" or "infinity" or "+infinity":
                return double.PositiveInfinity;
            case "-inf" or "-infinity":
                return double.NegativeInfinity;
            default:
                return double.Parse(t.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float, s_inv);
        }
    }
    public static int ParseInt(string s, int radix = 10)
    {
        string t = s.Trim();
        return radix == 16
            ? int.Parse(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t, NumberStyles.HexNumber, s_inv)
            : int.Parse(t, NumberStyles.AllowLeadingSign, s_inv);
    }
    public static uint ParseHex(string s)
    {
        string t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }
        return uint.Parse(t, NumberStyles.HexNumber, s_inv);
    }
    public static string[] SplitWhitespace(string s)
    {
        return s.Split([' ', '\t', '\n', '\r', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries);
    }
}
