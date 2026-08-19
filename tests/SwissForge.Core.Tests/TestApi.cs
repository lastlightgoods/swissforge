using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using SwissForge.Core.Api;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Tests
{
    public static class TestApi
    {
        private static HttpServer _server;
        private static int _port;
        private const string Token = "test-token-9f3a";

        /// <summary>Makes a real HTTP request over a real socket. No mocking of the transport.</summary>
        private static (int status, string body) Request(string method, string path, string body = null,
                                                         string token = Token)
        {
            using (var client = new TcpClient("127.0.0.1", _port))
            using (var stream = client.GetStream())
            {
                var sb = new StringBuilder();
                sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
                sb.Append("Host: 127.0.0.1\r\n");
                if (token != null) sb.Append("Authorization: Bearer ").Append(token).Append("\r\n");

                var payload = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                if (body != null)
                {
                    sb.Append("Content-Type: application/json\r\n");
                    sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");

                var head = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(head, 0, head.Length);
                if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
                stream.Flush();

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    var headers = split >= 0 ? text.Substring(0, split) : text;
                    var content = split >= 0 ? text.Substring(split + 4) : "";

                    int status = 0;
                    var firstLine = headers.Split('\n')[0];
                    var parts = firstLine.Split(' ');
                    if (parts.Length > 1) int.TryParse(parts[1], out status);

                    return (status, content);
                }
            }
        }

        public static void Run()
        {
            Check.Suite("REST API");

            var ctx = new ApiContext { Version = "1.0.0-test" };
            var api = new SwissForgeApi(ctx);

            _server = new HttpServer { AuthToken = Token, Handler = api.Handle };
            _server.Start(0);
            _port = _server.Port;

            try
            {
                // --- health needs no token
                var (hs, hb) = Request("GET", "/health", token: null);
                Check.Equal(200, hs, "health responds without a token");
                Check.Contains(hb, "\"status\"", "health returns JSON");

                // --- everything else does
                var (us, _) = Request("GET", "/api/v1/status", token: null);
                Check.Equal(401, us, "a request with no token is rejected");

                var (ws, _) = Request("GET", "/api/v1/status", token: "wrong-token");
                Check.Equal(401, ws, "a request with the wrong token is rejected");

                // --- status
                var (ss, sbody) = Request("GET", "/api/v1/status");
                Check.Equal(200, ss, "status responds with a valid token");
                var status = JsonValue.Parse(sbody);
                Check.Equal("1.0.0-test", status["version"].AsString(), "status reports the version");
                Check.False(status["esprit"]["connected"].AsBool(), "status is honest that ESPRIT is not attached");
                Check.Greater(status["materialCount"].AsInt(), 20, "the material library is loaded");

                // --- openapi
                var (os, ob) = Request("GET", "/openapi.json");
                Check.Equal(200, os, "the OpenAPI document is served");
                var spec = JsonValue.Parse(ob);
                Check.Equal("3.0.3", spec["openapi"].AsString(), "it is a valid OpenAPI 3 document");
                Check.True(spec["paths"].Has("/api/v1/quote"), "the spec lists the quote endpoint");
                Check.True(spec["paths"].Has("/api/v1/gcode/lint"), "the spec lists the lint endpoint");

                // --- materials
                var (ms, mb) = Request("GET", "/api/v1/materials");
                Check.Equal(200, ms, "materials list responds");
                Check.Greater(JsonValue.Parse(mb)["count"].AsInt(), 20, "materials are returned");

                var (m1s, m1b) = Request("GET", "/api/v1/materials/303");
                Check.Equal(200, m1s, "a fuzzy material lookup resolves");
                Check.Contains(JsonValue.Parse(m1b)["name"].AsString(), "303", "the right material comes back");

                var (m404, _) = Request("GET", "/api/v1/materials/MITHRIL");
                Check.Equal(404, m404, "an unknown material returns 404");

                // --- feeds and speeds
                var fsBody = @"{
                    ""material"": ""12L14"",
                    ""tool"": ""T01"",
                    ""operation"": ""TurnRough"",
                    ""workDiameterMm"": 12,
                    ""radialStockMm"": 1.0,
                    ""cutLengthMm"": 20
                }";
                var (fs, fb) = Request("POST", "/api/v1/feeds-speeds", fsBody);
                Check.Equal(200, fs, "feeds-speeds responds");
                var feeds = JsonValue.Parse(fb);
                Check.Greater(feeds["rpm"].AsDouble(), 0, "a spindle speed comes back");
                Check.Greater(feeds["feedMmPerRev"].AsDouble(), 0, "a feed comes back");
                Check.Greater(feeds["surfaceSpeedSfm"].AsDouble(), 0, "imperial units are provided alongside metric");

                var (fbad, _) = Request("POST", "/api/v1/feeds-speeds", @"{""material"":""NOPE"",""tool"":""T01""}");
                Check.Equal(400, fbad, "an unknown material is a 400, not a crash");

                var (fmethod, _) = Request("GET", "/api/v1/feeds-speeds");
                Check.Equal(405, fmethod, "GET on a POST endpoint returns 405");

                var (fempty, _) = Request("POST", "/api/v1/feeds-speeds");
                Check.Equal(400, fempty, "an empty body returns 400");

                var (fjson, fjb) = Request("POST", "/api/v1/feeds-speeds", "{not json");
                Check.Equal(400, fjson, "malformed JSON returns 400");
                Check.Contains(fjb, "bad_json", "the error names the problem");

                // --- cycle time
                var ctBody = @"{
                    ""operations"": [
                        { ""id"":""A1"", ""channel"":0, ""sequence"":1, ""cutSeconds"":10 },
                        { ""id"":""B1"", ""channel"":1, ""sequence"":1, ""cutSeconds"":6 }
                    ],
                    ""recomputeTimes"": false
                }";
                var (cs, cb) = Request("POST", "/api/v1/cycle-time", ctBody);
                Check.Equal(200, cs, "cycle-time responds");
                var cycle = JsonValue.Parse(cb);
                Check.Near(10, cycle["cycleSeconds"].AsDouble(), 1e-6, "concurrent channels give the longer time");
                Check.Greater(cycle["partsPerHour"].AsDouble(), 0, "parts per hour is returned");

                // --- G-code lint
                var lintBody = JsonValue.Obj()
                    .Set("gcode", "O1\nG21 G90\nS4000 M3\nG96 S250\nG0 X10.5\nG1 X-0.2\nM30")
                    .ToJson();
                var (ls, lb) = Request("POST", "/api/v1/gcode/lint", lintBody);
                Check.Equal(200, ls, "gcode/lint responds");
                var lint = JsonValue.Parse(lb);
                Check.Greater(lint["critical"].AsInt(), 0, "the linter finds the unclamped G96");
                Check.Contains(lb, "CSS_NO_CLAMP", "the specific rule is named in the response");
                Check.False(lint["isClean"].AsBool(), "the program is not reported clean");

                // --- G-code analyze
                var anBody = JsonValue.Obj()
                    .Set("gcode", "$1\nS1 M3\n!2L20\nG1 X1 F1\nM30\n$2\nS1 M3\n!1L20\nG1 Z1 F1\nM30")
                    .Set("dialect", "CitizenCincom")
                    .ToJson();
                var (as_, ab) = Request("POST", "/api/v1/gcode/analyze", anBody);
                Check.Equal(200, as_, "gcode/analyze responds");
                var analysis = JsonValue.Parse(ab);
                Check.Equal(2, analysis["channels"].Count, "both channels are detected");
                Check.Equal(1, analysis["waitcodes"].Count, "the shared waitcode is reported once");
                Check.True(analysis["waitcodes"][0]["paired"].AsBool(), "the waitcode is recognised as paired");

                // --- plan
                var planBody = @"{
                  ""part"": {
                    ""partNumber"": ""API-1"",
                    ""overallLengthMm"": 25,
                    ""maxDiameterMm"": 9.5,
                    ""cutoffWidthMm"": 1.5,
                    ""quantity"": 1000,
                    ""stock"": { ""materialId"": ""12L14"", ""diameterMm"": 10, ""lengthMm"": 3660, ""costPerBar"": 18 },
                    ""features"": [
                      { ""id"":""f1"", ""kind"":""TurnRough"", ""side"":""Main"",
                        ""startDiameterMm"":10, ""diameterMm"":9.5, ""lengthMm"":12 }
                    ]
                  }
                }";
                var (ps, pb) = Request("POST", "/api/v1/plan", planBody);
                Check.Equal(200, ps, "plan responds");
                var plan = JsonValue.Parse(pb);
                Check.Equal("API-1", plan["partNumber"].AsString(), "the plan names the part");
                Check.Greater(plan["operationCount"].AsInt(), 2, "operations are planned");
                Check.Greater(plan["cycle"]["cycleSeconds"].AsDouble(), 0, "the plan is timed");

                // --- quote, deriving its own cycle time from the plan
                var (qs, qb) = Request("POST", "/api/v1/quote", planBody);
                Check.Equal(200, qs, "quote responds");
                var quote = JsonValue.Parse(qb);
                Check.Greater(quote["cycleSeconds"].AsDouble(), 0, "the quote planned the job to get a cycle time");
                Check.Greater(quote["primary"]["pricePerPart"].AsDouble(), 0, "a price comes back");
                Check.Greater(quote["partsPerBar"].AsInt(), 0, "bar yield is reported");

                // --- ESPRIT endpoints degrade honestly when not hosted in ESPRIT
                var (es, eb) = Request("GET", "/api/v1/esprit/tools");
                Check.Equal(503, es, "an ESPRIT endpoint returns 503 when no document is attached");
                Check.Contains(eb, "esprit_unavailable", "the error explains why");
                Check.Contains(eb, "work without it", "the error points at what still works");

                var (eps, _) = Request("GET", "/api/v1/esprit/probe");
                Check.Equal(200, eps, "the probe endpoint answers even offline");

                // --- unknown routes
                var (ns, _) = Request("GET", "/api/v1/nonsense");
                Check.Equal(404, ns, "an unknown route returns 404");

                var (rs, rb) = Request("GET", "/nonsense");
                Check.Equal(404, rs, "a route outside /api/v1 returns 404");
                Check.Contains(rb, "openapi.json", "the 404 points at the API description");

                // --- the server survives a malformed request
                using (var raw = new TcpClient("127.0.0.1", _port))
                {
                    var junk = Encoding.ASCII.GetBytes("GARBAGE\r\n\r\n");
                    raw.GetStream().Write(junk, 0, junk.Length);
                }
                var (afterJunk, _) = Request("GET", "/health", token: null);
                Check.Equal(200, afterJunk, "the server still answers after a malformed request");
            }
            finally
            {
                _server.Stop();
            }

            Check.False(_server.IsRunning, "the server stops cleanly");
        }
    }
}
