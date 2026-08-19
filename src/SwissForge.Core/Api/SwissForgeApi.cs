using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.CycleTime;
using SwissForge.Core.Esprit;
using SwissForge.Core.Feeds;
using SwissForge.Core.GCode;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Quoting;
using SwissForge.Core.Templates;
using SwissForge.Core.Units;

namespace SwissForge.Core.Api
{
    /// <summary>Everything the API needs to answer a request.</summary>
    public sealed class ApiContext
    {
        public MaterialDatabase Materials { get; set; } = MaterialDatabase.CreateDefault();
        public ToolLibrary Tools { get; set; } = ToolLibrary.CreateStarter();
        public MachineProfile Machine { get; set; } = new MachineProfile();
        public SetupTemplate Template { get; set; } = new SetupTemplate();
        public QuotePolicy QuotePolicy { get; set; } = new QuotePolicy();
        public IEspritGateway Esprit { get; set; } = new OfflineEspritGateway();
        public string Version { get; set; } = "1.0.0";
    }

    /// <summary>
    /// The SwissForge REST surface.
    /// <para>
    /// This is the answer to "the plugin should have an API": rather than locking the
    /// engines inside a CAM dialog, the add-in hosts them on loopback so anything else in
    /// the shop can ask questions. An ERP can pull a cycle time, a quoting spreadsheet can
    /// post a part spec and get a price back, a shop-floor tablet can lint a program before
    /// it is sent to the machine, and a scripting agent can drive the whole thing.
    /// </para>
    /// <para>
    /// Every endpoint that touches the CAM document works only when the API is hosted inside
    /// ESPRIT. The engine endpoints work anywhere, including from the command line with no
    /// CAM system installed at all.
    /// </para>
    /// </summary>
    public sealed class SwissForgeApi
    {
        private readonly ApiContext _ctx;
        private readonly FeedSpeedEngine _feeds = new FeedSpeedEngine();
        private readonly CycleTimeEstimator _timer = new CycleTimeEstimator();
        private readonly QuoteEngine _quotes = new QuoteEngine();
        private readonly JobPlanner _planner = new JobPlanner();
        private readonly GParser _parser = new GParser();

        public SwissForgeApi(ApiContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
        }

