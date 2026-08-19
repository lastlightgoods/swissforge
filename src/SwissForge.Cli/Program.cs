using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SwissForge.Core.Api;
using SwissForge.Core.CycleTime;
using SwissForge.Core.Feeds;
using SwissForge.Core.GCode;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Quoting;
using SwissForge.Core.Templates;
using SwissForge.Core.Units;

namespace SwissForge.Cli
{
    /// <summary>
    /// Command-line access to the SwissForge engines.
    /// <para>
    /// Everything here runs with no ESPRIT installed and no CAM licence, on Windows, macOS
    /// or Linux. That is deliberate: quoting and program auditing are things an estimator or
    /// a shop manager needs to do from a laptop, not only from the one seat with the CAM
    /// system on it.
    /// </para>
    /// </summary>
    public static class Program
    {
        private const string Version = "1.0.0";

        public static int Main(string[] argv)
        {
            var args = Args.Parse(argv);

            if (args.Command == "" || args.Command == "help" || args.Flag("help") || args.Flag("h"))
            {
                PrintHelp();
                return args.Command == "" ? 1 : 0;
            }

            try
            {
                switch (args.Command.ToLowerInvariant())
                {
                    case "version": Console.WriteLine("SwissForge " + Version); return 0;
                    case "materials": return Materials(args);
                    case "tools": return Tools(args);
                    case "feeds": return Feeds(args);
                    case "plan": return Plan(args);
                    case "quote": return Quote(args);
                    case "lint": return Lint(args);
                    case "analyze": return Analyze(args);
                    case "sample": return Sample(args);
                    case "serve": return Serve(args);
                    default:
                        Console.Error.WriteLine($"Unknown command '{args.Command}'. Run 'swissforge help'.");
                        return 2;
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine("File not found: " + ex.FileName);
                return 3;
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine("Could not read the input: " + ex.Message);
                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 5;
            }
        }

        // ------------------------------------------------------------------ shared setup

        private static MaterialDatabase LoadMaterials(Args args)
        {
            var db = MaterialDatabase.CreateDefault();
            var path = args.Str("materials");
            if (!string.IsNullOrEmpty(path))
            {
                db.ApplyOverrides(JsonValue.Parse(File.ReadAllText(path)));
                Console.Error.WriteLine($"[loaded material overrides from {path}]");
            }
            return db;
        }

        private static ToolLibrary LoadTools(Args args)
        {
            var path = args.Str("tools");
            if (string.IsNullOrEmpty(path)) return ToolLibrary.CreateStarter();
            return ToolLibrary.FromJson(JsonValue.Parse(File.ReadAllText(path)));
        }

        private static MachineProfile LoadMachine(Args args)
        {
            var path = args.Str("machine");
            if (!string.IsNullOrEmpty(path))
                return MachineProfile.FromJson(JsonValue.Parse(File.ReadAllText(path)));

            return new MachineProfile
            {
                Id = "generic-swiss",
                Name = "Generic 20 mm Swiss",
                Dialect = args.Enum("dialect", ControlDialect.FanucGeneric),
                MaxBarDiameterMm = args.Num("max-bar", 20),
                ChannelCount = args.Int("channels", 2),
                HourlyRate = args.Num("rate", 75)
            };
        }

        private static UnitSystem Units(Args args) =>
            args.Flag("imperial") ? UnitSystem.Imperial : UnitSystem.Metric;

        private static int Emit(JsonValue j, Args args)
        {
            Console.WriteLine(j.ToJson(indent: true));
            return 0;
        }

        // ------------------------------------------------------------------ commands

        private static int Materials(Args args)
        {
            var db = LoadMaterials(args);

            if (args.Flag("json")) return Emit(db.ToJson(), args);

            Console.WriteLine($"{"ID",-10} {"NAME",-42} {"Vc m/min",9} {"BHN",5} {"kg/m3",7} {"COST/kg",9}");
            Console.WriteLine(new string('-', 88));
            foreach (var m in db.All.OrderBy(x => x.Group).ThenBy(x => x.Id))
            {
                var cost = db.CostIsIndicative(m.Id)
                    ? m.CostPerKg.ToString("F2", CultureInfo.InvariantCulture) + "*"
                    : m.CostPerKg.ToString("F2", CultureInfo.InvariantCulture);
                Console.WriteLine($"{m.Id,-10} {Trim(m.Name, 42),-42} {m.BaseSurfaceSpeedMPerMin,9:F0} " +
                                  $"{m.HardnessBhn,5:F0} {m.DensityGPerCm3 * 1000,7:F0} {cost,9}");
            }
            Console.WriteLine();
            Console.WriteLine("* indicative price, not your purchase cost. Quoting refuses to price off these");
            Console.WriteLine("  unless you pass --allow-indicative. Override them with --materials <file>.");
            return 0;
        }

        private static int Tools(Args args)
        {
            var lib = LoadTools(args);
            if (args.Flag("json")) return Emit(lib.ToJson(), args);

            Console.WriteLine($"{"ID",-8} {"DESCRIPTION",-34} {"PURPOSE",-12} {"SIDE",-5} {"DIA",7} {"NOSE",6}");
            Console.WriteLine(new string('-', 78));
            foreach (var t in lib.Tools)
                Console.WriteLine($"{t.Id,-8} {Trim(t.Description, 34),-34} {t.Purpose,-12} {t.Side,-5} " +
                                  $"{t.DiameterMm,7:F2} {t.NoseRadiusMm,6:F2}");
            return 0;
        }

        private static int Feeds(Args args)
        {
            var db = LoadMaterials(args);
            var tools = LoadTools(args);
            var machine = LoadMachine(args);
            var units = Units(args);

            var material = db.Find(args.Str("material", "12L14"));
            if (material == null)
            {
                Console.Error.WriteLine($"Material '{args.Str("material")}' not found. " +
                                        "Run 'swissforge materials' to see what is loaded.");
                return 6;
            }

            var toolId = args.Str("tool");
            ToolItem tool = toolId != null ? tools.Get(toolId) : null;
            var operation = args.Enum("op", OperationType.TurnRough);

            if (tool == null)
            {
                tool = tools.Select(operation, args.Enum("side", SpindleSide.Main), args.Num("dia", 0));
                if (tool == null)
                {
                    Console.Error.WriteLine($"No tool for {operation}. Pass --tool <id>, or supply a " +
                                            "library with --tools <file>.");
                    return 6;
                }
            }

            var rec = new FeedSpeedEngine().Recommend(new FeedSpeedRequest
            {
                Material = material,
                Tool = tool,
                Machine = machine,
                Operation = operation,
                Side = args.Enum("side", SpindleSide.Main),
                WorkDiameterMm = args.Num("dia", 0),
                RadialStockMm = args.Num("stock", 0),
                CutLengthMm = args.Num("length", 0),
                TargetRaMicron = args.Num("ra", 0),
                ToleranceMm = args.Num("tol", 0.05),
                UnsupportedLengthMm = args.Num("unsupported", 0),
                ThreadPitchMm = args.Num("pitch", 0),
                GuideBushingEngaged = !args.Flag("chucker")
            });

            if (args.Flag("json")) return Emit(rec.ToJson(), args);

            Console.WriteLine();
            Console.WriteLine($"  {material.Name}   |   {tool.Description ?? tool.Id}   |   {operation}");
            Console.WriteLine(new string('-', 66));
            Console.WriteLine($"  Spindle speed     {rec.Rpm,10:F0} rpm");
            Console.WriteLine($"  Surface speed     {U.FormatSpeed(rec.SurfaceSpeedMPerMin, units),14}");
            Console.WriteLine($"  Feed              {U.FormatFeed(rec.FeedMmPerRev, units),14}");
            Console.WriteLine($"  Feed rate         {rec.FeedMmPerMin,10:F0} mm/min");
            Console.WriteLine($"  Depth of cut      {U.FormatLength(rec.DepthOfCutMm, units),14}");
            Console.WriteLine($"  Passes            {rec.Passes,10}");
            if (!double.IsInfinity(rec.PredictedRaMicron))
                Console.WriteLine($"  Predicted finish  {rec.PredictedRaMicron,10:F2} um Ra");
            Console.WriteLine($"  Spindle power     {rec.SpindlePowerKw,10:F2} kW");
            Console.WriteLine($"  Cutting force     {rec.CuttingForceN,10:F0} N");
            Console.WriteLine($"  Removal rate      {rec.MrrCm3PerMin,10:F2} cm3/min");

            PrintAdvisories(rec.Advisories);
            return rec.HasBlocker ? 7 : 0;
        }

        private static int Plan(Args args)
        {
            var path = args.Positional.FirstOrDefault() ?? args.Str("part");
            if (path == null) { Console.Error.WriteLine("Usage: swissforge plan <part.json>"); return 2; }

            var part = PartSpec.FromJson(JsonValue.Parse(File.ReadAllText(path)));
            var plan = new JobPlanner().Plan(part, LoadTools(args), LoadMachine(args), LoadMaterials(args),
                                             LoadTemplate(args));

            if (args.Flag("json")) return Emit(plan.ToJson(), args);

            Console.WriteLine();
            Console.WriteLine($"  {part.PartNumber}  {part.Description}");
            Console.WriteLine($"  {plan.Operations.Count} operations across " +
                              $"{plan.Operations.Select(o => o.Channel).Distinct().Count()} channels");
            Console.WriteLine(new string('=', 92));
            Console.WriteLine($"  {"CH",-3} {"SEQ",-4} {"OPERATION",-32} {"TOOL",-6} {"RPM",8} {"FEED",9} {"SEC",8}");
            Console.WriteLine(new string('-', 92));

            foreach (var op in plan.Operations.OrderBy(o => o.Channel).ThenBy(o => o.Sequence))
            {
                var wait = string.IsNullOrEmpty(op.WaitBefore) ? "" : $"  <wait {op.WaitBefore}>";
                Console.WriteLine($"  {op.Channel,-3} {op.Sequence,-4} {Trim(op.Name, 32),-32} {op.ToolId,-6} " +
                                  $"{op.Rpm,8:F0} {op.FeedMmPerRev,9:F4} {op.TotalSeconds,8:F2}{wait}");
            }

            if (plan.Cycle != null)
            {
                Console.WriteLine(new string('-', 92));
                Console.WriteLine($"  Cycle time        {U.FormatDuration(plan.Cycle.CycleSeconds)}");
                Console.WriteLine($"  Sum of operations {U.FormatDuration(plan.Cycle.SumOfOperationSeconds)} " +
                                  $"(overlap ratio {plan.Cycle.OverlapRatio:F2})");
                Console.WriteLine($"  Parts per hour    {plan.Cycle.PartsPerHour:F1}");
                foreach (var kv in plan.Cycle.ChannelIdleSeconds.OrderBy(k => k.Key))
                    Console.WriteLine($"  Channel {kv.Key} idle   {kv.Value:F1} s " +
                                      $"({(plan.Cycle.CycleSeconds > 0 ? kv.Value / plan.Cycle.CycleSeconds * 100 : 0):F0}%)");
            }

            PrintAdvisories(plan.Advisories);
            return plan.IsRunnable ? 0 : 7;
        }

        private static SetupTemplate LoadTemplate(Args args)
        {
            var path = args.Str("template");
            return string.IsNullOrEmpty(path)
                ? new SetupTemplate()
                : SetupTemplate.FromJson(JsonValue.Parse(File.ReadAllText(path)));
        }

        private static int Quote(Args args)
        {
            var path = args.Positional.FirstOrDefault() ?? args.Str("part");
            if (path == null) { Console.Error.WriteLine("Usage: swissforge quote <part.json>"); return 2; }

            var part = PartSpec.FromJson(JsonValue.Parse(File.ReadAllText(path)));
            var machine = LoadMachine(args);
            var db = LoadMaterials(args);
            var tools = LoadTools(args);

            double cycleSeconds = args.Num("cycle", 0);
            var toolsUsed = new List<ToolItem>();

            if (cycleSeconds <= 0)
            {
                var plan = new JobPlanner().Plan(part, tools, machine, db, LoadTemplate(args));
                cycleSeconds = plan.Cycle?.CycleSeconds ?? 0;
                toolsUsed.AddRange(plan.ToolsUsed);
                Console.Error.WriteLine($"[planned the job to derive a {cycleSeconds:F1} s cycle time; " +
                                        "pass --cycle <seconds> to use your own]");
            }

            var policy = new QuotePolicy
            {
                SetupHours = args.Num("setup-hours", 4),
                SetupRatePerHour = args.Num("setup-rate", 85),
                ProgrammingHours = args.Num("prog-hours", 2),
                ProgrammingRatePerHour = args.Num("prog-rate", 95),
                TargetMarginFraction = args.Num("margin", 0.35),
                FirstArticleCost = args.Num("fai", 0),
                PackagingPerPart = args.Num("packaging", 0),
                Currency = args.Str("currency", "USD"),
                AllowIndicativeMaterialCost = args.Flag("allow-indicative")
            };

            var breaks = (args.Str("breaks") ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n > 0).ToList();

            var quote = new QuoteEngine().Quote(new QuoteRequest
            {
                Part = part, Machine = machine, Materials = db, Policy = policy,
                CycleSeconds = cycleSeconds, Tools = toolsUsed, QuantityBreaks = breaks
            });

            if (args.Flag("json")) return Emit(quote.ToJson(), args);

            var cur = policy.Currency;
            Console.WriteLine();
            Console.WriteLine($"  QUOTE  {part.PartNumber} {part.Revision}   {part.Customer}");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"  Cycle time        {U.FormatDuration(quote.CycleSeconds)}");
            Console.WriteLine($"  Parts per bar     {quote.PartsPerBar}   " +
                              $"(bar yield {quote.BarUtilizationFraction * 100:F0}%)");
            Console.WriteLine($"  Parts per hour    {quote.PartsPerHour:F1}");
            Console.WriteLine($"  Lot machine time  {quote.LotMachineHours:F1} h " +
                              $"({quote.LotCalendarDays:F1} production days)");
            Console.WriteLine();

            var p = quote.Primary;
            Console.WriteLine($"  Cost breakdown at {p.Quantity} pieces ({cur})");
            Console.WriteLine(new string('-', 70));
            Line("Material", p.MaterialPerPart);
            Line("Machine time", p.MachinePerPart);
            Line("Tooling", p.ToolingPerPart);
            Line("Setup", p.SetupPerPart);
            Line("Programming", p.ProgrammingPerPart);
            Line("Secondary ops", p.SecondaryPerPart);
            Line("Packaging", p.PackagingPerPart);
            Line("First article", p.FirstArticlePerPart);
            Line("Scrap allowance", p.ScrapPerPart);
            Console.WriteLine(new string('-', 70));
            Line("COST", p.CostPerPart);
            Line($"PRICE (at {p.MarginFraction * 100:F0}% margin)", p.PricePerPart);
            Console.WriteLine($"  {"Lot total",-32} {p.LotPrice,14:F2}");

            if (quote.Breaks.Count > 1)
            {
                Console.WriteLine();
                Console.WriteLine($"  {"QUANTITY",12} {"EACH",12} {"TOTAL",14}");
                Console.WriteLine(new string('-', 42));
                foreach (var b in quote.Breaks.OrderBy(x => x.Quantity))
                    Console.WriteLine($"  {b.Quantity,12} {b.PricePerPart,12:F4} {b.LotPrice,14:F2}");
            }

            PrintAdvisories(quote.Advisories);

            if (!quote.IsQuotable)
            {
                Console.WriteLine();
                Console.WriteLine("  This quote is NOT ready to send. Resolve the critical items above.");
                return 7;
            }
            return 0;

            void Line(string label, double value) =>
                Console.WriteLine($"  {label,-32} {value,14:F4}");
        }

