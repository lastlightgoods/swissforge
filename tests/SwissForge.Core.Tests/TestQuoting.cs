using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Feeds;
using SwissForge.Core.Model;
using SwissForge.Core.Quoting;

namespace SwissForge.Tests
{
    public static class TestQuoting
    {
        private static PartSpec Part(MaterialDatabase db)
        {
            var p = new PartSpec
            {
                PartNumber = "SF-1001",
                Customer = "Acme",
                OverallLengthMm = 25.0,
                MaxDiameterMm = 9.5,
                CutoffWidthMm = 1.5,
                BackFaceStockMm = 0.3,
                Quantity = 1000,
                ScrapRate = 0.02,
                Stock = new StockBar
                {
                    MaterialId = "12L14",
                    DiameterMm = 10.0,
                    LengthMm = 3660,
                    RemnantMm = 300,
                    BarFaceStockMm = 2.0,
                    CostPerBar = 18.00      // a real purchase price, so nothing is indicative
                }
            };
            return p;
        }

        private static MachineProfile Machine() => new MachineProfile
        {
            Name = "Citizen L20",
            MaxBarDiameterMm = 20,
            HourlyRate = 75,
            BarChangeSeconds = 45,
            ProductiveHoursPerDay = 20,
            UtilizationFactor = 0.85
        };

