using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwissForge.Core.Json
{
    /// <summary>Kind discriminator for <see cref="JsonValue"/>.</summary>
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A tiny, dependency-free JSON DOM.
    /// <para>
    /// SwissForge deliberately ships zero NuGet dependencies. The add-in is loaded by COM into
    /// ESPRIT's own process, where it shares an AppDomain with the host and every other add-in.
    /// Dragging in System.Text.Json (or Newtonsoft) is the single most common cause of
    /// "works standalone, throws FileLoadException inside the CAM host" assembly-binding
    /// conflicts. 600 lines of self-contained JSON is cheaper than that class of bug.
    /// </para>
    /// </summary>
    public sealed class JsonValue : IEnumerable<JsonValue>
    {
        private readonly object _value;

        public JsonKind Kind { get; }

        private JsonValue(JsonKind kind, object value)
        {
            Kind = kind;
            _value = value;
        }

        // ---------------------------------------------------------------- factories

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null, null);

        public static JsonValue Bool(bool b) => new JsonValue(JsonKind.Bool, b);
        public static JsonValue Num(double d) => new JsonValue(JsonKind.Number, d);
        public static JsonValue Str(string s) => s == null ? Null : new JsonValue(JsonKind.String, s);
        public static JsonValue Arr() => new JsonValue(JsonKind.Array, new List<JsonValue>());
        public static JsonValue Obj() => new JsonValue(JsonKind.Object, new Dictionary<string, JsonValue>(StringComparer.Ordinal));

        public static JsonValue Arr(IEnumerable<JsonValue> items)
        {
            var a = Arr();
            if (items != null) foreach (var i in items) a.Add(i);
            return a;
        }

        /// <summary>Boxes a CLR value into a JsonValue. Supports primitives, IDictionary&lt;string,*&gt; and IEnumerable.</summary>
        public static JsonValue From(object o)
        {
            switch (o)
            {
                case null: return Null;
                case JsonValue jv: return jv;
                case bool b: return Bool(b);
                case string s: return Str(s);
                case DateTime dt: return Str(dt.ToString("o", CultureInfo.InvariantCulture));
                case Enum e: return Str(e.ToString());
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Num(Convert.ToDouble(o, CultureInfo.InvariantCulture));
            }

            if (o is IDictionary dict)
            {
                var obj = Obj();
                foreach (DictionaryEntry kv in dict)
                    obj[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)] = From(kv.Value);
                return obj;
            }

            if (o is IEnumerable seq)
            {
                var arr = Arr();
                foreach (var item in seq) arr.Add(From(item));
                return arr;
            }

            return Str(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- accessors

        public bool IsNull => Kind == JsonKind.Null;

        private List<JsonValue> AsList =>
            Kind == JsonKind.Array ? (List<JsonValue>)_value
            : throw new InvalidOperationException("JSON value is " + Kind + ", not an array.");

        private Dictionary<string, JsonValue> AsMap =>
            Kind == JsonKind.Object ? (Dictionary<string, JsonValue>)_value
            : throw new InvalidOperationException("JSON value is " + Kind + ", not an object.");

        public int Count =>
            Kind == JsonKind.Array ? AsList.Count
            : Kind == JsonKind.Object ? AsMap.Count
            : 0;

        public IEnumerable<string> Keys => Kind == JsonKind.Object ? AsMap.Keys : (IEnumerable<string>)Array.Empty<string>();

        public JsonValue this[int index]
        {
            get => AsList[index];
            set => AsList[index] = value ?? Null;
        }

        public JsonValue this[string key]
        {
            get => Kind == JsonKind.Object && AsMap.TryGetValue(key, out var v) ? v : Null;
            set => AsMap[key] = value ?? Null;
        }

        public bool Has(string key) => Kind == JsonKind.Object && AsMap.ContainsKey(key);

        public JsonValue Add(JsonValue item) { AsList.Add(item ?? Null); return this; }

        public JsonValue Set(string key, object value) { AsMap[key] = From(value); return this; }

        public bool Remove(string key) => Kind == JsonKind.Object && AsMap.Remove(key);

        public IEnumerator<JsonValue> GetEnumerator() =>
            Kind == JsonKind.Array ? AsList.GetEnumerator() : Enumerable_Empty();

        private static IEnumerator<JsonValue> Enumerable_Empty() { yield break; }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ---------------------------------------------------------------- coercion

        public string AsString(string fallback = null)
        {
            switch (Kind)
            {
                case JsonKind.String: return (string)_value;
                case JsonKind.Number: return ((double)_value).ToString("R", CultureInfo.InvariantCulture);
                case JsonKind.Bool: return ((bool)_value) ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsDouble(double fallback = 0)
        {
            switch (Kind)
            {
                case JsonKind.Number: return (double)_value;
                case JsonKind.Bool: return ((bool)_value) ? 1 : 0;
                case JsonKind.String:
                    return double.TryParse((string)_value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
                default: return fallback;
            }
        }

        public int AsInt(int fallback = 0)
        {
            var d = AsDouble(double.NaN);
            return double.IsNaN(d) ? fallback : (int)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        public bool AsBool(bool fallback = false)
        {
            switch (Kind)
            {
                case JsonKind.Bool: return (bool)_value;
                case JsonKind.Number: return Math.Abs((double)_value) > double.Epsilon;
                case JsonKind.String:
                    var s = (string)_value;
                    if (bool.TryParse(s, out var b)) return b;
                    return s == "1" || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
                default: return fallback;
            }
        }

        public T AsEnum<T>(T fallback) where T : struct
        {
            var s = AsString();
            if (s != null && Enum.TryParse<T>(s, ignoreCase: true, out var v)) return v;
            return fallback;
        }

        // ---------------------------------------------------------------- serialization

        public static JsonValue Parse(string text) => new JsonParser(text).ParseDocument();

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (Exception ex) { value = Null; error = ex.Message; return false; }
        }

        public override string ToString() => ToJson(indent: false);

        public string ToJson(bool indent = false)
        {
            var sb = new StringBuilder(256);
            Write(sb, indent ? 0 : -1);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, int depth)
        {
            bool pretty = depth >= 0;
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); return;
                case JsonKind.Bool: sb.Append(((bool)_value) ? "true" : "false"); return;
                case JsonKind.Number: WriteNumber(sb, (double)_value); return;
                case JsonKind.String: WriteString(sb, (string)_value); return;

                case JsonKind.Array:
                {
                    var list = AsList;
                    if (list.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (pretty) Indent(sb, depth + 1);
                        list[i].Write(sb, pretty ? depth + 1 : -1);
                    }
                    if (pretty) Indent(sb, depth);
                    sb.Append(']');
                    return;
                }

                case JsonKind.Object:
                {
                    var map = AsMap;
                    if (map.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) Indent(sb, depth + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        if (pretty) sb.Append(' ');
                        kv.Value.Write(sb, pretty ? depth + 1 : -1);
                    }
                    if (pretty) Indent(sb, depth);
                    sb.Append('}');
                    return;
                }
            }
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