        private static int Lint(Args args)
        {
            var path = args.Positional.FirstOrDefault() ?? args.Str("file");
            string text = path != null ? File.ReadAllText(path) : Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Usage: swissforge lint <program.nc>   (or pipe the program on stdin)");
                return 2;
            }

            var machine = LoadMachine(args);
            var control = ControlProfile.For(args.Enum("dialect", machine.Dialect));

            var program = new GParser().Parse(text, Path.GetFileName(path ?? "stdin"));
            program.SplitChannels(control);

            var report = new GCodeLinter
            {
                Machine = machine, Control = control,
                ReportBlockDeletes = args.Flag("block-deletes")
            }.Lint(program);

            if (args.Flag("json")) return Emit(report.ToJson(), args);

            Console.WriteLine();
            Console.WriteLine($"  {report.ProgramName}   {control.Name}   " +
                              $"{program.Channels.Count} channel(s), {program.Blocks.Count} blocks");
            Console.WriteLine(new string('=', 88));

            if (report.Findings.Count == 0)
            {
                Console.WriteLine("  Nothing found.");
                return 0;
            }

            foreach (var f in report.Findings
                        .OrderByDescending(x => x.Severity).ThenBy(x => x.LineNumber))
            {
                Console.WriteLine($"  {Tag(f.Severity)} {f.Code}   line {f.LineNumber}, channel {f.Channel}");
                Console.WriteLine($"      {f.Message}");
                if (!string.IsNullOrEmpty(f.Raw)) Console.WriteLine($"      > {f.Raw}");
                if (!string.IsNullOrEmpty(f.Suggestion)) Console.WriteLine($"      {f.Suggestion}");
                Console.WriteLine();
            }