        public static void Run()
        {
            Check.Suite("Quoting");

            var db = MaterialDatabase.CreateDefault();
            var engine = new QuoteEngine();

            var req = new QuoteRequest
            {
                Part = Part(db),
                Machine = Machine(),
                Materials = db,
                CycleSeconds = 32.0,
                Policy = new QuotePolicy
                {
                    SetupHours = 4, SetupRatePerHour = 85,
                    ProgrammingHours = 2, ProgrammingRatePerHour = 95,
                    TargetMarginFraction = 0.35
                },
                Tools = new List<ToolItem>
                {
                    new ToolItem { Id = "T1", CostPerEdge = 12.50, PartsPerEdge = 800, EdgeChangeSeconds = 120 },
                    new ToolItem { Id = "T2", CostPerEdge = 9.00,  PartsPerEdge = 1500, EdgeChangeSeconds = 90 }
                },
                QuantityBreaks = new List<int> { 100, 5000, 25000 }
            };

            var q = engine.Quote(req);

            Check.True(q.IsQuotable, "a fully specified part is quotable");
            Check.False(q.Advisories.Any(a => a.Code == "NOT_QUOTABLE"), "no blocking advisory on a complete quote");

            // Bar arithmetic: (3660 - 300 - 2) / (25 + 0.3 + 1.5) = 3358 / 26.8 = 125.3 -> 125
            Check.Equal(125, q.PartsPerBar, "125 parts come off a 12 ft bar at 26.8 mm each");
            Check.Near(26.8, q.MaterialPerPartMm, 1e-9, "each part consumes length plus back-face stock plus kerf");

            // Material: 18.00 / 125 = 0.144 per part. The remnant is paid for.
            Check.Near(0.144, q.Primary.MaterialPerPart, 1e-6, "material cost divides the whole bar by usable parts");

            // Machine: 32 s = 0.008889 h at 75/h = 0.6667, plus bar change 45/125 s
            Check.Between(q.Primary.MachinePerPart, 0.66, 0.70, "machine cost reflects cycle time plus bar changes");

            // Tooling: 12.50/(800 x 1.35) + 9.00/(1500 x 1.35) plus the machine minutes spent indexing.
            // The 1.35 is 12L14's tool-life factor - a free-machining steel gets more parts per edge.
            Check.Between(q.Primary.ToolingPerPart, 0.018, 0.021, "tooling cost per part is in the expected band");

            var toughReq = new QuoteRequest
            {
                Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 32.0,
                Policy = req.Policy, Tools = req.Tools
            };
            toughReq.Part.Stock.MaterialId = "IN718";
            toughReq.Part.Stock.CostPerBar = 400.00;
            var tough = engine.Quote(toughReq);
            Check.Greater(tough.Primary.ToolingPerPart, q.Primary.ToolingPerPart * 3,
                "Inconel eats edges, so its tooling cost per part is several times that of 12L14");

            // Setup at 1000 pieces: 4 * 85 / 1000 = 0.34
            Check.Near(0.34, q.Primary.SetupPerPart, 1e-9, "setup amortises across the lot");

            Check.Greater(q.Primary.PricePerPart, q.Primary.CostPerPart, "price is above cost");
            Check.Near(0.35, q.Primary.MarginFraction, 0.0001, "the quoted price hits the target margin exactly");

            Check.Greater(q.PartsPerHour, 100, "parts per hour follows from the cycle");
            Check.Greater(q.LotMachineHours, 0, "lot machine hours are computed");
            Check.Greater(q.LotCalendarDays, 0, "lot calendar days are computed");

            // --- quantity breaks move in the right direction
            var atHundred = q.Breaks.First(b => b.Quantity == 100);
            var atTwentyFive = q.Breaks.First(b => b.Quantity == 25000);
            Check.Greater(atHundred.PricePerPart, atTwentyFive.PricePerPart,
                "100 pieces costs more each than 25,000 pieces");
            Check.Greater(atHundred.SetupPerPart, atTwentyFive.SetupPerPart * 10,
                "the difference is setup amortisation, as it should be");
            Check.Equal(4, q.Breaks.Count, "the break table includes the primary quantity");

            // --- scrap is priced on finished value, not raw bar
            var noScrapReq = new QuoteRequest
            {
                Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 32.0,
                Policy = req.Policy, Tools = req.Tools
            };
            noScrapReq.Part.ScrapRate = 0;
            var noScrap = engine.Quote(noScrapReq);
            Check.Equal(0.0, noScrap.Primary.ScrapPerPart, "zero scrap rate produces no scrap line");
            Check.Greater(q.Primary.ScrapPerPart, q.Primary.MaterialPerPart * 0.02,
                "scrap carries machine and tooling value, not just the bar");

            // ------------------------------------------------------------------
            Check.Suite("Quoting guard rails");

            // --- an indicative material price blocks the quote
            var indicative = new QuoteRequest
            {
                Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 32.0, Policy = req.Policy
            };
            indicative.Part.Stock.CostPerBar = 0;    // force the fallback to cost/kg
            var qi = engine.Quote(indicative);
            Check.False(qi.IsQuotable, "a placeholder metal price refuses to produce a sendable quote");
            Check.True(qi.Advisories.Any(a => a.Code == "INDICATIVE_MATERIAL_COST"),
                "the placeholder price is named as the reason");
            Check.True(qi.Advisories.Any(a => a.Code == "NOT_QUOTABLE"), "the quote is marked unusable");

            // --- unless the shop opts in explicitly
            indicative.Policy = new QuotePolicy { AllowIndicativeMaterialCost = true, TargetMarginFraction = 0.35 };
            var qi2 = engine.Quote(indicative);
            Check.True(qi2.IsQuotable, "the shop can opt in to budgetary pricing deliberately");
            Check.Greater(qi2.Primary.MaterialPerPart, 0, "a budgetary quote still costs the material");

            // --- no cycle time is a hard stop
            var noCycle = new QuoteRequest { Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 0 };
            var qn = engine.Quote(noCycle);
            Check.False(qn.IsQuotable, "a quote with no cycle time is refused");
            Check.True(qn.Advisories.Any(a => a.Code == "NO_CYCLE_TIME"), "the missing cycle time is named");

            // --- bar bigger than the machine
            var tooBig = new QuoteRequest { Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 20 };
            tooBig.Part.Stock.DiameterMm = 32;
            var qb = engine.Quote(tooBig);
            Check.True(qb.Advisories.Any(a => a.Code == "BAR_TOO_BIG"), "32 mm bar on a 20 mm machine is caught");
            Check.False(qb.IsQuotable, "an impossible bar size blocks the quote");

            // --- part larger than the stock it is cut from
            var impossible = new QuoteRequest { Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 20 };
            impossible.Part.MaxDiameterMm = 12;   // bar is 10
            var qp = engine.Quote(impossible);
            Check.True(qp.Advisories.Any(a => a.Code == "PART_BIGGER_THAN_BAR"),
                "a part larger than its bar is caught before anyone cuts it");

            // --- unknown material
            var unknown = new QuoteRequest { Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 20 };
            unknown.Part.Stock.MaterialId = "MITHRIL";
            var qu = engine.Quote(unknown);
            Check.True(qu.Advisories.Any(a => a.Code == "NO_MATERIAL"), "an unknown material is caught");

            // --- short-run setup dominance is flagged
            var shortRun = new QuoteRequest
            {
                Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 32, Policy = req.Policy
            };
            shortRun.Part.Quantity = 25;
            var qs = engine.Quote(shortRun);
            Check.True(qs.Advisories.Any(a => a.Code == "SETUP_DOMINATES"),
                "a 25-piece run is flagged as setup-dominated");

            // --- low bar utilisation is flagged
            var wasteful = new QuoteRequest
            {
                Part = Part(db), Machine = Machine(), Materials = db, CycleSeconds = 32, Policy = req.Policy
            };
            wasteful.Part.OverallLengthMm = 5;
            wasteful.Part.CutoffWidthMm = 3.0;    // kerf over half the part length
            var qw = engine.Quote(wasteful);
            Check.True(qw.Advisories.Any(a => a.Code == "LOW_BAR_UTILIZATION"),
                "a 3 mm blade on a 5 mm part is flagged as poor bar yield");
            Check.Less(qw.BarUtilizationFraction, 0.70,
                "yield counts finished part length, not bar length consumed");
            Check.Contains(qw.Advisories.First(a => a.Code == "LOW_BAR_UTILIZATION").Message, "blade",
                "the advisory identifies the cutoff blade as the dominant loss");
            Check.Between(q.BarUtilizationFraction, 0.80, 0.90,
                "a sensibly proportioned part yields around 85% of the bar");
        }
    }
}
