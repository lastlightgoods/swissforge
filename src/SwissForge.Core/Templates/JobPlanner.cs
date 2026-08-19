using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.CycleTime;
using SwissForge.Core.Feeds;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Units;

namespace SwissForge.Core.Templates
{
    /// <summary>A complete, timed plan for one part.</summary>
    public sealed class JobPlan
    {
        public PartSpec Part { get; set; }
        public MachineProfile Machine { get; set; }
        public SetupTemplate Template { get; set; }

        public List<OperationSpec> Operations { get; } = new List<OperationSpec>();
        public List<Advisory> Advisories { get; } = new List<Advisory>();
        public CycleTimeResult Cycle { get; set; }

        /// <summary>Tools the plan actually calls for, in the order first used.</summary>
        public List<ToolItem> ToolsUsed { get; } = new List<ToolItem>();

        public bool IsRunnable => !Advisories.Any(a => a.Severity == Severity.Critical);

        public JsonValue ToJson(bool includeSchedule = true)
        {
            var j = JsonValue.Obj();
            j.Set("partNumber", Part?.PartNumber ?? "");
            j.Set("machine", Machine?.Name ?? "");
            j.Set("template", Template?.Name ?? "");
            j.Set("isRunnable", IsRunnable);
            j.Set("operationCount", Operations.Count);

            var ops = JsonValue.Arr();
            foreach (var o in Operations.OrderBy(x => x.Channel).ThenBy(x => x.Sequence))
                ops.Add(o.ToJson());
            j["operations"] = ops;

            var tools = JsonValue.Arr();
            foreach (var t in ToolsUsed) tools.Add(t.ToJson());
            j["toolsUsed"] = tools;

            if (Cycle != null && includeSchedule) j["cycle"] = Cycle.ToJson();
            else if (Cycle != null) j.Set("cycleSeconds", Math.Round(Cycle.CycleSeconds, 3));

            var adv = JsonValue.Arr();
            foreach (var a in Advisories) adv.Add(a.ToJson());
            j["advisories"] = adv;
            return j;
        }
    }

    /// <summary>
    /// Turns a part specification into a runnable, timed, multi-channel Swiss plan.
    /// <para>
    /// The planner's job is to get you to a sensible starting point in seconds rather than
    /// an afternoon: right operations, right order, right channel, real cutting data, and a
    /// cycle time you can quote from. It is not a replacement for a programmer's judgement,
    /// and it says so — anything it had to assume comes back as an advisory rather than
    /// disappearing into the output.
    /// </para>
    /// <para>
    /// One thing it deliberately does not do is collision checking. It assigns work to
    /// channels on the assumption that the machine's tool positions allow concurrency;
    /// whether a given gang slide and endworking sleeve can actually be in the cut at the
    /// same time is a question for the kinematic model in ESPRIT, not for a planner working
    /// from a feature list.
    /// </para>
    /// </summary>
    public sealed class JobPlanner
    {
        private readonly FeedSpeedEngine _feeds = new FeedSpeedEngine();
        private readonly CycleTimeEstimator _timer = new CycleTimeEstimator();

        public JobPlan Plan(PartSpec part, ToolLibrary tools, MachineProfile machine,
                            MaterialDatabase materials, SetupTemplate template = null)
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            tools = tools ?? new ToolLibrary();
            machine = machine ?? new MachineProfile();
            template = template ?? new SetupTemplate();

            var plan = new JobPlan { Part = part, Machine = machine, Template = template };

            var material = materials?.Get(part.Stock.MaterialId);
            if (material == null)
            {
                plan.Advisories.Add(new Advisory(Severity.Critical, "NO_MATERIAL",
                    $"Material '{part.Stock.MaterialId}' is not in the library, so no cutting data " +
                    "can be derived. Add it before planning."));
                return plan;
            }

            var features = new List<PartFeature>(part.Features ?? new List<PartFeature>());