            Console.WriteLine(new string('-', 88));
            Console.WriteLine($"  {report.Count(Severity.Critical)} critical, {report.Count(Severity.Error)} error, " +
                              $"{report.Count(Severity.Warning)} warning, {report.Count(Severity.Info)} info");

            // Exit codes are chosen so this drops straight into a pre-post hook or a CI job.
            if (report.HasCritical) return 1;
            if (report.HasErrors) return args.Flag("strict") ? 1 : 0;
            return 0;
        }

        private static int Analyze(Args args)
        {
            var path = args.Positional.FirstOrDefault() ?? args.Str("file");
            string text = path != null ? File.ReadAllText(path) : Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.Error.WriteLine("Usage: swissforge analyze <program.nc>");
                return 2;
            }

            var machine = LoadMachine(args);
            var control = ControlProfile.For(args.Enum("dialect", machine.Dialect));
            var program = new GParser().Parse(text, Path.GetFileName(path ?? "stdin"));
            program.SplitChannels(control);

            if (args.Flag("json")) return Emit(program.ToJson(includeBlocks: args.Flag("blocks")), args);

            Console.WriteLine();
            Console.WriteLine($"  {program.Name}   {control.Name}");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"  Blocks          {program.Blocks.Count}");
            Console.WriteLine($"  Channels        {string.Join(", ", program.Channels)}");
            Console.WriteLine($"  Program numbers {string.Join(", ", program.ProgramNumbers)}");
            Console.WriteLine();

            Console.WriteLine($"  {"CHANNEL",8} {"BLOCKS",8} {"TOOLS",7} {"FEEDS",7} {"WAITS",7}");
            Console.WriteLine(new string('-', 42));
            foreach (var ch in program.Channels)
            {
                var blocks = program.BlocksIn(ch).ToList();
                Console.WriteLine($"  {ch,8} {blocks.Count(b => !b.IsEmpty),8} {blocks.Count(b => b.Has('T')),7} " +
                                  $"{blocks.Count(b => b.HasG(1) || b.HasG(2) || b.HasG(3)),7} " +
                                  $"{blocks.Count(b => !string.IsNullOrEmpty(b.WaitLabel)),7}");
            }

            var waits = program.Blocks.Where(b => !string.IsNullOrEmpty(b.WaitLabel))
                               .GroupBy(b => b.WaitLabel, StringComparer.OrdinalIgnoreCase).ToList();
            if (waits.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  {"WAITCODE",12} {"CHANNELS",12} {"USES",6}  STATUS");
                Console.WriteLine(new string('-', 56));
                foreach (var g in waits.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var channels = g.Select(b => b.Channel).Distinct().OrderBy(c => c).ToList();
                    var paired = channels.Count >= 2;
                    Console.WriteLine($"  {g.Key,12} {string.Join(",", channels),12} {g.Count(),6}  " +
                                      (paired ? "paired" : "UNPAIRED"));
                }
            }

            return 0;
        }

        private static int Sample(Args args)
        {
            var part = new PartSpec
            {
                PartNumber = "SF-2050",
                Revision = "B",
                Customer = "Example Customer",
                Description = "Stepped pin, M4 thread, cross hole, back bore",
                OverallLengthMm = 28.0,
                MaxDiameterMm = 9.5,
                CutoffWidthMm = 1.5,
                BackFaceStockMm = 0.3,
                Quantity = 5000,
                AnnualUsage = 40000,
                ScrapRate = 0.02,
                Stock = new StockBar
                {
                    MaterialId = "12L14", DiameterMm = 10.0, LengthMm = 3660,
                    RemnantMm = 300, BarFaceStockMm = 2.0, CostPerBar = 18.00
                },
                Features = new List<PartFeature>
                {
                    new PartFeature { Id = "f-od1", Kind = OperationType.TurnRough, Label = "Rough 9.5 shoulder",
                        Side = SpindleSide.Main, StartDiameterMm = 10.0, DiameterMm = 9.5, LengthMm = 12.0,
                        ToleranceMm = 0.1 },
                    new PartFeature { Id = "f-od2", Kind = OperationType.TurnFinish, Label = "Finish 9.5 shoulder",
                        Side = SpindleSide.Main, StartDiameterMm = 9.7, DiameterMm = 9.5, LengthMm = 12.0,
                        ToleranceMm = 0.013, SurfaceFinishRa = 1.6 },
                    new PartFeature { Id = "f-thd", Kind = OperationType.Thread, Label = "M4 x 0.7 thread",
                        Side = SpindleSide.Main, StartDiameterMm = 4.0, DiameterMm = 4.0, LengthMm = 8.0,
                        ThreadPitchMm = 0.7 },
                    new PartFeature { Id = "f-xh", Kind = OperationType.CrossDrill, Label = "2.0 cross hole",
                        Side = SpindleSide.Main, DiameterMm = 2.0, LengthMm = 9.5, Instances = 2 },
                    new PartFeature { Id = "f-bore", Kind = OperationType.BackDrill, Label = "3.0 back bore",
                        Side = SpindleSide.Sub, DiameterMm = 3.0, LengthMm = 10.0 },
                    new PartFeature { Id = "f-bface", Kind = OperationType.BackFace, Label = "Back face to length",
                        Side = SpindleSide.Sub, StartDiameterMm = 9.5, DiameterMm = 0, LengthMm = 4.75,
                        ToleranceMm = 0.05 }
                },
                SecondaryOps = new List<SecondaryOperation>
                {
                    new SecondaryOperation { Name = "Deburr and tumble", CostPerPart = 0.06, Vendor = "in house" },
                    new SecondaryOperation { Name = "Zinc plate", CostPerPart = 0.14, LotCharge = 120,
                                             LeadTimeDays = 5, Vendor = "Example Plating" }
                }
            };

            Console.WriteLine(part.ToJson().ToJson(indent: true));
            return 0;
        }

        private static int Serve(Args args)
        {
            var ctx = new ApiContext
            {
                Materials = LoadMaterials(args),
                Tools = LoadTools(args),
                Machine = LoadMachine(args),
                Template = LoadTemplate(args),
                Version = Version
            };

            var token = args.Str("token") ?? Guid.NewGuid().ToString("N");
            var api = new SwissForgeApi(ctx);

            using (var server = new HttpServer { AuthToken = token, Handler = api.Handle })
            {
                server.Fault = ex => Console.Error.WriteLine("[fault] " + ex.Message);
                server.RequestCompleted = (req, res, ms) =>
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {res.StatusCode}  {req?.Method,-5} " +
                                      $"{req?.Path,-32} {ms,6:F0} ms");

                server.Start(args.Int("port", 8731));

                Console.WriteLine();
                Console.WriteLine($"  SwissForge API on {server.BaseUrl}");
                Console.WriteLine($"  Token: {token}");
                Console.WriteLine($"  Spec:  {server.BaseUrl}/openapi.json");
                Console.WriteLine();
                Console.WriteLine("  Note: ESPRIT endpoints return 503 here. They need the add-in hosted");
                Console.WriteLine("  inside ESPRIT with a document open. Everything else works.");
                Console.WriteLine();
                Console.WriteLine("  Ctrl-C to stop.");
                Console.WriteLine();

                var stop = new System.Threading.ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
                stop.Wait();

                Console.WriteLine("Stopping.");
            }

            return 0;
        }

        // ------------------------------------------------------------------ presentation

        private static void PrintAdvisories(IEnumerable<Advisory> advisories)
        {
            var list = advisories?.ToList() ?? new List<Advisory>();
            if (list.Count == 0) return;

            Console.WriteLine();
            foreach (var a in list.OrderByDescending(x => x.Severity))
            {
                Console.WriteLine($"  {Tag(a.Severity)} {a.Code}");
                foreach (var line in Wrap(a.Message, 78)) Console.WriteLine("      " + line);
            }
        }

        private static string Tag(Severity s)
        {
            switch (s)
            {
                case Severity.Critical: return "[STOP] ";
                case Severity.Error: return "[ERROR]";
                case Severity.Warning: return "[warn] ";
                default: return "[note] ";
            }
        }

        private static string Trim(string s, int max)
        {
            s = s ?? "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static IEnumerable<string> Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            var words = text.Split(' ');
            var line = "";
            foreach (var w in words)
            {
                if (line.Length + w.Length + 1 > width)
                {
                    yield return line;
                    line = w;
                }
                else line = line.Length == 0 ? w : line + " " + w;
            }
            if (line.Length > 0) yield return line;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
SwissForge " + Version + @" - Swiss-type CNC engine tools

USAGE
  swissforge <command> [options]

COMMANDS
  feeds       Recommend cutting data for one operation
  plan        Plan a job from a part file, with a multi-channel timed schedule
  quote       Price a part, with a full cost breakdown and quantity breaks
  lint        Static analysis of an NC program
  analyze     Channel and waitcode structure of an NC program
  materials   List the material library
  tools       List the tool library
  sample      Print a sample part file to start from
  serve       Host the REST API on loopback
  version     Print the version

COMMON OPTIONS
  --json              Emit raw JSON instead of a formatted report
  --materials <file>  Overlay your material costs and speeds on the built-ins
  --tools <file>      Use your tool library instead of the built-in starter set
  --machine <file>    Use a machine profile file
  --imperial          Display SFM and inch/rev instead of m/min and mm/rev

FEEDS OPTIONS
  --material <id>     Material id or name          (default 12L14)
  --tool <id>         Tool id; otherwise selected from the library by operation
  --op <type>         TurnRough, TurnFinish, Drill, Thread, Cutoff, ...
  --dia <mm>          Diameter the tool works at
  --stock <mm>        Radial stock to remove
  --length <mm>       Axial cut length
  --ra <um>           Required surface finish
  --tol <mm>          Print tolerance
  --unsupported <mm>  Length ahead of the guide bushing
  --pitch <mm>        Thread pitch
  --chucker           Model a setup with the guide bushing removed

QUOTE OPTIONS
  --cycle <s>         Use this cycle time instead of planning the job
  --margin <f>        Target margin as a fraction     (default 0.35)
  --setup-hours <h>   Setup time                      (default 4)
  --breaks 100,1000   Extra quantities to price
  --allow-indicative  Price even where material cost is a built-in placeholder

LINT OPTIONS
  --dialect <name>    CitizenCincom, StarSR, TsugamiFanuc, HanwhaXD, FanucGeneric
  --strict            Treat errors, not just criticals, as a failing exit code

EXAMPLES
  swissforge feeds --material SS316 --op TurnRough --dia 12 --stock 1.5 --length 20
  swissforge sample > part.json
  swissforge plan part.json
  swissforge quote part.json --breaks 100,1000,10000
  swissforge lint main.nc --dialect CitizenCincom
  swissforge serve --port 8731 --token my-secret

EXIT CODES
  0 ok    1 findings    2 usage    3 file    4 parse    5 error    7 blocked
");
        }
    }
}
