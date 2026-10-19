using System.Collections;
using System.Globalization;
using System.Text;
namespace DWSSK.Formats.Core;
internal sealed class JObj : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    public int Count => _keys.Count;
    public IReadOnlyList<string> Keys => _keys;
    public object? this[string key]
    {
        get => _values.TryGetValue(key, out object? v)
            ? v
            : throw new KeyNotFoundException($"the document has no \"{key}\" field");
        set
        {
            if (!_values.ContainsKey(key))
            {
                _keys.Add(key);
            }
            _values[key] = value;
        }
    }
    public bool Has(string key)
    {
        return _values.ContainsKey(key);
    }
    public object? Get(string key, object? fallback = null)
    {
        return _values.TryGetValue(key, out object? v) ? v : fallback;
    }
    public object? Pop(string key)
    {
        object? v = this[key];
        Remove(key);
        return v;
    }
    public bool Remove(string key)
    {
        if (!_values.Remove(key))
        {
            return false;
        }
        _ = _keys.Remove(key);
        return true;
    }
    public void SetDefault(string key, object? value)
    {
        if (!Has(key))
        {
            this[key] = value;
        }
    }
    public JObj Copy()
    {
        JObj c = [];
        foreach (string k in _keys)
        {
            c[k] = _values[k];
        }
        return c;
    }
    public void Add(string key, object? value)
    {
        this[key] = value;
    }
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (string k in _keys)
        {
            yield return new KeyValuePair<string, object?>(k, _values[k]);
        }
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
internal static class J
{
    public static long L(object? v)
    {
        return v switch
        {
            long l => l,
            int i => i,
            uint u => u,
            bool b => b ? 1 : 0,
            byte y => y,
            _ => throw new StructException($"expected an integer, found {Describe(v)}"),
        };
    }
    public static int I(object? v)
    {
        return checked((int)L(v));
    }
    public static uint U(object? v)
    {
        long l = L(v);
        return l is < 0 or > uint.MaxValue
            ? throw new StructException($"{l} does not fit in an unsigned 32-bit field")
            : (uint)l;
    }
    public static uint U16(object? v)
    {
        long l = L(v);
        return l is < 0 or > ushort.MaxValue
            ? throw new StructException($"{l} does not fit in an unsigned 16-bit field")
            : (uint)l;
    }
    public static byte U8(object? v)
    {
        long l = L(v);
        return l is < 0 or > 255 ? throw new StructException($"{l} does not fit in a byte") : (byte)l;
    }
    public static int I32(object? v)
    {
        long l = L(v);
        return l is < int.MinValue or > int.MaxValue
            ? throw new StructException($"{l} does not fit in a signed 32-bit field")
            : (int)l;
    }
    public static short I16(object? v)
    {
        long l = L(v);
        return l is < short.MinValue or > short.MaxValue
            ? throw new StructException($"{l} does not fit in a signed 16-bit field")
            : (short)l;
    }
    public static double D(object? v)
    {
        return v switch
        {
            double d => d,
            long l => l,
            int i => i,
            bool b => b ? 1 : 0,
            float f => f,
            _ => throw new StructException($"expected a number, found {Describe(v)}"),
        };
    }
    public static string S(object? v)
    {
        return v as string ?? throw new InvalidDataException($"expected a string, found {Describe(v)}");
    }
    public static JObj O(object? v)
    {
        return v as JObj ?? throw new InvalidDataException($"expected an object, found {Describe(v)}");
    }
    public static List<object?> A(object? v)
    {
        return v as List<object?> ?? throw new InvalidDataException($"expected a list, found {Describe(v)}");
    }
    public static bool Truthy(object? v)
    {
        return v switch
        {
            null => false,
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            string s => s.Length > 0,
            List<object?> a => a.Count > 0,
            JObj o => o.Count > 0,
            _ => true,
        };
    }
    public static List<object?> List(IEnumerable<object?> items)
    {
        return [.. items];
    }
    public static List<object?> Longs(IEnumerable<long> items)
    {
        return [.. items.Select(x => (object?)x)];
    }
    public static List<object?> Longs(IEnumerable<uint> items)
    {
        return [.. items.Select(x => (object?)(long)x)];
    }
    public static List<object?> Longs(IEnumerable<int> items)
    {
        return [.. items.Select(x => (object?)(long)x)];
    }
    public static List<object?> Longs(ReadOnlySpan<byte> items)
    {
        List<object?> list = new(items.Length);
        foreach (byte b in items)
        {
            list.Add((long)b);
        }
        return list;
    }
    public static List<object?> Doubles(IEnumerable<float> items)
    {
        return [.. items.Select(x => (object?)(double)x)];
    }
    public static List<object?> Doubles(IEnumerable<double> items)
    {
        return [.. items.Select(x => (object?)x)];
    }
    public static bool Equal(object? a, object? b)
    {
        switch (a)
        {
            case null:
                return b is null;
            case JObj x when b is JObj y:
                if (x.Count != y.Count)
                {
                    return false;
                }
                foreach (KeyValuePair<string, object?> kv in x)
                {
                    if (!y.Has(kv.Key) || !Equal(kv.Value, y[kv.Key]))
                    {
                        return false;
                    }
                }
                return true;
            case List<object?> x when b is List<object?> y:
                if (x.Count != y.Count)
                {
                    return false;
                }
                for (int i = 0; i < x.Count; i++)
                {
                    if (!Equal(x[i], y[i]))
                    {
                        return false;
                    }
                }
                return true;
            case string s:
                return b is string t && s == t;
            case bool or long or double:
                return b is bool or long or double && D(a) == D(b);
            default:
                return Equals(a, b);
        }
    }
    public static string Describe(object? v)
    {
        return v switch
        {
            null => "null",
            string s => $"the string \"{s}\"",
            List<object?> => "a list",
            JObj => "an object",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "a value",
        };
    }
}
internal static class PyJson
{
    public static string Dumps(object? value, int? indent = null, bool ensureAscii = true)
    {
        StringBuilder sb = new();
        Write(sb, value, indent, ensureAscii, 0);
        return sb.ToString();
    }
    public static byte[] DumpBytes(object? value, int? indent = 1, bool ensureAscii = true)
    {
        return new UTF8Encoding(false).GetBytes(Dumps(value, indent, ensureAscii));
    }
    private static void Write(StringBuilder sb, object? v, int? indent, bool ascii, int level)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(Py.JsonFloat(d));
                break;
            case string s:
                WriteString(sb, s, ascii);
                break;
            case List<object?> list:
                if (list.Count == 0)
                {
                    sb.Append("[]");
                    break;
                }
                sb.Append('[');
                for (int k = 0; k < list.Count; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(indent is null ? ", " : ",");
                    }
                    NewLine(sb, indent, level + 1);
                    Write(sb, list[k], indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                break;
            case JObj obj:
                if (obj.Count == 0)
                {
                    sb.Append("{}");
                    break;
                }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object?> kv in obj)
                {
                    if (!first)
                    {
                        sb.Append(indent is null ? ", " : ",");
                    }
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, kv.Key, ascii);
                    sb.Append(": ");
                    Write(sb, kv.Value, indent, ascii, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                break;
            default:
                throw new InvalidDataException($"cannot write {v.GetType().Name} as JSON");
        }
    }
    private static void NewLine(StringBuilder sb, int? indent, int level)
    {
        if (indent is { } n)
        {
            sb.Append('\n');
            sb.Append(' ', n * level);
        }
    }
    private static void WriteString(StringBuilder sb, string s, bool ascii)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || (ascii && c > 0x7E))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
    public static object? Loads(string text)
    {
        Parser p = new(text);
        p.Skip();
        object? v = p.Value();
        p.Skip();
        return p.AtEnd ? v : throw p.Error("extra data after the JSON value");
    }
    public static object? Loads(ReadOnlySpan<byte> utf8)
    {
        return Loads(Encoding.UTF8.GetString(utf8));
    }
    private sealed class Parser(string s)
    {
        private int _i;
        public bool AtEnd => _i >= s.Length;
        public InvalidDataException Error(string what)
        {
            int line = 1;
            int col = 1;
            for (int k = 0; k < _i && k < s.Length; k++)
            {
                if (s[k] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            return new InvalidDataException($"JSON: {what} at line {line} column {col}");
        }
        public void Skip()
        {
            while (_i < s.Length && s[_i] is ' ' or '\t' or '\n' or '\r')
            {
                _i++;
            }
        }
        private bool Lit(string word)
        {
            if (string.CompareOrdinal(s, _i, word, 0, word.Length) == 0)
            {
                _i += word.Length;
                return true;
            }
            return false;
        }
        public object? Value()
        {
            if (AtEnd)
            {
                throw Error("expected a value");
            }
            char c = s[_i];
            switch (c)
            {
                case '{':
                    return Object();
                case '[':
                    return Array();
                case '"':
                    return String();
                case 't' when Lit("true"):
                    return true;
                case 'f' when Lit("false"):
                    return false;
                case 'n' when Lit("null"):
                    return null;
                case 'N' when Lit("NaN"):
                    return Py.PositiveNaN;
                case 'I' when Lit("Infinity"):
                    return double.PositiveInfinity;
                case '-' when Lit("-Infinity"):
                    return double.NegativeInfinity;
                default:
                    return Number();
            }
        }
        private JObj Object()
        {
            _i++;
            JObj o = [];
            Skip();
            if (!AtEnd && s[_i] == '}')
            {
                _i++;
                return o;
            }
            while (true)
            {
                Skip();
                if (AtEnd || s[_i] != '"')
                {
                    throw Error("expected a property name");
                }
                string key = String();
                Skip();
                if (AtEnd || s[_i] != ':')
                {
                    throw Error("expected ':'");
                }
                _i++;
                Skip();
                o[key] = Value();
                Skip();
                if (AtEnd)
                {
                    throw Error("unterminated object");
                }
                if (s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (s[_i] == '}')
                {
                    _i++;
                    return o;
                }
                throw Error("expected ',' or '}'");
            }
        }
        private List<object?> Array()
        {
            _i++;
            List<object?> a = [];
            Skip();
            if (!AtEnd && s[_i] == ']')
            {
                _i++;
                return a;
            }
            while (true)
            {
                Skip();
                a.Add(Value());
                Skip();
                if (AtEnd)
                {
                    throw Error("unterminated array");
                }
                if (s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (s[_i] == ']')
                {
                    _i++;
                    return a;
                }
                throw Error("expected ',' or ']'");
            }
        }
        private string String()
        {
            _i++;
            StringBuilder sb = new();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("unterminated string");
                }
                char c = s[_i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    if (c < 0x20)
                    {
                        throw Error("control character in a string");
                    }
                    sb.Append(c);
                    continue;
                }
                if (AtEnd)
                {
                    throw Error("unterminated escape");
                }
                char e = s[_i++];
                switch (e)
                {
                    case '"':
                    case '\\':
                    case '/':
                        sb.Append(e);
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (_i + 4 > s.Length)
                        {
                            throw Error("short \\u escape");
                        }
                        sb.Append((char)int.Parse(s.AsSpan(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _i += 4;
                        break;
                    default:
                        throw Error($"invalid escape \\{e}");
                }
            }
        }
        private object Number()
        {
            int start = _i;
            if (_i < s.Length && s[_i] == '-')
            {
                _i++;
            }
            bool isFloat = false;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c is >= '0' and <= '9')
                {
                    _i++;
                }
                else if (c is '.' or 'e' or 'E' or '+' or '-')
                {
                    isFloat = true;
                    _i++;
                }
                else
                {
                    break;
                }
            }
            string t = s[start.._i];
            if (t.Length == 0 || t == "-")
            {
                throw Error("expected a value");
            }
            if (isFloat)
            {
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
            {
                return l;
            }
            throw Error($"integer {t} is too large");
        }
    }
}