        public HttpResponse Handle(HttpRequest req)
        {
            var s = req.Segments;

            // --- unauthenticated liveness probe
            if (req.Path == "/health")
                return Ok(JsonValue.Obj().Set("status", "ok").Set("version", _ctx.Version));

            if (req.Path == "/openapi.json")
                return Ok(OpenApi.Document(_ctx.Version));

            if (s.Length < 2 || s[0] != "api" || s[1] != "v1")
                return HttpResponse.Error(404, "not_found",
                    $"No route for {req.Method} {req.Path}. The API lives under /api/v1; " +
                    "see /openapi.json for the full list.");

            var route = string.Join("/", s.Skip(2));

            try
            {
                switch (route)
                {
                    case "status": return Get(req, Status);
                    case "materials": return Get(req, Materials);
                    case "tools": return Get(req, Tools);
                    case "machine": return Get(req, MachineInfo);
                    case "feeds-speeds": return Post(req, FeedsSpeeds);
                    case "cycle-time": return Post(req, CycleTimeRoute);
                    case "plan": return Post(req, PlanRoute);
                    case "quote": return Post(req, QuoteRoute);
                    case "gcode/lint": return Post(req, GCodeLint);
                    case "gcode/analyze": return Post(req, GCodeAnalyze);
                    case "esprit/probe": return Get(req, EspritProbe);
                    case "esprit/tools": return Get(req, EspritTools);
                    case "esprit/operations": return Get(req, EspritOperations);
                    case "esprit/stock": return Get(req, EspritStock);
                    case "esprit/cutting-data": return Post(req, EspritApplyCuttingData);
                    case "esprit/post": return Post(req, EspritPost);
                }

                if (s.Length == 4 && route.StartsWith("materials/"))
                    return MaterialById(s[3]);
            }
            catch (FormatException ex)
            {
                return HttpResponse.Error(400, "bad_json", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return HttpResponse.Error(400, "bad_request", ex.Message);
            }

            return HttpResponse.Error(404, "not_found", $"No route for {req.Method} /api/v1/{route}.");
        }

        // ------------------------------------------------------------------ plumbing

        private static HttpResponse Ok(JsonValue j) => HttpResponse.Json(j.ToJson(indent: true));

        private static HttpResponse Get(HttpRequest req, Func<HttpRequest, HttpResponse> handler) =>
            req.Method == "GET" ? handler(req)
                                : HttpResponse.Error(405, "method_not_allowed", "Use GET for this endpoint.");

        private static HttpResponse Post(HttpRequest req, Func<HttpRequest, JsonValue, HttpResponse> handler)
        {
            if (req.Method != "POST")
                return HttpResponse.Error(405, "method_not_allowed", "Use POST for this endpoint.");
            if (string.IsNullOrWhiteSpace(req.Body))
                return HttpResponse.Error(400, "empty_body", "This endpoint needs a JSON body.");

            var body = JsonValue.Parse(req.Body);
            return handler(req, body);
        }

        // ------------------------------------------------------------------ read endpoints

        private HttpResponse Status(HttpRequest req)
        {
            var connection = _ctx.Esprit?.Connect() ?? new EspritConnectionInfo();
            var j = JsonValue.Obj();
            j.Set("version", _ctx.Version);
            j.Set("engineAvailable", true);
            j["esprit"] = connection.ToJson();
            j.Set("materialCount", _ctx.Materials.Count);
            j.Set("toolCount", _ctx.Tools.Count);
            j.Set("machine", _ctx.Machine.Name);
            return Ok(j);
        }

        private HttpResponse Materials(HttpRequest req)
        {
            var group = req.QueryValue("group");
            var arr = JsonValue.Arr();

            foreach (var m in _ctx.Materials.All.OrderBy(x => x.Id))
            {
                if (!string.IsNullOrEmpty(group) &&
                    !m.Group.ToString().Equals(group, StringComparison.OrdinalIgnoreCase)) continue;

                var mj = m.ToJson();
                mj.Set("costIsIndicative", _ctx.Materials.CostIsIndicative(m.Id));
                arr.Add(mj);
            }

            var j = JsonValue.Obj();
            j.Set("count", arr.Count);
            j["materials"] = arr;
            return Ok(j);
        }

        private HttpResponse MaterialById(string id)
        {
            var m = _ctx.Materials.Find(id);
            if (m == null)
                return HttpResponse.Error(404, "material_not_found",
                    $"No material matching '{id}'. GET /api/v1/materials lists what is loaded.");

            var mj = m.ToJson();
            mj.Set("costIsIndicative", _ctx.Materials.CostIsIndicative(m.Id));
            return Ok(mj);
        }

        private HttpResponse Tools(HttpRequest req) => Ok(_ctx.Tools.ToJson());

        private HttpResponse MachineInfo(HttpRequest req) => Ok(_ctx.Machine.ToJson());

        // ------------------------------------------------------------------ engine endpoints

        private HttpResponse FeedsSpeeds(HttpRequest req, JsonValue body)
        {
            var material = _ctx.Materials.Find(body["material"].AsString(""));
            if (material == null)
                return HttpResponse.Error(400, "material_not_found",
                    $"Material '{body["material"].AsString("")}' is not loaded.");

            ToolItem tool;
            if (body["tool"].Kind == JsonKind.Object) tool = ToolItem.FromJson(body["tool"]);
            else
            {
                tool = _ctx.Tools.Get(body["tool"].AsString(""));
                if (tool == null)
                    return HttpResponse.Error(400, "tool_not_found",
                        $"Tool '{body["tool"].AsString("")}' is not in the library. Send a full tool " +
                        "object instead of an id to use one that is not loaded.");
            }

            var request = new FeedSpeedRequest
            {
                Material = material,
                Tool = tool,
                Machine = body["machine"].Kind == JsonKind.Object
                    ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine,
                Operation = body["operation"].AsEnum(OperationType.TurnRough),
                Side = body["side"].AsEnum(SpindleSide.Main),
                WorkDiameterMm = body["workDiameterMm"].AsDouble(0),
                RadialStockMm = body["radialStockMm"].AsDouble(0),
                CutLengthMm = body["cutLengthMm"].AsDouble(0),
                TargetRaMicron = body["targetRaMicron"].AsDouble(0),
                ToleranceMm = body["toleranceMm"].AsDouble(0.05),
                UnsupportedLengthMm = body["unsupportedLengthMm"].AsDouble(0),
                ThreadPitchMm = body["threadPitchMm"].AsDouble(0),
                GuideBushingEngaged = body["guideBushingEngaged"].AsBool(true)
            };

            var rec = _feeds.Recommend(request);
            var j = rec.ToJson();
            j.Set("material", material.Id);
            j.Set("tool", tool.Id);
            j.Set("operation", request.Operation.ToString());
            return Ok(j);
        }

        private HttpResponse CycleTimeRoute(HttpRequest req, JsonValue body)
        {
            var machine = body["machine"].Kind == JsonKind.Object
                ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine;

            var ops = new List<OperationSpec>();
            foreach (var o in body["operations"]) ops.Add(OperationSpec.FromJson(o));

            if (ops.Count == 0)
                return HttpResponse.Error(400, "no_operations",
                    "Send an 'operations' array. Each entry needs at least a channel, a sequence, " +
                    "and either a duration or enough cutting data to derive one.");

            if (body["recomputeTimes"].AsBool(true))
                _timer.ComputeAllOperationTimes(ops, machine);

            var result = _timer.Schedule(ops, machine);
            var j = result.ToJson();
            j.Set("partsPerDay", Math.Round(result.PartsPerDay(machine), 1));
            return Ok(j);
        }

        private HttpResponse PlanRoute(HttpRequest req, JsonValue body)
        {
            if (body["part"].IsNull)
                return HttpResponse.Error(400, "no_part", "Send a 'part' object. See /openapi.json.");

            var part = PartSpec.FromJson(body["part"]);
            var machine = body["machine"].Kind == JsonKind.Object
                ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine;
            var tools = body["tools"].Count > 0 ? ToolLibrary.FromJson(body["tools"]) : _ctx.Tools;
            var template = body["template"].Kind == JsonKind.Object
                ? SetupTemplate.FromJson(body["template"]) : _ctx.Template;

            var plan = _planner.Plan(part, tools, machine, _ctx.Materials, template);
            return Ok(plan.ToJson(includeSchedule: body["includeSchedule"].AsBool(true)));
        }

        private HttpResponse QuoteRoute(HttpRequest req, JsonValue body)
        {
            if (body["part"].IsNull)
                return HttpResponse.Error(400, "no_part", "Send a 'part' object.");

            var part = PartSpec.FromJson(body["part"]);
            var machine = body["machine"].Kind == JsonKind.Object
                ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine;
            var policy = body["policy"].Kind == JsonKind.Object
                ? PolicyFromJson(body["policy"]) : _ctx.QuotePolicy;

            double cycleSeconds = body["cycleSeconds"].AsDouble(0);
            var tools = new List<ToolItem>();

            // If no cycle time was supplied, plan the job to get one. This is the common case:
            // a caller sends a part and expects a price, not a two-step dance.
            if (cycleSeconds <= 0)
            {
                var toolLib = body["tools"].Count > 0 ? ToolLibrary.FromJson(body["tools"]) : _ctx.Tools;
                var plan = _planner.Plan(part, toolLib, machine, _ctx.Materials,
                                         body["template"].Kind == JsonKind.Object
                                             ? SetupTemplate.FromJson(body["template"]) : _ctx.Template);
                cycleSeconds = plan.Cycle?.CycleSeconds ?? 0;
                tools.AddRange(plan.ToolsUsed);
            }
            else
            {
                foreach (var t in body["tools"]) tools.Add(ToolItem.FromJson(t));
            }

            var breaks = new List<int>();
            foreach (var b in body["quantityBreaks"]) breaks.Add(b.AsInt(0));

            var quote = _quotes.Quote(new QuoteRequest
            {
                Part = part,
                Machine = machine,
                Materials = _ctx.Materials,
                Policy = policy,
                CycleSeconds = cycleSeconds,
                Tools = tools,
                QuantityBreaks = breaks
            });

            return Ok(quote.ToJson());
        }

        private static QuotePolicy PolicyFromJson(JsonValue j) => new QuotePolicy
        {
            SetupRatePerHour = j["setupRatePerHour"].AsDouble(85),
            ProgrammingRatePerHour = j["programmingRatePerHour"].AsDouble(95),
            SetupHours = j["setupHours"].AsDouble(4),
            ProgrammingHours = j["programmingHours"].AsDouble(2),
            FirstArticleCost = j["firstArticleCost"].AsDouble(0),
            TargetMarginFraction = j["targetMarginFraction"].AsDouble(0.35),
            MachineOverheadFactor = j["machineOverheadFactor"].AsDouble(1.0),
            PackagingPerPart = j["packagingPerPart"].AsDouble(0),
            Currency = j["currency"].AsString("USD"),
            AllowIndicativeMaterialCost = j["allowIndicativeMaterialCost"].AsBool(false)
        };

        // ------------------------------------------------------------------ G-code endpoints

        private HttpResponse GCodeLint(HttpRequest req, JsonValue body)
        {
            var text = body["gcode"].AsString("");
            if (string.IsNullOrWhiteSpace(text))
                return HttpResponse.Error(400, "no_gcode", "Send the NC text in a 'gcode' string field.");

            var machine = body["machine"].Kind == JsonKind.Object
                ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine;
            var control = ControlProfile.For(body["dialect"].IsNull
                ? machine.Dialect
                : body["dialect"].AsEnum(machine.Dialect));

            var program = _parser.Parse(text, body["name"].AsString(""));
            program.SplitChannels(control);

            var linter = new GCodeLinter
            {
                Machine = machine,
                Control = control,
                ReportBlockDeletes = body["reportBlockDeletes"].AsBool(false)
            };

            var report = linter.Lint(program);
            var j = report.ToJson();
            j.Set("dialect", control.Dialect.ToString());
            j.Set("channelCount", program.Channels.Count);
            return Ok(j);
        }

        private HttpResponse GCodeAnalyze(HttpRequest req, JsonValue body)
        {
            var text = body["gcode"].AsString("");
            if (string.IsNullOrWhiteSpace(text))
                return HttpResponse.Error(400, "no_gcode", "Send the NC text in a 'gcode' string field.");

            var machine = body["machine"].Kind == JsonKind.Object
                ? MachineProfile.FromJson(body["machine"]) : _ctx.Machine;
            var control = ControlProfile.For(body["dialect"].IsNull
                ? machine.Dialect
                : body["dialect"].AsEnum(machine.Dialect));

            var program = _parser.Parse(text, body["name"].AsString(""));
            program.SplitChannels(control);

            var j = program.ToJson(includeBlocks: body["includeBlocks"].AsBool(false));

            var waits = JsonValue.Arr();
            foreach (var group in program.Blocks
                        .Where(b => !string.IsNullOrEmpty(b.WaitLabel))
                        .GroupBy(b => b.WaitLabel, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var wj = JsonValue.Obj();
                wj.Set("label", group.Key);
                wj.Set("channels", group.Select(b => b.Channel).Distinct().OrderBy(c => c).ToList());
                wj.Set("occurrences", group.Count());
                wj.Set("lines", group.Select(b => b.LineNumber).ToList());
                wj.Set("paired", group.Select(b => b.Channel).Distinct().Count() >= 2);
                waits.Add(wj);
            }
            j["waitcodes"] = waits;

            var perChannel = JsonValue.Arr();
            foreach (var ch in program.Channels)
            {
                var blocks = program.BlocksIn(ch).ToList();
                var cj = JsonValue.Obj();
                cj.Set("channel", ch);
                cj.Set("blockCount", blocks.Count(b => !b.IsEmpty));
                cj.Set("toolCalls", blocks.Count(b => b.Has('T')));
                cj.Set("feedMoves", blocks.Count(b => b.HasG(1) || b.HasG(2) || b.HasG(3)));
                cj.Set("waitcodes", blocks.Count(b => !string.IsNullOrEmpty(b.WaitLabel)));
                perChannel.Add(cj);
            }
            j["channelSummary"] = perChannel;

            return Ok(j);
        }

        // ------------------------------------------------------------------ ESPRIT endpoints

        private HttpResponse RequireEsprit()
        {
            if (_ctx.Esprit != null && _ctx.Esprit.IsConnected) return null;
            return HttpResponse.Error(503, "esprit_unavailable",
                "This endpoint reads or writes the CAM document, which needs SwissForge running " +
                "inside ESPRIT with a document open. The engine endpoints (feeds-speeds, cycle-time, " +
                "plan, quote, gcode/*) work without it.");
        }

        private HttpResponse EspritProbe(HttpRequest req) => Ok(_ctx.Esprit.Probe().ToJson());

        private HttpResponse EspritTools(HttpRequest req)
        {
            var guard = RequireEsprit();
            if (guard != null) return guard;

            var arr = JsonValue.Arr();
            foreach (var t in _ctx.Esprit.GetTools()) arr.Add(t.ToJson());
            return Ok(JsonValue.Obj().Set("count", arr.Count).Set("tools", arr));
        }

        private HttpResponse EspritOperations(HttpRequest req)
        {
            var guard = RequireEsprit();
            if (guard != null) return guard;

            var arr = JsonValue.Arr();
            foreach (var o in _ctx.Esprit.GetOperations()) arr.Add(o.ToJson());
            return Ok(JsonValue.Obj().Set("count", arr.Count).Set("operations", arr));
        }

        private HttpResponse EspritStock(HttpRequest req)
        {
            var guard = RequireEsprit();
            if (guard != null) return guard;

            var stock = _ctx.Esprit.GetStock();
            return stock == null
                ? HttpResponse.Error(404, "no_stock", "The active document has no stock definition.")
                : Ok(stock.ToJson());
        }

        private HttpResponse EspritApplyCuttingData(HttpRequest req, JsonValue body)
        {
            var guard = RequireEsprit();
            if (guard != null) return guard;

            var updates = new List<CuttingDataUpdate>();
            foreach (var u in body["updates"])
            {
                updates.Add(new CuttingDataUpdate
                {
                    OperationId = u["operationId"].AsString(""),
                    SpeedRpm = u["speedRpm"].IsNull ? (double?)null : u["speedRpm"].AsDouble(),
                    FeedMmPerRev = u["feedMmPerRev"].IsNull ? (double?)null : u["feedMmPerRev"].AsDouble(),
                    DepthOfCutMm = u["depthOfCutMm"].IsNull ? (double?)null : u["depthOfCutMm"].AsDouble(),
                    Reason = u["reason"].AsString("")
                });
            }

            if (updates.Count == 0)
                return HttpResponse.Error(400, "no_updates", "Send an 'updates' array.");

            int applied = _ctx.Esprit.ApplyCuttingData(updates);
            return Ok(JsonValue.Obj()
                .Set("requested", updates.Count)
                .Set("applied", applied)
                .Set("skipped", updates.Count - applied));
        }

        private HttpResponse EspritPost(HttpRequest req, JsonValue body)
        {
            var guard = RequireEsprit();
            if (guard != null) return guard;

            var postName = body["postProcessor"].AsString("");
            var nc = _ctx.Esprit.PostProcess(postName);

            if (nc == null)
                return HttpResponse.Error(500, "post_failed",
                    $"ESPRIT did not return NC output for post processor '{postName}'.");

            var j = JsonValue.Obj();
            j.Set("postProcessor", postName);
            j.Set("gcode", nc);
            j.Set("lineCount", nc.Split('\n').Length);

            // Linting the freshly posted program is the whole point of having both in one place.
            if (body["lint"].AsBool(true))
            {
                var control = ControlProfile.For(_ctx.Machine.Dialect);
                var program = _parser.Parse(nc, postName);
                program.SplitChannels(control);
                var report = new GCodeLinter { Machine = _ctx.Machine, Control = control }.Lint(program);
                j["lint"] = report.ToJson();
            }

            return Ok(j);
        }
    }
}
