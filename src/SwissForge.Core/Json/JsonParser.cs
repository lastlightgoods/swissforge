using System;
using System.Globalization;
using System.Text;

namespace SwissForge.Core.Json
{
    /// <summary>
    /// Strict-enough recursive-descent JSON reader. Accepts standard JSON plus two
    /// conveniences that matter for hand-edited machinist config files:
    /// <c>//</c> and <c>/* */</c> comments, and a trailing comma in arrays/objects.
    /// </summary>
    internal sealed class JsonParser
    {
        private readonly string _s;
        private int _i;

        public JsonParser(string text)
        {
            _s = text ?? throw new ArgumentNullException(nameof(text));
            _i = 0;
        }

        public JsonValue ParseDocument()
        {
            SkipTrivia();
            var v = ParseValue();
            SkipTrivia();
            if (_i < _s.Length) throw Error("unexpected trailing content");
            return v;
        }

        private JsonValue ParseValue()
        {
            SkipTrivia();
            if (_i >= _s.Length) throw Error("unexpected end of input");

            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return JsonValue.Str(ParseString());
                case 't': Expect("true"); return JsonValue.Bool(true);
                case 'f': Expect("false"); return JsonValue.Bool(false);
                case 'n': Expect("null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return JsonValue.Num(ParseNumber());
                    throw Error("unexpected character '" + c + "'");
            }
        }

        private JsonValue ParseObject()
        {
            var obj = JsonValue.Obj();
            _i++; // '{'
            SkipTrivia();
            if (Peek() == '}') { _i++; return obj; }

            while (true)
            {
                SkipTrivia();
                if (Peek() == '}') { _i++; return obj; }   // trailing comma
                if (Peek() != '"') throw Error("expected property name");
                var key = ParseString();
                SkipTrivia();
                if (Peek() != ':') throw Error("expected ':' after property name");
                _i++;
                obj[key] = ParseValue();
                SkipTrivia();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == '}') { _i++; return obj; }
                throw Error("expected ',' or '}' in object");
            }
        }

        private JsonValue ParseArray()
        {
            var arr = JsonValue.Arr();
            _i++; // '['
            SkipTrivia();
            if (Peek() == ']') { _i++; return arr; }

            while (true)
            {
                SkipTrivia();
                if (Peek() == ']') { _i++; return arr; }   // trailing comma
                arr.Add(ParseValue());
                SkipTrivia();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == ']') { _i++; return arr; }
                throw Error("expected ',' or ']' in array");
            }
        }

        private string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Error("unterminated string");
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (_i >= _s.Length) throw Error("unterminated escape");
                char e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length) throw Error("truncated \\u escape");
                        var hex = _s.Substring(_i, 4);
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                            throw Error("bad \\u escape '" + hex + "'");
                        _i += 4;
                        sb.Append((char)cp);
                        break;
                    default: throw Error("unknown escape '\\" + e + "'");
                }
            }
        }

        private double ParseNumber()
        {
            int start = _i;
            if (Peek() == '-') _i++;
            while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            if (Peek() == '.')
            {
                _i++;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                _i++;
                if (Peek() == '+' || Peek() == '-') _i++;
                while (_i < _s.Length && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }

            var slice = _s.Substring(start, _i - start);
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw Error("bad number '" + slice + "'");
            return d;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void Expect(string literal)
        {
            if (_i + literal.Length > _s.Length || string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                throw Error("expected '" + literal + "'");
            _i += literal.Length;
        }

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _i++; continue; }

                if (c == '/' && _i + 1 < _s.Length)
                {
                    if (_s[_i + 1] == '/')
                    {
                        _i += 2;
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        continue;
                    }
                    if (_s[_i + 1] == '*')
                    {
                        _i += 2;
                        while (_i + 1 < _s.Length && !(_s[_i] == '*' && _s[_i + 1] == '/')) _i++;
                        _i = Math.Min(_i + 2, _s.Length);
                        continue;
                    }
                }
                return;
            }
        }

        private FormatException Error(string message)
        {
            int line = 1, col = 1;
            for (int k = 0; k < _i && k < _s.Length; k++)
            {
                if (_s[k] == '\n') { line++; col = 1; } else col++;
            }
            return new FormatException($"JSON error at line {line}, column {col}: {message}");
        }
    }
}
