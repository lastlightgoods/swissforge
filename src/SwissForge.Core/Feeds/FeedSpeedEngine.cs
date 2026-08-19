using System;
using System.Collections.Generic;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Units;

namespace SwissForge.Core.Feeds
{
    /// <summary>A note raised while computing cutting data.</summary>
    public sealed class Advisory
    {
        public Severity Severity { get; }
        public string Code { get; }
        public string Message { get; }

        public Advisory(Severity severity, string code, string message)
        {
            Severity = severity;
            Code = code;
            Message = message;
        }

        public override string ToString() => $"[{Severity}] {Code}: {Message}";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("severity", Severity.ToString());
            j.Set("code", Code);
            j.Set("message", Message);
            return j;
        }
    }

    /// <summary>Inputs to a cutting-data calculation.</summary>
    public sealed class FeedSpeedRequest
    {
        public Material Material { get; set; }
        public ToolItem Tool { get; set; }
        public MachineProfile Machine { get; set; }
        public OperationType Operation { get; set; } = OperationType.TurnRough;
        public SpindleSide Side { get; set; } = SpindleSide.Main;

        /// <summary>Diameter the cutting edge is working at, mm.</summary>
        public double WorkDiameterMm { get; set; }

        /// <summary>Total stock to remove on radius, mm. Drives pass count for turning.</summary>
        public double RadialStockMm { get; set; }

        /// <summary>Axial length of the cut, mm.</summary>
        public double CutLengthMm { get; set; }

        /// <summary>Print surface finish, Ra micrometres. 0 = unspecified.</summary>
        public double TargetRaMicron { get; set; }

        /// <summary>Print tolerance, mm. Tight tolerances trigger a finish pass and slower feed.</summary>
        public double ToleranceMm { get; set; } = 0.05;

        /// <summary>
        /// How far the cut sits ahead of the guide bushing, mm. On a Swiss machine this,
        /// not the part's overall length, is what governs deflection and chatter.
        /// </summary>
        public double UnsupportedLengthMm { get; set; }

        /// <summary>Thread pitch, mm, for thread/tap operations.</summary>
        public double ThreadPitchMm { get; set; }

        /// <summary>Set false to model a chucker (no guide bushing) setup.</summary>
        public bool GuideBushingEngaged { get; set; } = true;
    }

    /// <summary>Computed cutting data plus everything that was clamped or derated on the way.</summary>
    public sealed class FeedSpeedRecommendation
    {
        public double SurfaceSpeedMPerMin { get; set; }
        public double Rpm { get; set; }
        public double FeedMmPerRev { get; set; }
        public double FeedMmPerMin { get; set; }
        public double DepthOfCutMm { get; set; }
        public int Passes { get; set; } = 1;

        /// <summary>Predicted theoretical roughness from feed and nose radius, Ra micrometres.</summary>
        public double PredictedRaMicron { get; set; }

        /// <summary>Estimated spindle power at the cut, kW.</summary>
        public double SpindlePowerKw { get; set; }

        /// <summary>Estimated tangential cutting force, N. High force on a slender part means chatter.</summary>
        public double CuttingForceN { get; set; }

        /// <summary>Material removal rate, cm3/min.</summary>
        public double MrrCm3PerMin { get; set; }

        public List<Advisory> Advisories { get; } = new List<Advisory>();

        public bool HasBlocker
        {
            get
            {
                foreach (var a in Advisories)
                    if (a.Severity == Severity.Critical) return true;
                return false;
            }
        }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("surfaceSpeedMPerMin", Math.Round(SurfaceSpeedMPerMin, 2));
            j.Set("surfaceSpeedSfm", Math.Round(U.MPerMinToSfm(SurfaceSpeedMPerMin), 0));
            j.Set("rpm", Math.Round(Rpm, 0));
            j.Set("feedMmPerRev", Math.Round(FeedMmPerRev, 5));
            j.Set("feedInchPerRev", Math.Round(U.MmRevToIpr(FeedMmPerRev), 6));
            j.Set("feedMmPerMin", Math.Round(FeedMmPerMin, 2));
            j.Set("depthOfCutMm", Math.Round(DepthOfCutMm, 4));
            j.Set("passes", Passes);
            j.Set("predictedRaMicron", double.IsInfinity(PredictedRaMicron) ? 0 : Math.Round(PredictedRaMicron, 3));
            j.Set("spindlePowerKw", Math.Round(SpindlePowerKw, 3));
            j.Set("cuttingForceN", Math.Round(CuttingForceN, 1));
            j.Set("mrrCm3PerMin", Math.Round(MrrCm3PerMin, 3));
            var adv = JsonValue.Arr();
            foreach (var a in Advisories) adv.Add(a.ToJson());
            j["advisories"] = adv;
            return j;
        }
    }

    /// <summary>
    /// Turns a material, a tool, and a feature into runnable cutting data.
    /// <para>
    /// The model is a conventional Kienzle specific-cutting-force treatment wrapped in
    /// Swiss-specific derates. The parts worth understanding before you trust a number:
    /// </para>
    /// <list type="bullet">
    /// <item>Speed starts from the material's baseline for coated carbide, corrected for
    /// delivered hardness, then scaled by tool substrate and operation type.</item>
    /// <item>Feed starts from tool geometry (nose radius for turning, diameter for drilling,
    /// pitch for threading), then gets multiplied by the material's feed factor and derated
    /// for stickout and unsupported length.</item>
    /// <item>Everything is clamped by machine RPM, spindle power, and — the one that catches
    /// people out on Swiss work — the part's own slenderness ahead of the guide bushing.</item>
    /// </list>
    /// <para>
    /// Every clamp and derate is reported as an <see cref="Advisory"/>. A recommendation that
    /// arrives with no advisories is one the machine can actually run as printed.
    /// </para>
    /// </summary>
    public sealed class FeedSpeedEngine
    {
        /// <summary>
        /// Kienzle reference specific cutting force kc1.1 in N/mm2 at 1 mm chip thickness,
        /// with the chip-thickness exponent mc. Standard tabulated values by material family.
        /// </summary>
        private static void KienzleConstants(MaterialGroup g, out double kc11, out double mc)
        {
            switch (g)
            {
                case MaterialGroup.FreeMachiningSteel: kc11 = 1500; mc = 0.24; return;
                case MaterialGroup.CarbonSteel: kc11 = 1750; mc = 0.25; return;
                case MaterialGroup.AlloySteel: kc11 = 2000; mc = 0.26; return;
                case MaterialGroup.StainlessAustenitic: kc11 = 2200; mc = 0.24; return;
                case MaterialGroup.StainlessMartensitic: kc11 = 2000; mc = 0.25; return;
                case MaterialGroup.Aluminum: kc11 = 800; mc = 0.23; return;
                case MaterialGroup.Brass: kc11 = 700; mc = 0.20; return;
                case MaterialGroup.Copper: kc11 = 900; mc = 0.22; return;
                case MaterialGroup.Titanium: kc11 = 1400; mc = 0.23; return;
                case MaterialGroup.Nickel: kc11 = 2700; mc = 0.27; return;
                case MaterialGroup.Plastic: kc11 = 250; mc = 0.20; return;
                default: kc11 = 1800; mc = 0.25; return;
            }
        }

        /// <summary>
        /// Speed multiplier by operation. Drilling and threading run well below turning speed;
        /// finishing runs above roughing because the chip load is lighter.
        /// </summary>
        private static double OperationSpeedFactor(OperationType t)
        {
            switch (t)
            {
                case OperationType.Face:
                case OperationType.TurnRough:
                case OperationType.BackTurn: return 1.00;
                case OperationType.TurnFinish: return 1.20;
                case OperationType.Bore: return 0.80;
                case OperationType.Groove: return 0.60;
                case OperationType.Cutoff: return 0.55;
                case OperationType.Thread: return 0.65;
                case OperationType.Drill:
                case OperationType.BackDrill:
                case OperationType.CrossDrill: return 0.55;
                case OperationType.Ream: return 0.40;
                case OperationType.Tap:
                case OperationType.BackTap: return 0.25;
                case OperationType.Mill: return 0.75;
                case OperationType.Knurl: return 0.15;
                case OperationType.Broach: return 0.20;
                case OperationType.Polygon: return 0.50;
                default: return 1.00;
            }
        }

        /// <summary>The main entry point.</summary>
        public FeedSpeedRecommendation Recommend(FeedSpeedRequest r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (r.Material == null) throw new ArgumentException("FeedSpeedRequest.Material is required.");
            if (r.Tool == null) throw new ArgumentException("FeedSpeedRequest.Tool is required.");

            var machine = r.Machine ?? new MachineProfile();
            var result = new FeedSpeedRecommendation();

            double dia = r.WorkDiameterMm > 0 ? r.WorkDiameterMm
                       : r.Tool.DiameterMm > 0 ? r.Tool.DiameterMm
                       : 0;

            if (dia <= 0)
            {
                result.Advisories.Add(new Advisory(Severity.Critical, "NO_DIAMETER",
                    "No work diameter and no tool diameter — cannot compute a spindle speed."));
                return result;
            }

            // ---------------------------------------------------------- speed
            double vc = r.Tool.RecommendedSpeedMPerMin > 0
                ? r.Tool.RecommendedSpeedMPerMin
                : r.Material.EffectiveBaseSpeed()
                  * r.Tool.SubstrateSpeedFactor()
                  * OperationSpeedFactor(r.Operation);

            if (r.Tool.RecommendedSpeedMPerMin > 0)
                result.Advisories.Add(new Advisory(Severity.Info, "TOOL_SPEED_OVERRIDE",
                    $"Using the tool's own recommended speed of {vc:F0} m/min instead of the material table."));

            double rpm = U.Rpm(vc, dia);

            double rpmCeiling = r.Tool.IsDriven
                ? machine.LiveToolMaxRpm
                : (r.Side == SpindleSide.Sub ? machine.SubSpindleMaxRpm : machine.MainSpindleMaxRpm);

            if (r.Tool.MaxRpm > 0) rpmCeiling = Math.Min(rpmCeiling, r.Tool.MaxRpm);

            if (rpmCeiling > 0 && rpm > rpmCeiling)
            {
                result.Advisories.Add(new Advisory(Severity.Warning, "RPM_CLAMPED",
                    $"Ideal speed of {vc:F0} m/min needs {rpm:F0} rpm at {dia:F3} mm, above the " +
                    $"{rpmCeiling:F0} rpm limit. Clamped, so the cut runs at " +
                    $"{U.SurfaceSpeed(rpmCeiling, dia):F0} m/min. Expect longer cycle time, not a worse part."));
                rpm = rpmCeiling;
                vc = U.SurfaceSpeed(rpm, dia);
            }

            result.Rpm = rpm;
            result.SurfaceSpeedMPerMin = vc;

            // ---------------------------------------------------------- feed
            double feed = BaseFeed(r, result);
            feed *= r.Material.FeedFactor;

            double stickoutFactor = r.Tool.StickoutFeedFactor();
            if (stickoutFactor < 1.0)
            {
                feed *= stickoutFactor;
                result.Advisories.Add(new Advisory(Severity.Warning, "STICKOUT_DERATE",
                    $"Tool stickout is {r.Tool.StickoutMm:F1} mm on a {r.Tool.DiameterMm:F2} mm tool " +
                    $"({r.Tool.StickoutMm / Math.Max(r.Tool.DiameterMm, 0.0001):F1} diameters). " +
                    $"Feed derated to {stickoutFactor * 100:F0}% to keep deflection in check."));
            }

            double slenderFactor = SlendernessFactor(r, dia, result);
            feed *= slenderFactor;

            // Threading and tapping are pitch-locked; the derates above must not touch them.
            if (r.Operation == OperationType.Thread || r.Operation == OperationType.Tap || r.Operation == OperationType.BackTap)
            {
                double pitch = r.ThreadPitchMm > 0 ? r.ThreadPitchMm : r.Tool.ThreadPitchMm;
                if (pitch <= 0)
                {
                    result.Advisories.Add(new Advisory(Severity.Critical, "NO_PITCH",
                        "A thread or tap operation needs a pitch. Set ThreadPitchMm on the feature or the tool."));
                    return result;
                }
                feed = pitch;
                result.Advisories.Add(new Advisory(Severity.Info, "PITCH_LOCKED",
                    $"Feed locked to the {pitch:F3} mm thread pitch. Synchronised feed, not a free parameter."));
            }

            // Finish-pass feed ceiling from the print's surface finish
            if (r.TargetRaMicron > 0 && r.Tool.NoseRadiusMm > 0 && IsFinishing(r.Operation))
            {
                double feedForRa = U.FeedForRa(r.TargetRaMicron, r.Tool.NoseRadiusMm);
                if (feedForRa > 0 && feed > feedForRa)
                {
                    result.Advisories.Add(new Advisory(Severity.Info, "FEED_FOR_FINISH",
                        $"Feed pulled back from {feed:F4} to {feedForRa:F4} mm/rev to reach the " +
                        $"Ra {r.TargetRaMicron:F2} um called out on the print."));
                    feed = feedForRa;
                }
            }

            if (feed <= 0)
            {
                result.Advisories.Add(new Advisory(Severity.Critical, "NO_FEED",
                    "Computed feed collapsed to zero. Check tool geometry and material factors."));
                return result;
            }

            result.FeedMmPerRev = feed;
            result.FeedMmPerMin = U.FeedMmPerMin(feed, rpm);

            // ---------------------------------------------------------- depth of cut and passes
            ComputeDepthAndPasses(r, result);

            // ---------------------------------------------------------- force, power, finish
            KienzleConstants(r.Material.Group, out double kc11, out double mc);

            double chipThickness = Math.Max(result.FeedMmPerRev * 0.7071, 1e-4); // 45 deg lead approximation
            double kc = kc11 * Math.Pow(chipThickness, -mc);
            double ap = Math.Max(result.DepthOfCutMm, 1e-4);

            result.CuttingForceN = kc * ap * result.FeedMmPerRev;
            result.SpindlePowerKw = result.CuttingForceN * result.SurfaceSpeedMPerMin / 60000.0;
            result.MrrCm3PerMin = U.TurningMrrCm3PerMin(result.SurfaceSpeedMPerMin, ap, result.FeedMmPerRev);
            result.PredictedRaMicron = U.TheoreticalRaMicron(result.FeedMmPerRev, r.Tool.NoseRadiusMm);

            CheckPower(r, machine, result);
            CheckFinish(r, result);

            return result;
        }

        private static bool IsFinishing(OperationType t) =>
            t == OperationType.TurnFinish || t == OperationType.Bore ||
            t == OperationType.Ream || t == OperationType.BackTurn || t == OperationType.Face;

        /// <summary>Starting feed before any material or rigidity corrections.</summary>
        private static double BaseFeed(FeedSpeedRequest r, FeedSpeedRecommendation result)
        {
            if (r.Tool.RecommendedFeedMmPerRev > 0)
            {
                result.Advisories.Add(new Advisory(Severity.Info, "TOOL_FEED_OVERRIDE",
                    $"Using the tool's own recommended feed of {r.Tool.RecommendedFeedMmPerRev:F4} mm/rev."));
                return r.Tool.RecommendedFeedMmPerRev;
            }

            double nose = r.Tool.NoseRadiusMm > 0 ? r.Tool.NoseRadiusMm : 0.4;
            double dia = r.Tool.DiameterMm > 0 ? r.Tool.DiameterMm : r.WorkDiameterMm;

            switch (r.Operation)
            {
                // Turning: the workable ceiling is about half the nose radius before the
                // chip stops curling properly. Roughing lives near it, finishing well under.
                case OperationType.TurnRough:
                case OperationType.BackTurn:
                    return Math.Min(0.50 * nose, 0.30);

                case OperationType.Face:
                    return Math.Min(0.35 * nose, 0.20);

                case OperationType.TurnFinish:
                    return Math.Min(0.22 * nose, 0.12);

                case OperationType.Bore:
                    return Math.Min(0.18 * nose, 0.10);

                case OperationType.Ream:
                    return Math.Max(0.05, 0.04 * dia);

                // Drilling: feed scales with diameter. Micro drills below 3 mm get a
                // flatter curve or they snap.
                case OperationType.Drill:
                case OperationType.BackDrill:
                case OperationType.CrossDrill:
                    if (dia <= 0) return 0.02;
                    if (dia < 1.0) return 0.006 + 0.004 * dia;
                    if (dia < 3.0) return 0.010 * dia;
                    if (dia < 10.0) return 0.015 * dia;
                    return Math.Min(0.020 * dia, 0.35);

                // Grooving and cutoff: feed is a function of blade width, and the last
                // few tenths at centre want a slowdown the caller handles separately.
                case OperationType.Groove:
                    return Math.Max(0.02, 0.025 * Math.Max(r.Tool.WidthMm, 0.5));

                case OperationType.Cutoff:
                    return Math.Max(0.015, 0.020 * Math.Max(r.Tool.WidthMm, 0.5));

                case OperationType.Mill:
                {
                    // Milling is quoted per tooth, then converted to per-rev for the common pipeline.
                    double perTooth = dia < 1.0 ? 0.005
                                    : dia < 3.0 ? 0.010
                                    : dia < 6.0 ? 0.025
                                    : 0.040;
                    return perTooth * Math.Max(r.Tool.Flutes, 1);
                }

                case OperationType.Knurl: return 0.15;
                case OperationType.Broach: return 0.05;
                case OperationType.Polygon: return 0.05;

                case OperationType.Thread:
                case OperationType.Tap:
                case OperationType.BackTap:
                    return r.ThreadPitchMm > 0 ? r.ThreadPitchMm : r.Tool.ThreadPitchMm;

                default:
                    return Math.Min(0.30 * nose, 0.15);
            }
        }

        /// <summary>
        /// The Swiss-specific derate. A Swiss lathe supports the bar right at the cut, so what
        /// matters is not the part length but how far the tool is working ahead of the guide
        /// bushing. Past about three diameters of unsupported stock the part starts pushing
        /// away and the finish goes with it.
        /// </summary>
        private static double SlendernessFactor(FeedSpeedRequest r, double dia, FeedSpeedRecommendation result)
        {
            if (r.UnsupportedLengthMm <= 0 || dia <= 0) return 1.0;

            double ratio = r.UnsupportedLengthMm / dia;

            if (!r.GuideBushingEngaged)
            {
                // Chucker mode: the whole protrusion is a cantilever, so the limits arrive far sooner.
                if (ratio > 3.0)
                {
                    double f = Math.Max(0.25, 1.0 - (ratio - 3.0) * 0.20);
                    result.Advisories.Add(new Advisory(
                        ratio > 5.0 ? Severity.Error : Severity.Warning, "CHUCKER_SLENDER",
                        $"Running without the guide bushing at {ratio:F1} diameters of overhang. " +
                        $"Feed derated to {f * 100:F0}%. Consider the guide bushing, or a shorter " +
                        $"protrusion with more Z passes."));
                    return f;
                }
                return 1.0;
            }

            if (ratio <= 3.0) return 1.0;

            double factor = Math.Max(0.35, 1.0 - (ratio - 3.0) * 0.10);
            result.Advisories.Add(new Advisory(
                ratio > 8.0 ? Severity.Error : Severity.Warning, "SLENDER_DERATE",
                $"Cutting {r.UnsupportedLengthMm:F1} mm ahead of the guide bushing on {dia:F2} mm stock " +
                $"({ratio:F1} diameters). Feed derated to {factor * 100:F0}%. " +
                (ratio > 8.0
                    ? "This is chatter territory — break the cut into shorter Z steps and let the bushing catch up."
                    : "Watch for taper on the finished diameter.")));
            return factor;
        }

        private static void ComputeDepthAndPasses(FeedSpeedRequest r, FeedSpeedRecommendation result)
        {
            double stock = r.RadialStockMm;

            // Non-turning operations consume their stock in a single defined move.
            switch (r.Operation)
            {
                case OperationType.Drill:
                case OperationType.BackDrill:
                case OperationType.CrossDrill:
                case OperationType.Ream:
                case OperationType.Tap:
                case OperationType.BackTap:
                case OperationType.Knurl:
                case OperationType.Broach:
                case OperationType.Polygon:
                    result.DepthOfCutMm = Math.Max(r.Tool.DiameterMm / 2.0, 0.01);
                    result.Passes = 1;
                    return;

                case OperationType.Cutoff:
                    result.DepthOfCutMm = Math.Max(r.Tool.WidthMm, 0.5);
                    result.Passes = 1;
                    return;

                case OperationType.Groove:
                    result.DepthOfCutMm = stock > 0 ? stock : Math.Max(r.Tool.WidthMm, 0.5);
                    result.Passes = 1;
                    return;

                case OperationType.Thread:
                {
                    // Infeed passes for a single-point thread. Roughly proportional to depth,
                    // and never fewer than four or the last pass is doing all the work.
                    double pitch = r.ThreadPitchMm > 0 ? r.ThreadPitchMm : r.Tool.ThreadPitchMm;
                    double threadDepth = 0.6134 * Math.Max(pitch, 0.1);
                    int passes = Math.Max(4, (int)Math.Ceiling(threadDepth / 0.08));
                    result.DepthOfCutMm = threadDepth / passes;
                    result.Passes = passes;
                    return;
                }
            }

            if (stock <= 0)
            {
                result.DepthOfCutMm = 0.2;
                result.Passes = 1;
                return;
            }

            // Turning: pick a max depth of cut, then divide the stock into whole passes.
            double maxAp;
            switch (r.Operation)
            {
                case OperationType.TurnFinish:
                    maxAp = 0.25;
                    break;
                case OperationType.Face:
                    maxAp = 0.60;
                    break;
                case OperationType.Bore:
                    maxAp = 0.30;
                    break;
                default:
                    // Roughing depth is limited by the insert nose radius and, on small
                    // Swiss diameters, by the bar itself.
                    maxAp = Math.Min(2.0, Math.Max(0.3, r.Tool.NoseRadiusMm * 2.5));
                    maxAp = Math.Min(maxAp, Math.Max(0.2, r.WorkDiameterMm * 0.25));
                    break;
            }

            // A tight-tolerance feature keeps a dedicated spring-free finish pass in reserve.
            bool reserveFinish = r.ToleranceMm > 0 && r.ToleranceMm <= 0.025 && r.Operation == OperationType.TurnRough;
            double roughStock = reserveFinish ? Math.Max(0, stock - 0.15) : stock;

            int n = Math.Max(1, (int)Math.Ceiling(roughStock / maxAp - 1e-9));
            result.Passes = n;
            result.DepthOfCutMm = roughStock / n;

            if (reserveFinish)
                result.Advisories.Add(new Advisory(Severity.Info, "FINISH_STOCK_RESERVED",
                    $"Tolerance is +/-{r.ToleranceMm:F3} mm, so 0.15 mm of radial stock is left on " +
                    $"for a separate finish pass rather than roughed to size."));
        }

        private static void CheckPower(FeedSpeedRequest r, MachineProfile machine, FeedSpeedRecommendation result)
        {
            if (machine.MainSpindlePowerKw <= 0) return;

            // Assume 80% drivetrain efficiency between the motor rating and the cut.
            double available = machine.MainSpindlePowerKw * 0.80;
            if (result.SpindlePowerKw <= available) return;

            double over = result.SpindlePowerKw / available;
            result.Advisories.Add(new Advisory(
                over > 1.5 ? Severity.Error : Severity.Warning, "POWER_LIMIT",
                $"This cut wants about {result.SpindlePowerKw:F2} kW at the tool but the spindle can " +
                $"deliver roughly {available:F2} kW. Reduce depth of cut to about " +
                $"{result.DepthOfCutMm / over:F3} mm, or add a pass."));
        }

        private static void CheckFinish(FeedSpeedRequest r, FeedSpeedRecommendation result)
        {
            if (r.TargetRaMicron <= 0 || double.IsInfinity(result.PredictedRaMicron)) return;

            if (result.PredictedRaMicron > r.TargetRaMicron * 1.05)
            {
                double neededNose = result.FeedMmPerRev * result.FeedMmPerRev / (32.0 * r.TargetRaMicron / 1000.0);
                result.Advisories.Add(new Advisory(Severity.Error, "FINISH_UNREACHABLE",
                    $"At {result.FeedMmPerRev:F4} mm/rev with a {r.Tool.NoseRadiusMm:F2} mm nose the best " +
                    $"theoretical finish is Ra {result.PredictedRaMicron:F2} um, but the print calls for " +
                    $"Ra {r.TargetRaMicron:F2} um. Either slow the feed, or fit an insert with at least a " +
                    $"{neededNose:F2} mm nose radius."));
            }
        }
    }
}
