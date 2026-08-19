using SwissForge.Core.Json;

namespace SwissForge.Tests
{
    public static class TestJson
    {
        public static void Run()
        {
            Check.Suite("JSON");

            var j = JsonValue.Parse(@"{
                // a comment, because machinists edit these by hand
                ""name"": ""12L14"",
                ""vc"": 200.5,
                ""tags"": [""free"", ""leaded""],
                ""nested"": { ""deep"": { ""value"": -3.5e2 } },
                ""ok"": true,
                ""nope"": null,
            }");

            Check.Equal("12L14", j["name"].AsString(), "reads a string property");
            Check.Near(200.5, j["vc"].AsDouble(), 1e-9, "reads a number");
            Check.Equal(2, j["tags"].Count, "reads an array length");
            Check.Equal("leaded", j["tags"][1].AsString(), "indexes into an array");
            Check.Near(-350, j["nested"]["deep"]["value"].AsDouble(), 1e-9, "reads exponent notation through nesting");
            Check.True(j["ok"].AsBool(), "reads a boolean");
            Check.True(j["nope"].IsNull, "reads null");
            Check.True(j["absent"].IsNull, "a missing key reads as null rather than throwing");
            Check.True(j["absent"]["deeper"].IsNull, "missing keys chain safely");
            Check.Near(42, j["absent"].AsDouble(42), 1e-9, "a missing key falls back to the supplied default");

            // round trip
            var text = j.ToJson(indent: true);
            var again = JsonValue.Parse(text);
            Check.Equal("12L14", again["name"].AsString(), "survives a pretty-printed round trip");
            Check.Near(-350, again["nested"]["deep"]["value"].AsDouble(), 1e-9, "numbers survive a round trip");

            // escapes
            var esc = JsonValue.Parse("{\"s\":\"line1\\nline2\\t\\\"quoted\\\"\\u0041\"}");
            Check.Equal("line1\nline2\t\"quoted\"A", esc["s"].AsString(), "decodes escapes including \\u");
            var reEsc = JsonValue.Parse(esc.ToJson())["s"].AsString();
            Check.Equal("line1\nline2\t\"quoted\"A", reEsc, "re-encodes escapes losslessly");

            // building
            var built = JsonValue.Obj();
            built.Set("a", 1).Set("b", "two").Set("c", true);
            built["list"] = JsonValue.Arr();
            built["list"].Add(JsonValue.Num(1)).Add(JsonValue.Num(2));
            Check.Equal("{\"a\":1,\"b\":\"two\",\"c\":true,\"list\":[1,2]}", built.ToJson(), "builds compact JSON");

            // errors
            Check.Throws<System.FormatException>(() => JsonValue.Parse("{\"a\":}"), "rejects a malformed value");
            Check.Throws<System.FormatException>(() => JsonValue.Parse("{\"a\":1"), "rejects an unterminated object");
            Check.Throws<System.FormatException>(() => JsonValue.Parse("[1,2] extra"), "rejects trailing content");

            Check.False(JsonValue.TryParse("nonsense", out _, out var err), "TryParse returns false instead of throwing");
            Check.Contains(err, "JSON error", "TryParse reports where the parse failed");
        }
    }
}