            if (template.AlwaysFaceFirst && !features.Any(f => f.Kind == OperationType.Face && f.Side == SpindleSide.Main))
            {
                features.Insert(0, new PartFeature
                {
                    Id = "auto-face",
                    Kind = OperationType.Face,
                    Label = "Face bar (added by template)",
                    Side = SpindleSide.Main,
                    DiameterMm = 0,
                    StartDiameterMm = part.Stock.DiameterMm,
                    LengthMm = part.Stock.DiameterMm / 2.0,
                    ToleranceMm = 0.05
                });
                plan.Advisories.Add(new Advisory(Severity.Info, "AUTO_FACE",
                    "A facing operation was added because the template asks for one on every bar. " +
                    "Turn off AlwaysFaceFirst if your bar arrives faced."));
            }

            if (features.Count == 0)
            {
                plan.Advisories.Add(new Advisory(Severity.Critical, "NO_FEATURES",
                    "The part has no features, so there is nothing to plan."));
                return plan;
            }

            // ---------------------------------------------------------- main side
            var channelSeq = new Dictionary<int, int>();

            int NextSeq(int channel)
            {
                if (!channelSeq.TryGetValue(channel, out var n)) n = 0;
                channelSeq[channel] = n + 1;
                return n + 1;
            }

            var mainFeatures = OrderFeatures(features.Where(f => f.Side == SpindleSide.Main), template.MainSpindleOrder);
            var subFeatures = OrderFeatures(features.Where(f => f.Side == SpindleSide.Sub), template.SubSpindleOrder);

            // Track how far along the part the tool is working, which is what governs
            // deflection on a Swiss machine.
            double axialPosition = 0;

            foreach (var feature in mainFeatures)
            {
                for (int instance = 0; instance < Math.Max(1, feature.Instances); instance++)
                {
                    var op = BuildOperation(feature, instance, part, material, tools, machine, template,
                                            plan, axialPosition, NextSeq);
                    if (op != null) plan.Operations.Add(op);
                }

                if (IsAxial(feature.Kind)) axialPosition += feature.LengthMm;
            }

            // ---------------------------------------------------------- sub side
            // With overlap on, back-working runs on the previous part while the main spindle
            // starts the next one. This is where a Swiss machine earns its price, and it means
            // the back operations are sequenced BEFORE the transfer, not after it.
            var subOps = new List<OperationSpec>();
            double subAxial = 0;
            foreach (var feature in subFeatures)
            {
                for (int instance = 0; instance < Math.Max(1, feature.Instances); instance++)
                {
                    var op = BuildOperation(feature, instance, part, material, tools, machine, template,
                                            plan, subAxial, NextSeq);
                    if (op != null) subOps.Add(op);
                }
                if (IsAxial(feature.Kind)) subAxial += feature.LengthMm;
            }
            plan.Operations.AddRange(subOps);

            // ---------------------------------------------------------- transfer, cutoff, bar feed
            string transferLabel = template.WaitLabelPrefix + template.FirstWaitNumber;

            int mainChannel = 0;
            int subChannel = template.StationChannel.TryGetValue(ToolStation.BackWorking, out var sc)
                ? Math.Min(sc, Math.Max(0, machine.ChannelCount - 1))
                : Math.Min(1, Math.Max(0, machine.ChannelCount - 1));

            bool hasSubWork = subOps.Count > 0;

            if (machine.HasSubSpindle && hasSubWork && subChannel != mainChannel)
            {
                // Both sides rendezvous before the sub spindle comes forward.
                plan.Operations.Add(new OperationSpec
                {
                    Id = "sync-transfer-sub",
                    Name = "Wait for main spindle before transfer",
                    Type = OperationType.Custom,
                    Channel = subChannel,
                    Side = SpindleSide.Sub,
                    Sequence = NextSeq(subChannel),
                    WaitBefore = transferLabel,
                    IsSyncMarker = true
                });
            }

