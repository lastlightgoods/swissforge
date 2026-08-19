using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>The tools loaded on a machine, and the logic for picking one for a job.</summary>
    public sealed class ToolLibrary
    {
        private readonly List<ToolItem> _tools = new List<ToolItem>();

        public IReadOnlyList<ToolItem> Tools => _tools;
        public int Count => _tools.Count;

        public void Add(ToolItem t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            if (string.IsNullOrEmpty(t.Id)) throw new ArgumentException("Tool needs an Id.");
            _tools.RemoveAll(x => x.Id == t.Id);
            _tools.Add(t);
        }

        public ToolItem Get(string id) => _tools.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// Picks the best tool for an operation.
        /// <para>
        /// Selection is: right purpose, right spindle side, then — for anything with a
        /// diameter that must match the feature, like a drill, reamer or tap — the closest
        /// diameter that is not larger than the hole. A 3.1 mm drill will not be offered for
        /// a 3.0 mm hole, because it does not fit, and quietly substituting one is how a lot
        /// gets scrapped.
        /// </para>
        /// </summary>
        public ToolItem Select(OperationType purpose, SpindleSide side, double featureDiameterMm = 0,
                               double threadPitchMm = 0)
        {
            var candidates = _tools.Where(t => t.Purpose == purpose && t.Side == side).ToList();
            if (candidates.Count == 0)
                candidates = _tools.Where(t => t.Purpose == purpose).ToList();
            if (candidates.Count == 0) return null;

            if (RequiresExactDiameter(purpose) && featureDiameterMm > 0)
            {
                if (threadPitchMm > 0)
                {
                    var pitched = candidates.Where(t => Math.Abs(t.ThreadPitchMm - threadPitchMm) < 1e-6).ToList();
                    if (pitched.Count > 0) candidates = pitched;
                }

                var exact = candidates
                    .Where(t => t.DiameterMm > 0 && t.DiameterMm <= featureDiameterMm + 1e-6)
                    .OrderByDescending(t => t.DiameterMm)
                    .ToList();

                if (exact.Count > 0) return exact[0];
                return null;   // nothing fits: say so rather than substituting
            }

            // For single-point tools, prefer the largest nose radius that can still make the
            // corners on the part - a bigger nose is stronger and finishes better.
            return candidates.OrderByDescending(t => t.NoseRadiusMm).First();
        }

        private static bool RequiresExactDiameter(OperationType t) =>
            t == OperationType.Drill || t == OperationType.BackDrill || t == OperationType.CrossDrill ||
            t == OperationType.Ream || t == OperationType.Tap || t == OperationType.BackTap ||
            t == OperationType.Mill;

        public JsonValue ToJson()
        {
            var root = JsonValue.Obj();
            root.Set("schema", "swissforge.tools/1");
            var arr = JsonValue.Arr();
            foreach (var t in _tools) arr.Add(t.ToJson());
            root["tools"] = arr;
            return root;
        }

        public static ToolLibrary FromJson(JsonValue root)
        {
            var lib = new ToolLibrary();
            var arr = root.Kind == JsonKind.Array ? root : root["tools"];
            foreach (var j in arr) lib.Add(ToolItem.FromJson(j));
            return lib;
        }

        /// <summary>A plausible starting tool list for a 20 mm class Swiss machine.</summary>
        public static ToolLibrary CreateStarter()
        {
            var lib = new ToolLibrary();

            lib.Add(new ToolItem { Id = "T01", Description = "OD rough, CNMG 0.4R", Purpose = OperationType.TurnRough,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 1,
                NoseRadiusMm = 0.4, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 9.50, PartsPerEdge = 900 });

            lib.Add(new ToolItem { Id = "T02", Description = "OD finish, DCGT 0.2R", Purpose = OperationType.TurnFinish,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 2,
                NoseRadiusMm = 0.2, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 11.00, PartsPerEdge = 1400 });

            lib.Add(new ToolItem { Id = "T03", Description = "Face, DCGT 0.4R", Purpose = OperationType.Face,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 3,
                NoseRadiusMm = 0.4, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 11.00, PartsPerEdge = 1600 });

            lib.Add(new ToolItem { Id = "T04", Description = "Cutoff blade 1.5 mm", Purpose = OperationType.Cutoff,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 4,
                WidthMm = 1.5, NoseRadiusMm = 0.1, Substrate = ToolMaterial.CoatedCarbide,
                CostPerEdge = 14.00, PartsPerEdge = 1200 });

            lib.Add(new ToolItem { Id = "T05", Description = "Grooving blade 2.0 mm", Purpose = OperationType.Groove,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 5,
                WidthMm = 2.0, NoseRadiusMm = 0.2, Substrate = ToolMaterial.CoatedCarbide,
                CostPerEdge = 13.00, PartsPerEdge = 1000 });

            lib.Add(new ToolItem { Id = "T06", Description = "Single point thread, 60 deg", Purpose = OperationType.Thread,
                Station = ToolStation.GangSlide, Side = SpindleSide.Main, StationNumber = 6,
                NoseRadiusMm = 0.1, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 15.00, PartsPerEdge = 700 });

            foreach (var d in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 6.0 })
            {
                lib.Add(new ToolItem
                {
                    Id = "D" + d.ToString("0.0").Replace(".", ""),
                    Description = $"Carbide drill {d:0.0} mm",
                    Purpose = OperationType.Drill,
                    Station = ToolStation.EndworkingSlide, Side = SpindleSide.Main,
                    DiameterMm = d, Flutes = 2, StickoutMm = d * 6,
                    Substrate = ToolMaterial.Carbide,
                    CostPerEdge = 18.00 + d * 2, PartsPerEdge = 1500
                });
            }

            lib.Add(new ToolItem { Id = "B01", Description = "Back face tool", Purpose = OperationType.BackFace,
                Station = ToolStation.BackWorking, Side = SpindleSide.Sub, StationNumber = 21,
                NoseRadiusMm = 0.4, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 11.00, PartsPerEdge = 1600 });

            lib.Add(new ToolItem { Id = "B02", Description = "Back turn tool", Purpose = OperationType.BackTurn,
                Station = ToolStation.BackWorking, Side = SpindleSide.Sub, StationNumber = 22,
                NoseRadiusMm = 0.2, Substrate = ToolMaterial.CoatedCarbide, CostPerEdge = 11.00, PartsPerEdge = 1400 });

            foreach (var d in new[] { 1.5, 2.0, 3.0, 4.0 })
            {
                lib.Add(new ToolItem
                {
                    Id = "BD" + d.ToString("0.0").Replace(".", ""),
                    Description = $"Back drill {d:0.0} mm",
                    Purpose = OperationType.BackDrill,
                    Station = ToolStation.BackWorking, Side = SpindleSide.Sub,
                    DiameterMm = d, Flutes = 2, StickoutMm = d * 6,
                    Substrate = ToolMaterial.Carbide,
                    CostPerEdge = 18.00 + d * 2, PartsPerEdge = 1400
                });
            }

            lib.Add(new ToolItem { Id = "L01", Description = "Cross drill 2.0 mm, live", Purpose = OperationType.CrossDrill,
                Station = ToolStation.LiveToolFront, Side = SpindleSide.Main, StationNumber = 11,
                DiameterMm = 2.0, Flutes = 2, StickoutMm = 14, MaxRpm = 6000,
                Substrate = ToolMaterial.Carbide, CostPerEdge = 22.00, PartsPerEdge = 1200 });

            return lib;
        }
    }
}
