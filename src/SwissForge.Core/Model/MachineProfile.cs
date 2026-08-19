using System.Collections.Generic;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>
    /// A Swiss-type lathe as SwissForge models it: capacity limits, channel count,
    /// motion rates and the shop rate used to price time on it.
    /// </summary>
    public sealed class MachineProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Builder { get; set; } = "";
        public ControlDialect Dialect { get; set; } = ControlDialect.FanucGeneric;

        /// <summary>Maximum bar diameter through the guide bushing, mm.</summary>
        public double MaxBarDiameterMm { get; set; } = 20.0;

        /// <summary>Maximum Z stroke per gripping pass without re-chucking, mm.</summary>
        public double MaxZStrokePerPassMm { get; set; } = 200.0;

        /// <summary>
        /// Number of independently programmed channels ($1, $2, $3...). Two is common on
        /// entry Swiss, three on 20-32 mm class, four or more on the big Citizen/Star boxes.
        /// </summary>
        public int ChannelCount { get; set; } = 2;

        public bool HasSubSpindle { get; set; } = true;
        public bool HasGuideBushing { get; set; } = true;

        /// <summary>True if the machine can run guide-bushing-less for short parts.</summary>
        public bool SupportsChucker { get; set; } = true;

        public bool HasHighPressureCoolant { get; set; }
        public bool HasPartsConveyor { get; set; } = true;

        public double MainSpindleMaxRpm { get; set; } = 10000;
        public double SubSpindleMaxRpm { get; set; } = 8000;
        public double LiveToolMaxRpm { get; set; } = 6000;

        /// <summary>Main spindle power, kW. Used to sanity-check heavy roughing passes.</summary>
        public double MainSpindlePowerKw { get; set; } = 3.7;

        /// <summary>Rapid traverse, mm/min. Drives the non-cutting portion of cycle time.</summary>
        public double RapidRateMmPerMin { get; set; } = 32000;

        /// <summary>Seconds lost to a tool index/approach, averaged.</summary>
        public double ToolChangeSeconds { get; set; } = 0.4;

        /// <summary>Seconds for the sub spindle to advance, grip, and the main to release.</summary>
        public double TransferSeconds { get; set; } = 2.5;

        /// <summary>Seconds to feed a new bar length after cutoff.</summary>
        public double BarFeedSeconds { get; set; } = 1.2;

        /// <summary>Seconds to load a fresh bar in the magazine when the old one runs out.</summary>
        public double BarChangeSeconds { get; set; } = 45;

        /// <summary>Spindle accel/decel penalty per speed change, seconds.</summary>
        public double SpindleRampSeconds { get; set; } = 0.35;

        /// <summary>Fully burdened shop rate for this machine, currency per hour.</summary>
        public double HourlyRate { get; set; } = 75.0;

        /// <summary>Fraction of the shift the machine actually runs (uptime after jams, bar changes, breaks).</summary>
        public double UtilizationFactor { get; set; } = 0.85;

        /// <summary>Hours per day the machine is staffed or lights-out capable.</summary>
        public double ProductiveHoursPerDay { get; set; } = 20.0;

        /// <summary>Named channels for program generation, e.g. ["$1","$2","$3"].</summary>
        public List<string> ChannelNames { get; set; } = new List<string> { "$1", "$2" };

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("builder", Builder);
            j.Set("dialect", Dialect.ToString());
            j.Set("maxBarDiameterMm", MaxBarDiameterMm);
            j.Set("maxZStrokePerPassMm", MaxZStrokePerPassMm);
            j.Set("channelCount", ChannelCount);
            j.Set("hasSubSpindle", HasSubSpindle);
            j.Set("hasGuideBushing", HasGuideBushing);
            j.Set("supportsChucker", SupportsChucker);
            j.Set("hasHighPressureCoolant", HasHighPressureCoolant);
            j.Set("hasPartsConveyor", HasPartsConveyor);
            j.Set("mainSpindleMaxRpm", MainSpindleMaxRpm);
            j.Set("subSpindleMaxRpm", SubSpindleMaxRpm);
            j.Set("liveToolMaxRpm", LiveToolMaxRpm);
            j.Set("mainSpindlePowerKw", MainSpindlePowerKw);
            j.Set("rapidRateMmPerMin", RapidRateMmPerMin);
            j.Set("toolChangeSeconds", ToolChangeSeconds);
            j.Set("transferSeconds", TransferSeconds);
            j.Set("barFeedSeconds", BarFeedSeconds);
            j.Set("barChangeSeconds", BarChangeSeconds);
            j.Set("spindleRampSeconds", SpindleRampSeconds);
            j.Set("hourlyRate", HourlyRate);
            j.Set("utilizationFactor", UtilizationFactor);
            j.Set("productiveHoursPerDay", ProductiveHoursPerDay);
            j.Set("channelNames", ChannelNames);
            return j;
        }

        public static MachineProfile FromJson(JsonValue j)
        {
            var m = new MachineProfile
            {
                Id = j["id"].AsString(""),
                Name = j["name"].AsString(""),
                Builder = j["builder"].AsString(""),
                Dialect = j["dialect"].AsEnum(ControlDialect.FanucGeneric),
                MaxBarDiameterMm = j["maxBarDiameterMm"].AsDouble(20),
                MaxZStrokePerPassMm = j["maxZStrokePerPassMm"].AsDouble(200),
                ChannelCount = j["channelCount"].AsInt(2),
                HasSubSpindle = j["hasSubSpindle"].AsBool(true),
                HasGuideBushing = j["hasGuideBushing"].AsBool(true),
                SupportsChucker = j["supportsChucker"].AsBool(true),
                HasHighPressureCoolant = j["hasHighPressureCoolant"].AsBool(false),
                HasPartsConveyor = j["hasPartsConveyor"].AsBool(true),
                MainSpindleMaxRpm = j["mainSpindleMaxRpm"].AsDouble(10000),
                SubSpindleMaxRpm = j["subSpindleMaxRpm"].AsDouble(8000),
                LiveToolMaxRpm = j["liveToolMaxRpm"].AsDouble(6000),
                MainSpindlePowerKw = j["mainSpindlePowerKw"].AsDouble(3.7),
                RapidRateMmPerMin = j["rapidRateMmPerMin"].AsDouble(32000),
                ToolChangeSeconds = j["toolChangeSeconds"].AsDouble(0.4),
                TransferSeconds = j["transferSeconds"].AsDouble(2.5),
                BarFeedSeconds = j["barFeedSeconds"].AsDouble(1.2),
                BarChangeSeconds = j["barChangeSeconds"].AsDouble(45),
                SpindleRampSeconds = j["spindleRampSeconds"].AsDouble(0.35),
                HourlyRate = j["hourlyRate"].AsDouble(75),
                UtilizationFactor = j["utilizationFactor"].AsDouble(0.85),
                ProductiveHoursPerDay = j["productiveHoursPerDay"].AsDouble(20)
            };
            if (j["channelNames"].Count > 0)
            {
                m.ChannelNames = new List<string>();
                foreach (var c in j["channelNames"]) m.ChannelNames.Add(c.AsString(""));
            }
            return m;
        }
    }
}