            var transfer = new OperationSpec
            {
                Id = "transfer",
                Name = "Sub spindle grip and part transfer",
                Type = OperationType.Transfer,
                Channel = mainChannel,
                Side = SpindleSide.Main,
                Sequence = NextSeq(mainChannel),
                WaitBefore = (machine.HasSubSpindle && hasSubWork && subChannel != mainChannel) ? transferLabel : null
            };

            if (machine.HasSubSpindle)
                plan.Operations.Add(transfer);
            else
                plan.Advisories.Add(new Advisory(Severity.Info, "NO_SUB_SPINDLE",
                    "This machine has no sub spindle, so no transfer is planned. Any back-working " +
                    "features will need a second operation off the machine."));

            var cutoffTool = tools.Select(OperationType.Cutoff, SpindleSide.Main);
            if (cutoffTool == null)
            {
                plan.Advisories.Add(new Advisory(Severity.Critical, "NO_CUTOFF_TOOL",
                    "No cutoff tool in the library. Every bar job needs one."));
            }
            else
            {
                RegisterTool(plan, cutoffTool);

                var rec = _feeds.Recommend(new FeedSpeedRequest
                {
                    Material = material,
                    Tool = cutoffTool,
                    Machine = machine,
                    Operation = OperationType.Cutoff,
                    WorkDiameterMm = part.Stock.DiameterMm,
                    CutLengthMm = part.Stock.DiameterMm / 2.0,
                    GuideBushingEngaged = machine.HasGuideBushing
                });

                var cutoff = new OperationSpec
                {
                    Id = "cutoff",
                    Name = $"Cut off ({cutoffTool.WidthMm:F2} mm blade)",
                    Type = OperationType.Cutoff,
                    Channel = mainChannel,
                    Side = SpindleSide.Main,
                    Sequence = NextSeq(mainChannel),
                    ToolId = cutoffTool.Id,
                    Rpm = rec.Rpm,
                    SurfaceSpeedMPerMin = rec.SurfaceSpeedMPerMin,
                    FeedMmPerRev = rec.FeedMmPerRev,
                    WorkDiameterMm = part.Stock.DiameterMm,
                    CutLengthMm = part.Stock.DiameterMm / 2.0,
                    DepthOfCutMm = cutoffTool.WidthMm,
                    ApproachLengthMm = template.ApproachLengthMm
                };
                foreach (var a in rec.Advisories) cutoff.Warnings.Add(a.ToString());
                plan.Operations.Add(cutoff);

                if (Math.Abs(cutoffTool.WidthMm - part.CutoffWidthMm) > 0.01)
                    plan.Advisories.Add(new Advisory(Severity.Warning, "CUTOFF_WIDTH_MISMATCH",
                        $"The part spec assumes a {part.CutoffWidthMm:F2} mm kerf but the selected blade " +
                        $"is {cutoffTool.WidthMm:F2} mm. Material cost per part is wrong by the difference."));
            }

            plan.Operations.Add(new OperationSpec
            {
                Id = "barfeed",
                Name = "Advance bar to stop",
                Type = OperationType.BarFeed,
                Channel = mainChannel,
                Side = SpindleSide.Main,
                Sequence = NextSeq(mainChannel)
            });

            // ---------------------------------------------------------- time it
            _timer.ComputeAllOperationTimes(plan.Operations, machine);
            plan.Cycle = _timer.Schedule(plan.Operations, machine);

            foreach (var a in plan.Cycle.Advisories) plan.Advisories.Add(a);

