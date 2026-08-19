using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Feeds;
using SwissForge.Core.Model;
using SwissForge.Core.Templates;

namespace SwissForge.Tests
{
    public static class TestPlanner
    {
        /// <summary>
        /// A realistic small Swiss part: a stepped pin with a threaded end, a cross hole,
        /// and a back-drilled bore. The kind of thing that runs 50,000 a year.
        /// </summary>
        private static PartSpec Pin() => new PartSpec
        {
            PartNumber = "SF-2050",
            Description = "Stepped pin, M4 thread, cross hole, back bore",
            OverallLengthMm = 28.0,
            MaxDiameterMm = 9.5,
            CutoffWidthMm = 1.5,
            BackFaceStockMm = 0.3,
            Quantity = 5000,
            Stock = new StockBar { MaterialId = "12L14", DiameterMm = 10.0, LengthMm = 3660, CostPerBar = 18.00 },
            Features = new List<PartFeature>
            {
                new PartFeature { Id = "f-od1", Kind = OperationType.TurnRough, Label = "Rough 9.5 shoulder",
                    Side = SpindleSide.Main, StartDiameterMm = 10.0, DiameterMm = 9.5, LengthMm = 12.0, ToleranceMm = 0.1 },
                new PartFeature { Id = "f-od2", Kind = OperationType.TurnFinish, Label = "Finish 9.5 shoulder",
                    Side = SpindleSide.Main, StartDiameterMm = 9.7, DiameterMm = 9.5, LengthMm = 12.0,
                    ToleranceMm = 0.013, SurfaceFinishRa = 1.6 },
                new PartFeature { Id = "f-thd", Kind = OperationType.Thread, Label = "M4 x 0.7 thread",
                    Side = SpindleSide.Main, StartDiameterMm = 4.0, DiameterMm = 4.0, LengthMm = 8.0, ThreadPitchMm = 0.7 },
                new PartFeature { Id = "f-xh", Kind = OperationType.CrossDrill, Label = "2.0 cross hole",
                    Side = SpindleSide.Main, DiameterMm = 2.0, LengthMm = 9.5, Instances = 2 },
                new PartFeature { Id = "f-bore", Kind = OperationType.BackDrill, Label = "3.0 back bore",
                    Side = SpindleSide.Sub, DiameterMm = 3.0, LengthMm = 10.0 },
                new PartFeature { Id = "f-bface", Kind = OperationType.BackFace, Label = "Back face to length",
                    Side = SpindleSide.Sub, StartDiameterMm = 9.5, DiameterMm = 0, LengthMm = 4.75, ToleranceMm = 0.05 }
            }
        };

        private static MachineProfile Machine() => new MachineProfile
        {
            Id = "L20", Name = "Citizen L20-VIII", Builder = "Citizen",
            Dialect = ControlDialect.CitizenCincom,
            MaxBarDiameterMm = 20, MaxZStrokePerPassMm = 200,
            ChannelCount = 3, HasSubSpindle = true, HasGuideBushing = true,
            MainSpindleMaxRpm = 10000, SubSpindleMaxRpm = 8000, LiveToolMaxRpm = 6000,
            MainSpindlePowerKw = 3.7, RapidRateMmPerMin = 32000,
            HourlyRate = 75, ChannelNames = new List<string> { "$1", "$2", "$3" }
        };

