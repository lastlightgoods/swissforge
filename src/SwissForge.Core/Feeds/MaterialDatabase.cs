using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Core.Feeds
{
    /// <summary>
    /// The shop's material library.
    /// <para>
    /// The built-in entries are starting points drawn from common carbide-tooling
    /// practice for Swiss-type work. Cutting data is not physics — it is a negotiated
    /// truce between your tooling vendor, your coolant, and your machine's rigidity.
    /// Treat the defaults as a first pass and override them from your own proven jobs.
    /// </para>
    /// <para>
    /// Material <b>costs</b> in the built-in set are flagged indicative and default to
    /// zero effect: the quoting engine refuses to price a job off a made-up metal price
    /// and raises a blocking warning instead. Load your real purchase prices before
    /// quoting anything you intend to send to a customer.
    /// </para>
    /// </summary>
    public sealed class MaterialDatabase
    {
        private readonly Dictionary<string, Material> _byId =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ids whose CostPerKg came from the built-in table rather than the shop's data.</summary>
        public HashSet<string> IndicativeCostIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<Material> All => _byId.Values.ToList();
        public int Count => _byId.Count;

        public Material Get(string id)
        {
            if (id != null && _byId.TryGetValue(id, out var m)) return m;
            return null;
        }

        public Material GetOrThrow(string id)
        {
            var m = Get(id);
            if (m == null)
                throw new KeyNotFoundException(
                    $"Material '{id}' is not in the library. Known: {string.Join(", ", _byId.Keys.OrderBy(k => k))}");
            return m;
        }

        public void Add(Material m, bool indicativeCost = false)
        {
            if (m == null || string.IsNullOrEmpty(m.Id)) throw new ArgumentException("Material needs an Id.");
            _byId[m.Id] = m;
            if (indicativeCost) IndicativeCostIds.Add(m.Id);
            else IndicativeCostIds.Remove(m.Id);
        }

        public bool CostIsIndicative(string id) => IndicativeCostIds.Contains(id);

        /// <summary>Find by fuzzy name, e.g. "303" or "stainless 303" -> SS303.</summary>
        public Material Find(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            var direct = Get(query);
            if (direct != null) return direct;

            var q = query.Trim();
            return _byId.Values.FirstOrDefault(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
                ?? _byId.Values.FirstOrDefault(m => m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? _byId.Values.FirstOrDefault(m => m.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ------------------------------------------------------------------ built-ins

        /// <summary>
        /// A library covering what actually runs on Swiss machines day to day.
        /// Speeds are for coated-carbide turning at the material's nominal hardness;
        /// the feed/speed engine applies substrate, operation, and hardness corrections.
        /// </summary>
        public static MaterialDatabase CreateDefault()
        {
            var db = new MaterialDatabase();

            void M(string id, string name, MaterialGroup g, double bhn, double vc, double density,
                   double machinability, double toolLife, double feedFactor, double costPerKg,
                   bool chipBreak = false, string note = "")
            {
                db.Add(new Material
                {
                    Id = id,
                    Name = name,
                    Group = g,
                    HardnessBhn = bhn,
                    NominalHardnessBhn = bhn,
                    BaseSurfaceSpeedMPerMin = vc,
                    DensityGPerCm3 = density,
                    MachinabilityPercent = machinability,
                    ToolLifeFactor = toolLife,
                    FeedFactor = feedFactor,
                    CostPerKg = costPerKg,
                    RequiresChipBreaking = chipBreak,
                    Note = note
                }, indicativeCost: true);
            }

            // --- free machining and carbon steels
            M("12L14", "12L14 leaded free-machining steel", MaterialGroup.FreeMachiningSteel,
              163, 200, 7.87, 160, 1.35, 1.20, 1.60, false,
              "The Swiss shop's bread and butter. Holds size, breaks chips, easy on edges.");
            M("1215", "1215 resulphurised free-machining steel", MaterialGroup.FreeMachiningSteel,
              167, 190, 7.87, 136, 1.25, 1.15, 1.55);
            M("1018", "1018 low carbon steel", MaterialGroup.CarbonSteel,
              126, 150, 7.87, 78, 0.95, 0.95, 1.30, true,
              "Gummy. Expect stringy chips without a chip-break cycle.");
            M("1045", "1045 medium carbon steel", MaterialGroup.CarbonSteel,
              170, 130, 7.87, 64, 0.85, 0.90, 1.35, true);

            // --- alloy steels
            M("4140A", "4140 annealed", MaterialGroup.AlloySteel,
              200, 110, 7.85, 62, 0.80, 0.85, 2.10, true);
            M("4140PH", "4140 pre-hardened 28-32 HRC", MaterialGroup.AlloySteel,
              285, 85, 7.85, 45, 0.55, 0.75, 2.40, true,
              "Watch spindle load on roughing. Sharp positive geometry helps.");
            M("8620", "8620 carburising alloy steel", MaterialGroup.AlloySteel,
              190, 115, 7.85, 66, 0.85, 0.88, 2.20, true);

            // --- stainless
            M("SS303", "303 free-machining austenitic stainless", MaterialGroup.StainlessAustenitic,
              160, 130, 8.00, 78, 0.80, 0.95, 6.50, false,
              "The friendly stainless. Keep the feed up so you cut under the work-hardened layer.");
            M("SS304", "304 austenitic stainless", MaterialGroup.StainlessAustenitic,
              150, 100, 8.00, 45, 0.55, 0.85, 6.00, true,
              "Work hardens badly. Never dwell, never rub, never take a spring pass.");
            M("SS316", "316 austenitic stainless", MaterialGroup.StainlessAustenitic,
              150, 90, 8.00, 36, 0.50, 0.82, 8.50, true,
              "Like 304 but tougher on edges. High pressure coolant pays for itself here.");
            M("SS416", "416 free-machining martensitic stainless", MaterialGroup.StainlessMartensitic,
              180, 140, 7.70, 85, 0.90, 1.00, 6.80);
            M("SS17-4", "17-4 PH condition A", MaterialGroup.StainlessMartensitic,
              320, 75, 7.78, 38, 0.45, 0.80, 12.00, true);
            M("NIT60", "Nitronic 60", MaterialGroup.StainlessAustenitic,
              210, 60, 7.62, 25, 0.35, 0.75, 18.00, true,
              "Galling-resistant by design, which is exactly why it hates being cut.");

            // --- aluminium
            M("AL6061", "6061-T6 aluminium", MaterialGroup.Aluminum,
              95, 500, 2.70, 190, 2.00, 1.60, 7.00, false,
              "Watch built-up edge at low speed. Faster is usually cleaner.");
            M("AL7075", "7075-T6 aluminium", MaterialGroup.Aluminum,
              150, 450, 2.81, 170, 1.70, 1.45, 12.00);
            M("AL2011", "2011-T3 free-machining aluminium", MaterialGroup.Aluminum,
              95, 600, 2.83, 250, 2.30, 1.75, 9.00);

            // --- copper alloys
            M("C360", "C36000 free-cutting brass", MaterialGroup.Brass,
              78, 400, 8.50, 300, 2.50, 1.80, 11.00, false,
              "Cuts like butter. Use neutral or negative rake to stop it grabbing.");
            M("C110", "C11000 electrolytic tough pitch copper", MaterialGroup.Copper,
              45, 250, 8.94, 70, 1.20, 1.10, 12.50, true,
              "Gummy and smeary. Sharp polished edges, plenty of coolant.");
            M("C932", "C93200 bearing bronze", MaterialGroup.Copper,
              65, 220, 8.93, 90, 1.40, 1.20, 14.00);

            // --- exotics
            M("TI64", "Ti-6Al-4V", MaterialGroup.Titanium,
              334, 55, 4.43, 22, 0.30, 0.70, 45.00, true,
              "Low thermal conductivity puts all the heat in the edge. Constant feed, never dwell, flood it.");
            M("IN718", "Inconel 718 solution treated", MaterialGroup.Nickel,
              340, 30, 8.19, 12, 0.18, 0.60, 75.00, true,
              "Plan tool changes into the cycle. Expect to index on a schedule, not on failure.");
            M("MONEL400", "Monel 400", MaterialGroup.Nickel,
              140, 55, 8.80, 30, 0.40, 0.75, 40.00, true);

            // --- plastics
            M("DELRIN", "Delrin / acetal homopolymer", MaterialGroup.Plastic,
              20, 500, 1.41, 400, 3.00, 1.60, 6.00, false,
              "Melts before it dulls a tool. Watch chip evacuation, not tool life.");
            M("PEEK", "PEEK unfilled", MaterialGroup.Plastic,
              25, 400, 1.32, 250, 1.50, 1.30, 180.00, false,
              "Abrasive if glass filled. Check the grade before trusting this number.");
            M("PTFE", "PTFE", MaterialGroup.Plastic,
              10, 300, 2.20, 350, 2.50, 1.20, 45.00, false,
              "Moves under cutting pressure. Size it, then let it relax, then measure.");
            M("NYLON66", "Nylon 6/6", MaterialGroup.Plastic,
              18, 400, 1.14, 300, 2.50, 1.40, 8.00, false,
              "Absorbs moisture and grows. Do not chase tenths on a hygroscopic plastic.");

            return db;
        }

        // ------------------------------------------------------------------ persistence

        public JsonValue ToJson()
        {
            var arr = JsonValue.Arr();
            foreach (var m in _byId.Values.OrderBy(x => x.Id))
            {
                var j = m.ToJson();
                j.Set("costIsIndicative", IndicativeCostIds.Contains(m.Id));
                arr.Add(j);
            }
            var root = JsonValue.Obj();
            root.Set("schema", "swissforge.materials/1");
            root["materials"] = arr;
            return root;
        }

        public static MaterialDatabase FromJson(JsonValue root)
        {
            var db = new MaterialDatabase();
            var arr = root.Kind == JsonKind.Array ? root : root["materials"];
            foreach (var j in arr)
                db.Add(Material.FromJson(j), j["costIsIndicative"].AsBool(false));
            return db;
        }

        /// <summary>
        /// Overlay shop-specific overrides on top of the built-ins. Any field present in the
        /// override file replaces the default; anything absent is inherited. Overriding a
        /// cost clears the "indicative" flag, which is what unblocks quoting.
        /// </summary>
        public void ApplyOverrides(JsonValue root)
        {
            var arr = root.Kind == JsonKind.Array ? root : root["materials"];
            foreach (var j in arr)
            {
                var id = j["id"].AsString("");
                if (string.IsNullOrEmpty(id)) continue;

                var existing = Get(id);
                if (existing == null) { Add(Material.FromJson(j)); continue; }

                if (j.Has("name")) existing.Name = j["name"].AsString(existing.Name);
                if (j.Has("group")) existing.Group = j["group"].AsEnum(existing.Group);
                if (j.Has("hardnessBhn")) existing.HardnessBhn = j["hardnessBhn"].AsDouble(existing.HardnessBhn);
                if (j.Has("baseSurfaceSpeedMPerMin")) existing.BaseSurfaceSpeedMPerMin = j["baseSurfaceSpeedMPerMin"].AsDouble(existing.BaseSurfaceSpeedMPerMin);
                if (j.Has("densityGPerCm3")) existing.DensityGPerCm3 = j["densityGPerCm3"].AsDouble(existing.DensityGPerCm3);
                if (j.Has("toolLifeFactor")) existing.ToolLifeFactor = j["toolLifeFactor"].AsDouble(existing.ToolLifeFactor);
                if (j.Has("feedFactor")) existing.FeedFactor = j["feedFactor"].AsDouble(existing.FeedFactor);
                if (j.Has("requiresChipBreaking")) existing.RequiresChipBreaking = j["requiresChipBreaking"].AsBool(existing.RequiresChipBreaking);
                if (j.Has("note")) existing.Note = j["note"].AsString(existing.Note);
                if (j.Has("costPerKg"))
                {
                    existing.CostPerKg = j["costPerKg"].AsDouble(existing.CostPerKg);
                    IndicativeCostIds.Remove(id);   // the shop supplied a real number
                }
            }
        }
    }
}
