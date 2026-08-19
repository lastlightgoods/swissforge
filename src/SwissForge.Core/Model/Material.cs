using System;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>
    /// A bar-stock material and everything the feed/speed and quoting engines
    /// need to know about it.
    /// </summary>
    public sealed class Material
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public MaterialGroup Group { get; set; } = MaterialGroup.Other;

        /// <summary>Typical delivered hardness, Brinell. Used to scale speed off the nominal.</summary>
        public double HardnessBhn { get; set; } = 150;

        /// <summary>Hardness the <see cref="BaseSurfaceSpeedMPerMin"/> figure was measured at.</summary>
        public double NominalHardnessBhn { get; set; } = 150;

        /// <summary>AISI B1112 = 100%. Informational; the speed tables do the real work.</summary>
        public double MachinabilityPercent { get; set; } = 100;

        /// <summary>Baseline turning speed for uncoated carbide at nominal hardness, m/min.</summary>
        public double BaseSurfaceSpeedMPerMin { get; set; } = 150;

        /// <summary>Density, g/cm3. Drives bar mass and therefore material cost.</summary>
        public double DensityGPerCm3 { get; set; } = 7.85;

        /// <summary>Purchased cost per kilogram of bar.</summary>
        public double CostPerKg { get; set; } = 0;

        /// <summary>
        /// Multiplier on tool life. Free-machining brass is kind to edges (&gt;1),
        /// Inconel is not (&lt;1). Feeds the per-part tooling cost in the quote.
        /// </summary>
        public double ToolLifeFactor { get; set; } = 1.0;

        /// <summary>
        /// Multiplier on feed rate. Gummy materials want less feed per rev for the
        /// same finish; free-machining materials tolerate more.
        /// </summary>
        public double FeedFactor { get; set; } = 1.0;

        /// <summary>True for materials that make long stringy chips and need chip-break cycles.</summary>
        public bool RequiresChipBreaking { get; set; }

        /// <summary>Free-text note surfaced in the UI, e.g. "work hardens - do not dwell".</summary>
        public string Note { get; set; } = "";

        /// <summary>
        /// Hardness correction. Cutting speed falls roughly with the inverse of hardness;
        /// the exponent is softened to 0.8 because the pure inverse over-corrects on the
        /// soft end and produces speeds nobody would actually run.
        /// </summary>
        public double HardnessSpeedFactor()
        {
            if (HardnessBhn <= 0 || NominalHardnessBhn <= 0) return 1.0;
            return Math.Pow(NominalHardnessBhn / HardnessBhn, 0.8);
        }

        /// <summary>Effective baseline speed for this material as delivered.</summary>
        public double EffectiveBaseSpeed() => BaseSurfaceSpeedMPerMin * HardnessSpeedFactor();

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("group", Group.ToString());
            j.Set("hardnessBhn", HardnessBhn);
            j.Set("nominalHardnessBhn", NominalHardnessBhn);
            j.Set("machinabilityPercent", MachinabilityPercent);
            j.Set("baseSurfaceSpeedMPerMin", BaseSurfaceSpeedMPerMin);
            j.Set("densityGPerCm3", DensityGPerCm3);
            j.Set("costPerKg", CostPerKg);
            j.Set("toolLifeFactor", ToolLifeFactor);
            j.Set("feedFactor", FeedFactor);
            j.Set("requiresChipBreaking", RequiresChipBreaking);
            j.Set("note", Note);
            return j;
        }

        public static Material FromJson(JsonValue j) => new Material
        {
            Id = j["id"].AsString(""),
            Name = j["name"].AsString(""),
            Group = j["group"].AsEnum(MaterialGroup.Other),
            HardnessBhn = j["hardnessBhn"].AsDouble(150),
            NominalHardnessBhn = j["nominalHardnessBhn"].AsDouble(j["hardnessBhn"].AsDouble(150)),
            MachinabilityPercent = j["machinabilityPercent"].AsDouble(100),
            BaseSurfaceSpeedMPerMin = j["baseSurfaceSpeedMPerMin"].AsDouble(150),
            DensityGPerCm3 = j["densityGPerCm3"].AsDouble(7.85),
            CostPerKg = j["costPerKg"].AsDouble(0),
            ToolLifeFactor = j["toolLifeFactor"].AsDouble(1.0),
            FeedFactor = j["feedFactor"].AsDouble(1.0),
            RequiresChipBreaking = j["requiresChipBreaking"].AsBool(false),
            Note = j["note"].AsString("")
        };
    }
}
