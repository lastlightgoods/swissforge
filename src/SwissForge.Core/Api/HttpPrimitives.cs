using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwissForge.Core.Api
{
    /// <summary>A parsed HTTP request.</summary>
    public sealed class HttpRequest
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public string RawTarget { get; set; } = "/";
        public Dictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Query { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; } = "";

        /// <summary>Path split on '/', empty segments removed. Used for routing.</summary>
        public string[] Segments { get; set; } = Array.Empty<string>();

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        public string QueryValue(string name, string fallback = null) =>
            Query.TryGetValue(name, out var v) ? v : fallback;

        public double QueryDouble(string name, double fallback)
        {
            var s = QueryValue(name);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }
    }

    /// <summary>A response ready to write to the socket.</summary>
    public sealed class HttpResponse
    {
        public int StatusCode { get; set; } = 200;
        public string StatusText { get; set; } = "OK";
        public string ContentType { get; set; } = "application/json; charset=utf-8";
        public string Body { get; set; } = "";
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        public static HttpResponse Json(string json, int status = 200) => new HttpResponse
        {
            StatusCode = status,
            StatusText = StatusTextFor(status),
            ContentType = "application/json; charset=utf-8",
            Body = json
        };

        public static HttpResponse Text(string text, int status = 200) => new HttpResponse
        {
            StatusCode = status,
            StatusText = StatusTextFor(status),
            ContentType = "text/plain; charset=utf-8",
            Body = text
        };

        public static HttpResponse Error(int status, string code, string message)
        {
            var j = SwissForge.Core.Json.JsonValue.Obj();
            j.Set("error", code);
            j.Set("message", message);
            j.Set("status", status);
            return Json(j.ToJson(indent: true), status);
        }

        private static string StatusTextFor(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status " + status;
            }
        }

        public byte[] ToBytes()
        {
            var bodyBytes = Encoding.UTF8.GetBytes(Body ?? "");
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(StatusCode).Append(' ').Append(StatusText).Append("\r\n");
            head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("Cache-Control: no-store\r\n");
            foreach (var kv in Headers)
                head.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            var all = new byte[headBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headBytes, 0, all, 0, headBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, all, headBytes.Length, bodyBytes.Length);
            return all;
        }
    }

    /// <summary>Percent-decoding and query-string parsing, without a web framework.</summary>
    internal static class UrlCodec
    {
        public static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.IndexOf('%') < 0 && s.IndexOf('+') < 0)) return s;

            var bytes = new List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+') { bytes.Add((byte)' '); continue; }
                if (c == '%' && i + 2 < s.Length &&
                    int.TryParse(s.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                {
                    bytes.Add((byte)v);
                    i += 2;
                    continue;
                }
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static void ParseQuery(string query, Dictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(query)) return;
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0) into[Decode(pair)] = "";
                else into[Decode(pair.Substring(0, eq))] = Decode(pair.Substring(eq + 1));
            }
        }
    }
}