        public static void Run()
        {
            Check.Suite("Job planner");

            var db = MaterialDatabase.CreateDefault();
            var tools = ToolLibrary.CreateStarter();
            var machine = Machine();
            var planner = new JobPlanner();

            var plan = planner.Plan(Pin(), tools, machine, db);

            Check.Greater(plan.Operations.Count, 6, "the planner produces a full operation list");
            Check.True(plan.IsRunnable, "a well-specified part plans without a blocking problem");

            // every operation that cuts has real cutting data
            var cutting = plan.Operations.Where(o =>
                o.Type != OperationType.Transfer && o.Type != OperationType.BarFeed &&
                o.Type != OperationType.Custom).ToList();
            Check.True(cutting.All(o => o.Rpm > 0), "every cutting operation has a spindle speed");
            Check.True(cutting.All(o => o.FeedMmPerRev > 0), "every cutting operation has a feed");
            Check.True(cutting.All(o => !string.IsNullOrEmpty(o.ToolId)), "every cutting operation has a tool");

            // the two cross-hole instances both appear
            var crossHoles = plan.Operations.Where(o => o.FeatureId == "f-xh").ToList();
            Check.Equal(2, crossHoles.Count, "a feature with two instances produces two operations");
            Check.Contains(crossHoles[0].Name, "1 of 2", "instances are labelled so the setup sheet reads properly");

            // thread feed equals pitch
            var thread = plan.Operations.First(o => o.Type == OperationType.Thread);
            Check.Near(0.7, thread.FeedMmPerRev, 1e-9, "the M4 thread is fed at its 0.7 mm pitch");

            // the mandatory closing operations
            Check.True(plan.Operations.Any(o => o.Type == OperationType.Cutoff), "the plan ends with a cutoff");
            Check.True(plan.Operations.Any(o => o.Type == OperationType.Transfer), "a sub-spindle transfer is planned");
            Check.True(plan.Operations.Any(o => o.Type == OperationType.BarFeed), "the bar is advanced for the next part");

            // channel assignment
            var channels = plan.Operations.Select(o => o.Channel).Distinct().OrderBy(c => c).ToList();
            Check.Greater(channels.Count, 1, "work is spread across more than one channel");
            var backOps = plan.Operations.Where(o => o.Side == SpindleSide.Sub &&
                                                     o.Type != OperationType.Custom).ToList();
            Check.Greater(backOps.Count, 0, "back-working operations are planned");
            Check.True(backOps.All(o => o.Channel != 0), "back-working is not on the main channel");

            // cycle time
            Check.True(plan.Cycle != null, "the plan is timed");
            Check.Greater(plan.Cycle.CycleSeconds, 0, "the cycle time is positive");
            Check.Less(plan.Cycle.CycleSeconds, 600, "the cycle time is plausible for a small pin");
            Check.Greater(plan.Cycle.OverlapRatio, 1.0, "the channels genuinely overlap");

            // the transfer is the exclusive barrier it should be
            var transferSched = plan.Cycle.Schedule.First(s => s.Operation.Type == OperationType.Transfer);
            var mainCutting = plan.Cycle.Schedule
                .Where(s => s.Operation.Channel == 0 && s.Operation.Type != OperationType.Transfer &&
                            s.Operation.Type != OperationType.Cutoff && s.Operation.Type != OperationType.BarFeed);
            Check.True(mainCutting.All(s => s.StartSeconds <= transferSched.StartSeconds + 1e-6),
                "nothing on the main channel starts after the transfer");

            // honest advisories
            Check.True(plan.Advisories.Any(a => a.Code == "NO_COLLISION_CHECK"),
                "the planner states plainly that it has not checked for collisions");
            Check.True(plan.Advisories.Any(a => a.Code == "OVERLAP_ASSUMED"),
                "the planner explains that the first part off the bar runs longer");

            // ------------------------------------------------------------------
            Check.Suite("Job planner guard rails");

            // --- a missing tool is a blocking problem, not a silent omission
            var sparse = new ToolLibrary();
            sparse.Add(new ToolItem { Id = "only", Purpose = OperationType.TurnRough, Side = SpindleSide.Main,
                                      NoseRadiusMm = 0.4, Substrate = ToolMaterial.CoatedCarbide });
            var plan2 = planner.Plan(Pin(), sparse, machine, db);
            Check.False(plan2.IsRunnable, "a plan missing tools is not runnable");
            Check.True(plan2.Advisories.Any(a => a.Code == "NO_TOOL"), "each missing tool is named");
            Check.True(plan2.Advisories.Any(a => a.Code == "NO_CUTOFF_TOOL"), "a missing cutoff tool is called out");
            Check.Contains(plan2.Advisories.First(a => a.Code == "NO_TOOL").Message, "short by",
                "the advisory warns that the cycle time is understated as a result");

            // --- an oversized drill is refused rather than substituted
            var partWithOddHole = Pin();
            partWithOddHole.Features.Add(new PartFeature
            {
                Id = "f-odd", Kind = OperationType.Drill, Label = "0.4 mm micro hole",
                Side = SpindleSide.Main, DiameterMm = 0.4, LengthMm = 3.0
            });
            var plan3 = planner.Plan(partWithOddHole, tools, machine, db);
            Check.True(plan3.Advisories.Any(a => a.Code == "NO_TOOL" && a.Message.Contains("0.40")),
                "a 0.4 mm hole is not drilled with the smallest available 1.0 mm drill");

            // --- unknown material stops the plan immediately
            var badMaterial = Pin();
            badMaterial.Stock.MaterialId = "ADAMANTIUM";
            var plan4 = planner.Plan(badMaterial, tools, machine, db);
            Check.False(plan4.IsRunnable, "an unknown material blocks planning");
            Check.Equal(0, plan4.Operations.Count, "no operations are invented without cutting data");

            // --- bar too big
            var bigBar = Pin();
            bigBar.Stock.DiameterMm = 32;
            var plan5 = planner.Plan(bigBar, tools, machine, db);
            Check.True(plan5.Advisories.Any(a => a.Code == "BAR_TOO_BIG"), "an oversized bar is caught");

            // --- a long part warns about re-gripping
            var longPart = Pin();
            longPart.OverallLengthMm = 400;
            var plan6 = planner.Plan(longPart, tools, machine, db);
            Check.True(plan6.Advisories.Any(a => a.Code == "LONG_PART"),
                "a part longer than the Z stroke is flagged for re-gripping");

            // --- a template with overlap off still plans
            var serialTemplate = new SetupTemplate { OverlapBackWorking = false };
            var plan7 = planner.Plan(Pin(), tools, machine, db, serialTemplate);
            Check.Greater(plan7.Cycle.CycleSeconds, 0, "a non-overlapping template still produces a cycle");

            // --- a machine with no sub spindle says so
            var noSub = Machine();
            noSub.HasSubSpindle = false;
            noSub.ChannelCount = 1;
            var plan8 = planner.Plan(Pin(), tools, noSub, db);
            Check.True(plan8.Advisories.Any(a => a.Code == "NO_SUB_SPINDLE"),
                "a single-spindle machine is told it needs a second operation");

            // --- a cross drill is not derated for the workpiece being long
            var crossDrill = plan.Operations.First(o => o.Type == OperationType.CrossDrill);
            Check.False(crossDrill.Warnings.Any(w => w.Contains("SLENDER_DERATE")),
                "a cross hole is not slenderness-derated for the part's overhang; the tool is the flexible member");
            Check.Greater(crossDrill.FeedMmPerRev, 0.01,
                "the cross drill keeps a workable feed rather than collapsing to nothing");

            // --- but an OD turn far out from the bushing still is
            var longOd = Pin();
            longOd.Features = new List<PartFeature>
            {
                new PartFeature { Id = "long", Kind = OperationType.TurnRough, Label = "Long slender OD",
                    Side = SpindleSide.Main, StartDiameterMm = 10, DiameterMm = 6, LengthMm = 80, ToleranceMm = 0.1 }
            };
            var slenderPlan = planner.Plan(longOd, tools, machine, db);
            var slenderOp = slenderPlan.Operations.First(o => o.FeatureId == "long");
            Check.True(slenderOp.Warnings.Any(w => w.Contains("SLENDER_DERATE")),
                "an 80 mm OD turn on 10 mm stock is still derated for workpiece deflection");

            // --- a waitcode placeholder costs no cycle time
            var marker = plan.Operations.FirstOrDefault(o => o.IsSyncMarker);
            Check.True(marker != null, "a sync marker is emitted to hold the transfer waitcode");
            Check.Equal(0.0, marker.TotalSeconds, "a sync marker costs no time; the waiting is the scheduler's job");

            // --- channel fallback does not push endworking onto the sub spindle
            var twoChannel = Machine();
            twoChannel.ChannelCount = 2;
            var template2 = new SetupTemplate();
            var endworkingTool = new ToolItem { Id = "E1", Purpose = OperationType.Drill,
                Station = ToolStation.EndworkingSlide, Side = SpindleSide.Main, DiameterMm = 3 };
            var backTool = new ToolItem { Id = "K1", Purpose = OperationType.BackDrill,
                Station = ToolStation.BackWorking, Side = SpindleSide.Sub, DiameterMm = 3 };
            Check.Equal(0, template2.ChannelFor(endworkingTool, twoChannel),
                "endworking falls back to the main channel, not to the sub-spindle channel");
            Check.Equal(1, template2.ChannelFor(backTool, twoChannel),
                "back-working still lands on the sub-spindle channel");

            // --- the plan round-trips through JSON
            var json = plan.ToJson().ToJson(indent: true);
            Check.Greater(json.Length, 500, "the plan serialises to JSON");
            Check.Contains(json, "SF-2050", "the serialised plan names the part");
            Check.Contains(json, "cycleSeconds", "the serialised plan carries the cycle time");
        }
    }
}
