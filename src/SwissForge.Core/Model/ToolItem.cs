using System;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>A physical cutting tool loaded on the machine.</summary>
    public sealed class ToolItem
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>Vendor catalogue number, carried through to setup sheets and the ERP.</summary>
        public string CatalogNumber { get; set; } = "";

        public OperationType Purpose { get; set; } = OperationType.TurnRough;
        public ToolMaterial Substrate { get; set; } = ToolMaterial.CoatedCarbide;
        public ToolStation Station { get; set; } = ToolStation.GangSlide;
        public SpindleSide Side { get; set; } = SpindleSide.Main;

        /// <summary>Station number as programmed (T-number, or gang position).</summary>
        public int StationNumber { get; set; }

        /// <summary>Cutter diameter in mm. Zero for single-point turning tools.</summary>
        public double DiameterMm { get; set; }

        /// <summary>Insert nose radius in mm. Drives achievable finish.</summary>
        public double NoseRadiusMm { get; set; } = 0.4;

        /// <summary>Number of flutes/teeth. 1 for single-point.</summary>
        public int Flutes { get; set; } = 1;

        /// <summary>Usable flute/cutting length in mm.</summary>
        public double FluteLengthMm { get; set; }

        /// <summary>Overall stickout in mm. Long stickout derates feed.</summary>
        public double StickoutMm { get; set; }

        /// <summary>Machine or holder RPM ceiling for this tool. 0 = no explicit limit.</summary>
        public double MaxRpm { get; set; }

        /// <summary>Vendor's recommended surface speed override, m/min. 0 = derive from material.</summary>
        public double RecommendedSpeedMPerMin { get; set; }

        /// <summary>Vendor's recommended feed override, mm/rev. 0 = derive from material.</summary>
        public double RecommendedFeedMmPerRev { get; set; }

        /// <summary>Cost to replace one cutting edge (insert index, or amortised solid tool).</summary>
        public double CostPerEdge { get; set; }

        /// <summary>Parts expected from one edge in a nominal material. Drives per-part tooling cost.</summary>
        public double PartsPerEdge { get; set; } = 1000;

        /// <summary>Seconds to index or swap this tool when it wears out.</summary>
        public double EdgeChangeSeconds { get; set; } = 120;

        /// <summary>True if the tool is driven (live tooling).</summary>
        public bool IsDriven =>
            Station == ToolStation.LiveToolFront || Station == ToolStation.LiveToolBack;

        /// <summary>Thread pitch in mm for taps and thread mills. 0 otherwise.</summary>
        public double ThreadPitchMm { get; set; }

        /// <summary>Width in mm for grooving and cutoff blades.</summary>
        public double WidthMm { get; set; }

        /// <summary>Substrate speed multiplier relative to uncoated carbide.</summary>
        public double SubstrateSpeedFactor()
        {
            switch (Substrate)
            {
                case ToolMaterial.HSS: return 0.35;
                case ToolMaterial.Cobalt: return 0.45;
                case ToolMaterial.Carbide: return 1.00;
                case ToolMaterial.CoatedCarbide: return 1.35;
                case ToolMaterial.Cermet: return 1.50;
                case ToolMaterial.CBN: return 2.50;
                case ToolMaterial.PCD: return 3.00;
                default: return 1.00;
            }
        }

        /// <summary>
        /// Stickout derate. Deflection scales with (L/D)^3, so past 4 diameters of
        /// stickout we pull feed back hard. This is what stops the generated program
        /// from chattering a 1 mm drill 12 mm deep at catalogue feed.
        /// </summary>
        public double StickoutFeedFactor()
        {
            if (DiameterMm <= 0 || StickoutMm <= 0) return 1.0;
            double ratio = StickoutMm / DiameterMm;
            if (ratio <= 3.0) return 1.0;
            if (ratio >= 12.0) return 0.30;
            return Math.Max(0.30, 1.0 - (ratio - 3.0) * 0.078);
        }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("description", Description);
            j.Set("catalogNumber", CatalogNumber);
            j.Set("purpose", Purpose.ToString());
            j.Set("substrate", Substrate.ToString());
            j.Set("station", Station.ToString());
            j.Set("side", Side.ToString());
            j.Set("stationNumber", StationNumber);
            j.Set("diameterMm", DiameterMm);
            j.Set("noseRadiusMm", NoseRadiusMm);
            j.Set("flutes", Flutes);
            j.Set("fluteLengthMm", FluteLengthMm);
            j.Set("stickoutMm", StickoutMm);
            j.Set("maxRpm", MaxRpm);
            j.Set("recommendedSpeedMPerMin", RecommendedSpeedMPerMin);
            j.Set("recommendedFeedMmPerRev", RecommendedFeedMmPerRev);
            j.Set("costPerEdge", CostPerEdge);
            j.Set("partsPerEdge", PartsPerEdge);
            j.Set("edgeChangeSeconds", EdgeChangeSeconds);
            j.Set("threadPitchMm", ThreadPitchMm);
            j.Set("widthMm", WidthMm);
            return j;
        }

        public static ToolItem FromJson(JsonValue j) => new ToolItem
        {
            Id = j["id"].AsString(""),
            Description = j["description"].AsString(""),
            CatalogNumber = j["catalogNumber"].AsString(""),
            Purpose = j["purpose"].AsEnum(OperationType.TurnRough),
            Substrate = j["substrate"].AsEnum(ToolMaterial.CoatedCarbide),
            Station = j["station"].AsEnum(ToolStation.GangSlide),
            Side = j["side"].AsEnum(SpindleSide.Main),
            StationNumber = j["stationNumber"].AsInt(0),
            DiameterMm = j["diameterMm"].AsDouble(0),
            NoseRadiusMm = j["noseRadiusMm"].AsDouble(0.4),
            Flutes = j["flutes"].AsInt(1),
            FluteLengthMm = j["fluteLengthMm"].AsDouble(0),
            StickoutMm = j["stickoutMm"].AsDouble(0),
            MaxRpm = j["maxRpm"].AsDouble(0),
            RecommendedSpeedMPerMin = j["recommendedSpeedMPerMin"].AsDouble(0),
            RecommendedFeedMmPerRev = j["recommendedFeedMmPerRev"].AsDouble(0),
            CostPerEdge = j["costPerEdge"].AsDouble(0),
            PartsPerEdge = j["partsPerEdge"].AsDouble(1000),
            EdgeChangeSeconds = j["edgeChangeSeconds"].AsDouble(120),
            ThreadPitchMm = j["threadPitchMm"].AsDouble(0),
            WidthMm = j["widthMm"].AsDouble(0)
        };
    }
}
