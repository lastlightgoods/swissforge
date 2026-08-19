using System;
using System.Collections.Generic;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>
    /// One programmed operation on one channel. This is the unit that gets scheduled,
    /// timed, costed, and eventually handed to ESPRIT to become a real toolpath.
    /// </summary>
    public sealed class OperationSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public OperationType Type { get; set; } = OperationType.TurnRough;

        /// <summary>Zero-based channel index. Channel 0 is $1.</summary>
        public int Channel { get; set; }

        public SpindleSide Side { get; set; } = SpindleSide.Main;

        public string ToolId { get; set; } = "";
        public string FeatureId { get; set; } = "";

        /// <summary>Sequence within its channel. Lower runs first.</summary>
        public int Sequence { get; set; }

        // ---- cutting parameters -------------------------------------------

        public double Rpm { get; set; }
        public double SurfaceSpeedMPerMin { get; set; }
        public double FeedMmPerRev { get; set; }
        public double DepthOfCutMm { get; set; }

        /// <summary>Number of passes at <see cref="DepthOfCutMm"/>.</summary>
        public int Passes { get; set; } = 1;

        /// <summary>Axial distance cut per pass, mm.</summary>
        public double CutLengthMm { get; set; }

        /// <summary>Diameter the tool is working at, mm. Drives RPM and surface speed.</summary>
        public double WorkDiameterMm { get; set; }

        /// <summary>Rapid distance travelled to get to and from the cut, mm.</summary>
        public double ApproachLengthMm { get; set; } = 5.0;

        /// <summary>Programmed dwell, seconds. Grooves and back-bores often need one.</summary>
        public double DwellSeconds { get; set; }

        /// <summary>True when the spindle runs in constant surface speed (G96).</summary>
        public bool ConstantSurfaceSpeed { get; set; }

        // ---- timing --------------------------------------------------------

        /// <summary>Time actually in the cut, seconds. Filled in by the cycle-time estimator.</summary>
        public double CutSeconds { get; set; }

        /// <summary>Rapids, indexes, dwells and spindle ramps, seconds.</summary>
        public double NonCutSeconds { get; set; }

        public double TotalSeconds => CutSeconds + NonCutSeconds;

        // ---- synchronisation ------------------------------------------------

        /// <summary>
        /// Waitcode this operation waits on before it may start. Null means no wait.
        /// On a Citizen this becomes an <c>!L</c> code, on a Star an M-code in the
        /// reserved wait range.
        /// </summary>
        public string WaitBefore { get; set; }

        /// <summary>Waitcode posted after this operation completes. Null means none.</summary>
        public string PostAfter { get; set; }

        /// <summary>
        /// True when this operation must have the whole machine to itself — a bar feed,
        /// a spindle transfer, or a cutoff. The scheduler will not overlap it.
        /// </summary>
        public bool IsExclusive =>
            Type == OperationType.Transfer || Type == OperationType.BarFeed || Type == OperationType.Cutoff;

        /// <summary>
        /// True when this entry exists only to hold a waitcode. It commands no motion and
        /// must not be charged tool-change or spindle-ramp time, or a program with several
        /// rendezvous accumulates seconds of cycle time that do not exist.
        /// </summary>
        public bool IsSyncMarker { get; set; }

        public string Note { get; set; } = "";

        /// <summary>Warnings raised while generating this operation (speed clamped, feed derated, etc.).</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("type", Type.ToString());
            j.Set("channel", Channel);
            j.Set("side", Side.ToString());
            j.Set("toolId", ToolId);
            j.Set("featureId", FeatureId);
            j.Set("sequence", Sequence);
            j.Set("rpm", Math.Round(Rpm, 1));
            j.Set("surfaceSpeedMPerMin", Math.Round(SurfaceSpeedMPerMin, 2));
            j.Set("feedMmPerRev", Math.Round(FeedMmPerRev, 5));
            j.Set("depthOfCutMm", Math.Round(DepthOfCutMm, 4));
            j.Set("passes", Passes);
            j.Set("cutLengthMm", Math.Round(CutLengthMm, 4));
            j.Set("workDiameterMm", Math.Round(WorkDiameterMm, 4));
            j.Set("approachLengthMm", ApproachLengthMm);
            j.Set("dwellSeconds", DwellSeconds);
            j.Set("constantSurfaceSpeed", ConstantSurfaceSpeed);
            j.Set("cutSeconds", Math.Round(CutSeconds, 4));
            j.Set("nonCutSeconds", Math.Round(NonCutSeconds, 4));
            j.Set("totalSeconds", Math.Round(TotalSeconds, 4));
            j.Set("waitBefore", WaitBefore);
            j.Set("postAfter", PostAfter);
            j.Set("isSyncMarker", IsSyncMarker);
            j.Set("note", Note);
            j.Set("warnings", Warnings);
            return j;
        }

        public static OperationSpec FromJson(JsonValue j)
        {
            var o = new OperationSpec
            {
                Id = j["id"].AsString(""),
                Name = j["name"].AsString(""),
                Type = j["type"].AsEnum(OperationType.TurnRough),
                Channel = j["channel"].AsInt(0),
                Side = j["side"].AsEnum(SpindleSide.Main),
                ToolId = j["toolId"].AsString(""),
                FeatureId = j["featureId"].AsString(""),
                Sequence = j["sequence"].AsInt(0),
                Rpm = j["rpm"].AsDouble(0),
                SurfaceSpeedMPerMin = j["surfaceSpeedMPerMin"].AsDouble(0),
                FeedMmPerRev = j["feedMmPerRev"].AsDouble(0),
                DepthOfCutMm = j["depthOfCutMm"].AsDouble(0),
                Passes = j["passes"].AsInt(1),
                CutLengthMm = j["cutLengthMm"].AsDouble(0),
                WorkDiameterMm = j["workDiameterMm"].AsDouble(0),
                ApproachLengthMm = j["approachLengthMm"].AsDouble(5),
                DwellSeconds = j["dwellSeconds"].AsDouble(0),
                ConstantSurfaceSpeed = j["constantSurfaceSpeed"].AsBool(false),
                CutSeconds = j["cutSeconds"].AsDouble(0),
                NonCutSeconds = j["nonCutSeconds"].AsDouble(0),
                WaitBefore = j["waitBefore"].IsNull ? null : j["waitBefore"].AsString(),
                PostAfter = j["postAfter"].IsNull ? null : j["postAfter"].AsString(),
                IsSyncMarker = j["isSyncMarker"].AsBool(false),
                Note = j["note"].AsString("")
            };
            foreach (var w in j["warnings"]) o.Warnings.Add(w.AsString(""));
            return o;
        }
    }
}
