using System;
using System.Collections.Generic;
using SwissForge.Core.Json;

namespace SwissForge.Core.Model
{
    /// <summary>One machined feature on the part, as read off the print.</summary>
    public sealed class PartFeature
    {
        public string Id { get; set; } = "";
        public OperationType Kind { get; set; } = OperationType.TurnRough;
        public string Label { get; set; } = "";

        public SpindleSide Side { get; set; } = SpindleSide.Main;

        /// <summary>Finished diameter of the feature, mm. For a drill, the hole diameter.</summary>
        public double DiameterMm { get; set; }

        /// <summary>Starting diameter the tool sees, mm. Used to compute stock removal.</summary>
        public double StartDiameterMm { get; set; }

        /// <summary>Axial length of the feature, mm. For a drill, the depth.</summary>
        public double LengthMm { get; set; }

        /// <summary>Bilateral tolerance in mm. Tight tolerances force a separate finish pass.</summary>
        public double ToleranceMm { get; set; } = 0.05;

        /// <summary>Required surface finish, Ra micrometres. 0 = not specified.</summary>
        public double SurfaceFinishRa { get; set; }

        /// <summary>Thread pitch in mm, for thread and tap features.</summary>
        public double ThreadPitchMm { get; set; }

        /// <summary>Number of identical instances, e.g. 4 cross-holes on a bolt circle.</summary>
        public int Instances { get; set; } = 1;

        /// <summary>Free-text note carried onto the setup sheet.</summary>
        public string Note { get; set; } = "";

        /// <summary>True when the print tolerance is tight enough to demand a dedicated finish pass.</summary>
        public bool NeedsFinishPass =>
            ToleranceMm > 0 && ToleranceMm <= 0.025 ||
            (SurfaceFinishRa > 0 && SurfaceFinishRa <= 0.8);

        /// <summary>Radial stock to remove, mm.</summary>
        public double RadialStockMm =>
            StartDiameterMm > DiameterMm ? (StartDiameterMm - DiameterMm) / 2.0 : 0;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("kind", Kind.ToString());
            j.Set("label", Label);
            j.Set("side", Side.ToString());
            j.Set("diameterMm", DiameterMm);
            j.Set("startDiameterMm", StartDiameterMm);
            j.Set("lengthMm", LengthMm);
            j.Set("toleranceMm", ToleranceMm);
            j.Set("surfaceFinishRa", SurfaceFinishRa);
            j.Set("threadPitchMm", ThreadPitchMm);
            j.Set("instances", Instances);
            j.Set("note", Note);
            return j;
        }