            AddPlanAdvisories(plan, part, machine, template, hasSubWork);
            return plan;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// True for operations where the bar's own overhang past the guide bushing governs
        /// how hard you can cut: single-point work on the outside of the part. False for
        /// anything where the tool is the flexible member.
        /// </summary>
        private static bool WorkpieceSlendernessApplies(OperationType t)
        {
            switch (t)
            {
                case OperationType.TurnRough:
                case OperationType.TurnFinish:
                case OperationType.BackTurn:
                case OperationType.Groove:
                case OperationType.Thread:
                case OperationType.Knurl:
                case OperationType.Polygon:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsAxial(OperationType t) =>
            t == OperationType.TurnRough || t == OperationType.TurnFinish ||
            t == OperationType.BackTurn || t == OperationType.Groove || t == OperationType.Thread;

        private static IEnumerable<PartFeature> OrderFeatures(IEnumerable<PartFeature> features, List<OperationType> order)
        {
            var list = features.ToList();
            return list.OrderBy(f =>
            {
                int i = order.IndexOf(f.Kind);
                return i < 0 ? order.Count : i;
            }).ToList();
        }

        private static void RegisterTool(JobPlan plan, ToolItem tool)
        {
            if (tool != null && !plan.ToolsUsed.Any(t => t.Id == tool.Id)) plan.ToolsUsed.Add(tool);
        }

        private OperationSpec BuildOperation(PartFeature feature, int instance, PartSpec part,
                                             Material material, ToolLibrary tools, MachineProfile machine,
                                             SetupTemplate template, JobPlan plan, double axialPosition,
                                             Func<int, int> nextSeq)
        {
            var tool = tools.Select(feature.Kind, feature.Side, feature.DiameterMm, feature.ThreadPitchMm);
            if (tool == null)
            {
                plan.Advisories.Add(new Advisory(Severity.Critical, "NO_TOOL",
                    $"No tool in the library can perform '{feature.Label}' " +
                    $"({feature.Kind} on the {feature.Side.ToString().ToLowerInvariant()} spindle" +
                    (feature.DiameterMm > 0 ? $", {feature.DiameterMm:F2} mm" : "") + "). " +
                    "The operation was skipped, so the cycle time below is short by however long it takes."));
                return null;
            }

            RegisterTool(plan, tool);

            int channel = template.ChannelFor(tool, machine);

            double workDiameter = feature.Kind == OperationType.Drill ||
                                  feature.Kind == OperationType.BackDrill ||
                                  feature.Kind == OperationType.CrossDrill ||
                                  feature.Kind == OperationType.Ream ||
                                  feature.Kind == OperationType.Tap ||
                                  feature.Kind == OperationType.BackTap ||
                                  feature.Kind == OperationType.Mill
                ? (tool.DiameterMm > 0 ? tool.DiameterMm : feature.DiameterMm)
                : (feature.StartDiameterMm > 0 ? feature.StartDiameterMm : feature.DiameterMm);

            var request = new FeedSpeedRequest
            {
                Material = material,
                Tool = tool,
                Machine = machine,
                Operation = feature.Kind,
                Side = feature.Side,
                WorkDiameterMm = workDiameter,
                RadialStockMm = feature.RadialStockMm,
                CutLengthMm = feature.LengthMm,
                TargetRaMicron = feature.SurfaceFinishRa,
                ToleranceMm = feature.ToleranceMm,
                ThreadPitchMm = feature.ThreadPitchMm,
                // Slenderness derating models the WORKPIECE pushing away from the tool, which
                // only happens when the tool is working the outside of a bar sticking out of
                // the guide bushing. For drilling, tapping, milling and cross-work the flexible
                // member is the tool, and that is already handled by its stickout factor.
                // Applying both would derate a 2 mm cross drill to a third of its feed because
                // the part happens to be long, which is nonsense.
                UnsupportedLengthMm = WorkpieceSlendernessApplies(feature.Kind)
                    ? (feature.Side == SpindleSide.Main ? axialPosition + feature.LengthMm : feature.LengthMm)
                    : 0,
                GuideBushingEngaged = machine.HasGuideBushing && feature.Side == SpindleSide.Main
            };

            var rec = _feeds.Recommend(request);

            var op = new OperationSpec
            {
                Id = feature.Id + (feature.Instances > 1 ? $"-{instance + 1}" : ""),
                Name = string.IsNullOrEmpty(feature.Label)
                    ? $"{feature.Kind} {feature.DiameterMm:F2}"
                    : feature.Label + (feature.Instances > 1 ? $" ({instance + 1} of {feature.Instances})" : ""),
                Type = feature.Kind,
                Channel = channel,
                Side = feature.Side,
                ToolId = tool.Id,
                FeatureId = feature.Id,
                Sequence = nextSeq(channel),
                Rpm = rec.Rpm,
                SurfaceSpeedMPerMin = rec.SurfaceSpeedMPerMin,
                FeedMmPerRev = rec.FeedMmPerRev,
                DepthOfCutMm = rec.DepthOfCutMm,
                Passes = rec.Passes,
                CutLengthMm = feature.LengthMm,
                WorkDiameterMm = workDiameter,
                ApproachLengthMm = template.ApproachLengthMm,
                ConstantSurfaceSpeed = feature.Kind == OperationType.Face || feature.Kind == OperationType.Cutoff,
                Note = feature.Note
            };

            foreach (var a in rec.Advisories)
            {
                op.Warnings.Add(a.ToString());
                if (a.Severity >= Severity.Error)
                    plan.Advisories.Add(new Advisory(a.Severity, a.Code,
                        $"{op.Name}: {a.Message}"));
            }

            return op;
        }

        private static void AddPlanAdvisories(JobPlan plan, PartSpec part, MachineProfile machine,
                                              SetupTemplate template, bool hasSubWork)
        {
            if (part.Stock.DiameterMm > machine.MaxBarDiameterMm && machine.MaxBarDiameterMm > 0)
                plan.Advisories.Add(new Advisory(Severity.Critical, "BAR_TOO_BIG",
                    $"{part.Stock.DiameterMm:F2} mm bar will not fit a machine rated for " +
                    $"{machine.MaxBarDiameterMm:F2} mm."));

            if (part.OverallLengthMm > machine.MaxZStrokePerPassMm && machine.MaxZStrokePerPassMm > 0)
                plan.Advisories.Add(new Advisory(Severity.Warning, "LONG_PART",
                    $"The part is {part.OverallLengthMm:F1} mm long against a {machine.MaxZStrokePerPassMm:F0} mm " +
                    "stroke. It will need re-gripping partway, which this plan does not model."));

            var usedChannels = plan.Operations.Select(o => o.Channel).Distinct().Count();
            if (usedChannels > 1)
                plan.Advisories.Add(new Advisory(Severity.Info, "NO_COLLISION_CHECK",
                    $"Work is spread across {usedChannels} channels on the assumption that those tool " +
                    "positions can be in the cut simultaneously. Verify that in the machine's kinematic " +
                    "model before you post — this planner works from a feature list, not from geometry."));

            if (hasSubWork && template.OverlapBackWorking)
                plan.Advisories.Add(new Advisory(Severity.Info, "OVERLAP_ASSUMED",
                    "Back-working is scheduled against main-spindle work on the following part, which is " +
                    "how a Swiss machine is meant to run. The first part off the bar will take longer than " +
                    "the steady-state cycle shown here."));

            var slowest = plan.Operations.OrderByDescending(o => o.TotalSeconds).FirstOrDefault();
            if (slowest != null && plan.Cycle != null && plan.Cycle.CycleSeconds > 0 &&
                slowest.TotalSeconds > plan.Cycle.CycleSeconds * 0.35)
                plan.Advisories.Add(new Advisory(Severity.Info, "DOMINANT_OPERATION",
                    $"'{slowest.Name}' is {slowest.TotalSeconds:F1} s of a {plan.Cycle.CycleSeconds:F1} s cycle " +
                    $"({slowest.TotalSeconds / plan.Cycle.CycleSeconds * 100:F0}%). If this cycle needs to come " +
                    "down, start here."));
        }
    }
}