        public static PartFeature FromJson(JsonValue j) => new PartFeature
        {
            Id = j["id"].AsString(""),
            Kind = j["kind"].AsEnum(OperationType.TurnRough),
            Label = j["label"].AsString(""),
            Side = j["side"].AsEnum(SpindleSide.Main),
            DiameterMm = j["diameterMm"].AsDouble(0),
            StartDiameterMm = j["startDiameterMm"].AsDouble(0),
            LengthMm = j["lengthMm"].AsDouble(0),
            ToleranceMm = j["toleranceMm"].AsDouble(0.05),
            SurfaceFinishRa = j["surfaceFinishRa"].AsDouble(0),
            ThreadPitchMm = j["threadPitchMm"].AsDouble(0),
            Instances = j["instances"].AsInt(1),
            Note = j["note"].AsString("")
        };
    }

    /// <summary>Bar stock as purchased.</summary>
    public sealed class StockBar
    {
        public string MaterialId { get; set; } = "";
        public double DiameterMm { get; set; }
        public double LengthMm { get; set; } = 3660;   // 12 ft, the usual purchase length

        /// <summary>Cost of one whole bar. If 0, cost is derived from mass x material cost/kg.</summary>
        public double CostPerBar { get; set; }

        /// <summary>Unusable tail the bar feeder cannot push, mm. Pure scrap on every bar.</summary>
        public double RemnantMm { get; set; } = 300;

        /// <summary>Facing/cleanup stock taken off the front of a new bar, mm.</summary>
        public double BarFaceStockMm { get; set; } = 2.0;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("materialId", MaterialId);
            j.Set("diameterMm", DiameterMm);
            j.Set("lengthMm", LengthMm);
            j.Set("costPerBar", CostPerBar);
            j.Set("remnantMm", RemnantMm);
            j.Set("barFaceStockMm", BarFaceStockMm);
            return j;
        }

        public static StockBar FromJson(JsonValue j) => new StockBar
        {
            MaterialId = j["materialId"].AsString(""),
            DiameterMm = j["diameterMm"].AsDouble(0),
            LengthMm = j["lengthMm"].AsDouble(3660),
            CostPerBar = j["costPerBar"].AsDouble(0),
            RemnantMm = j["remnantMm"].AsDouble(300),
            BarFaceStockMm = j["barFaceStockMm"].AsDouble(2.0)
        };
    }

    /// <summary>Everything SwissForge needs to know about the part being quoted or programmed.</summary>
    public sealed class PartSpec
    {
        public string PartNumber { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Customer { get; set; } = "";
        public string Description { get; set; } = "";

        public StockBar Stock { get; set; } = new StockBar();

        /// <summary>Finished overall length, mm.</summary>
        public double OverallLengthMm { get; set; }

        /// <summary>Largest finished diameter, mm.</summary>
        public double MaxDiameterMm { get; set; }

        /// <summary>Width of the cutoff blade, mm. Lost material on every part.</summary>
        public double CutoffWidthMm { get; set; } = 1.5;

        /// <summary>Extra facing stock left on the part for the sub-spindle back-face, mm.</summary>
        public double BackFaceStockMm { get; set; } = 0.3;

        public List<PartFeature> Features { get; set; } = new List<PartFeature>();

        /// <summary>Order quantity for this release.</summary>
        public int Quantity { get; set; } = 1;

        /// <summary>Expected annual usage, used for quantity-break pricing.</summary>
        public int AnnualUsage { get; set; }

        /// <summary>Deburr, plating, heat treat, passivate — priced per part on the quote.</summary>
        public List<SecondaryOperation> SecondaryOps { get; set; } = new List<SecondaryOperation>();

        /// <summary>Scrap allowance as a fraction, e.g. 0.02 for 2%.</summary>
        public double ScrapRate { get; set; } = 0.02;

        /// <summary>Material consumed per part along the bar, mm.</summary>
        public double MaterialPerPartMm => OverallLengthMm + BackFaceStockMm + CutoffWidthMm;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("partNumber", PartNumber);
            j.Set("revision", Revision);
            j.Set("customer", Customer);
            j.Set("description", Description);
            j["stock"] = Stock.ToJson();
            j.Set("overallLengthMm", OverallLengthMm);
            j.Set("maxDiameterMm", MaxDiameterMm);
            j.Set("cutoffWidthMm", CutoffWidthMm);
            j.Set("backFaceStockMm", BackFaceStockMm);
            j.Set("quantity", Quantity);
            j.Set("annualUsage", AnnualUsage);
            j.Set("scrapRate", ScrapRate);
            var feats = JsonValue.Arr();
            foreach (var f in Features) feats.Add(f.ToJson());
            j["features"] = feats;
            var sec = JsonValue.Arr();
            foreach (var s in SecondaryOps) sec.Add(s.ToJson());
            j["secondaryOps"] = sec;
            return j;
        }

        public static PartSpec FromJson(JsonValue j)
        {
            var p = new PartSpec
            {
                PartNumber = j["partNumber"].AsString(""),
                Revision = j["revision"].AsString(""),
                Customer = j["customer"].AsString(""),
                Description = j["description"].AsString(""),
                Stock = StockBar.FromJson(j["stock"]),
                OverallLengthMm = j["overallLengthMm"].AsDouble(0),
                MaxDiameterMm = j["maxDiameterMm"].AsDouble(0),
                CutoffWidthMm = j["cutoffWidthMm"].AsDouble(1.5),
                BackFaceStockMm = j["backFaceStockMm"].AsDouble(0.3),
                Quantity = j["quantity"].AsInt(1),
                AnnualUsage = j["annualUsage"].AsInt(0),
                ScrapRate = j["scrapRate"].AsDouble(0.02)
            };
            foreach (var f in j["features"]) p.Features.Add(PartFeature.FromJson(f));
            foreach (var s in j["secondaryOps"]) p.SecondaryOps.Add(SecondaryOperation.FromJson(s));
            return p;
        }
    }

    /// <summary>An off-machine operation priced into the part.</summary>
    public sealed class SecondaryOperation
    {
        public string Name { get; set; } = "";
        public double CostPerPart { get; set; }
        public double LotCharge { get; set; }
        public int LeadTimeDays { get; set; }
        public string Vendor { get; set; } = "";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("name", Name);
            j.Set("costPerPart", CostPerPart);
            j.Set("lotCharge", LotCharge);
            j.Set("leadTimeDays", LeadTimeDays);
            j.Set("vendor", Vendor);
            return j;
        }

        public static SecondaryOperation FromJson(JsonValue j) => new SecondaryOperation
        {
            Name = j["name"].AsString(""),
            CostPerPart = j["costPerPart"].AsDouble(0),
            LotCharge = j["lotCharge"].AsDouble(0),
            LeadTimeDays = j["leadTimeDays"].AsInt(0),
            Vendor = j["vendor"].AsString("")
        };
    }
}
