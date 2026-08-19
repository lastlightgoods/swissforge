using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Feeds;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Units;

namespace SwissForge.Core.Quoting
{
    /// <summary>Shop-level rates and policy that apply to every quote.</summary>
    public sealed class QuotePolicy
    {
        /// <summary>Rate charged for setup labour, currency per hour.</summary>
        public double SetupRatePerHour { get; set; } = 85.0;

        /// <summary>Rate charged for CAM programming, currency per hour.</summary>
        public double ProgrammingRatePerHour { get; set; } = 95.0;

        /// <summary>Hours to set the job up on the machine.</summary>
        public double SetupHours { get; set; } = 4.0;

        /// <summary>One-off programming hours. Amortised over annual usage if known, else over the lot.</summary>
        public double ProgrammingHours { get; set; } = 2.0;

        /// <summary>First-article inspection cost charged once per lot.</summary>
        public double FirstArticleCost { get; set; } = 0;

        /// <summary>Gross margin as a fraction of price, e.g. 0.35 for 35%.</summary>
        public double TargetMarginFraction { get; set; } = 0.35;

        /// <summary>Uplift on machine time to cover jams, bar changes, and operator attention.</summary>
        public double MachineOverheadFactor { get; set; } = 1.0;

        /// <summary>Freight, packaging and handling per part.</summary>
        public double PackagingPerPart { get; set; } = 0;

        /// <summary>Currency label for display only.</summary>
        public string Currency { get; set; } = "USD";

        /// <summary>
        /// Set true to price a quote even when the material cost is a built-in placeholder.
        /// Off by default, deliberately: a quote built on an invented metal price is the
        /// kind of mistake that only shows up on the invoice.
        /// </summary>
        public bool AllowIndicativeMaterialCost { get; set; } = false;
    }

    /// <summary>Everything the quoting engine needs for one part.</summary>
    public sealed class QuoteRequest
    {
        public PartSpec Part { get; set; }
        public MachineProfile Machine { get; set; }
        public MaterialDatabase Materials { get; set; }
        public QuotePolicy Policy { get; set; } = new QuotePolicy();

        /// <summary>Modelled cycle time for one part, seconds.</summary>
        public double CycleSeconds { get; set; }

        /// <summary>Tools used by the job. Drives the per-part tooling cost.</summary>
        public List<ToolItem> Tools { get; set; } = new List<ToolItem>();

        /// <summary>Quantities to price alongside the requested one.</summary>
        public List<int> QuantityBreaks { get; set; } = new List<int>();
    }

    /// <summary>A costed price at one quantity.</summary>
    public sealed class QuoteLine
    {
        public int Quantity { get; set; }

        public double MaterialPerPart { get; set; }
        public double MachinePerPart { get; set; }
        public double ToolingPerPart { get; set; }
        public double SetupPerPart { get; set; }
        public double ProgrammingPerPart { get; set; }
        public double SecondaryPerPart { get; set; }
        public double PackagingPerPart { get; set; }
        public double FirstArticlePerPart { get; set; }

        /// <summary>Cost carried for the parts you expect to scrap.</summary>
        public double ScrapPerPart { get; set; }

        public double CostPerPart =>
            MaterialPerPart + MachinePerPart + ToolingPerPart + SetupPerPart +
            ProgrammingPerPart + SecondaryPerPart + PackagingPerPart +
            FirstArticlePerPart + ScrapPerPart;

        public double PricePerPart { get; set; }
        public double MarginFraction => PricePerPart > 0 ? (PricePerPart - CostPerPart) / PricePerPart : 0;

        public double LotCost => CostPerPart * Quantity;
        public double LotPrice => PricePerPart * Quantity;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("quantity", Quantity);
            j.Set("materialPerPart", Math.Round(MaterialPerPart, 4));
            j.Set("machinePerPart", Math.Round(MachinePerPart, 4));
            j.Set("toolingPerPart", Math.Round(ToolingPerPart, 4));
            j.Set("setupPerPart", Math.Round(SetupPerPart, 4));
            j.Set("programmingPerPart", Math.Round(ProgrammingPerPart, 4));
            j.Set("secondaryPerPart", Math.Round(SecondaryPerPart, 4));
            j.Set("packagingPerPart", Math.Round(PackagingPerPart, 4));
            j.Set("firstArticlePerPart", Math.Round(FirstArticlePerPart, 4));
            j.Set("scrapPerPart", Math.Round(ScrapPerPart, 4));
            j.Set("costPerPart", Math.Round(CostPerPart, 4));
            j.Set("pricePerPart", Math.Round(PricePerPart, 4));
            j.Set("marginFraction", Math.Round(MarginFraction, 4));
            j.Set("lotCost", Math.Round(LotCost, 2));
            j.Set("lotPrice", Math.Round(LotPrice, 2));
            return j;
        }
    }

    /// <summary>A complete quote: the requested quantity, any breaks, and the reasoning.</summary>
    public sealed class QuoteResult
    {
        public string PartNumber { get; set; } = "";
        public string Currency { get; set; } = "USD";

        /// <summary>False when something is missing that makes the number untrustworthy.</summary>
        public bool IsQuotable { get; set; } = true;

        public QuoteLine Primary { get; set; }
        public List<QuoteLine> Breaks { get; } = new List<QuoteLine>();
        public List<Advisory> Advisories { get; } = new List<Advisory>();

        // --- supporting figures worth showing on the quote sheet
        public double CycleSeconds { get; set; }
        public int PartsPerBar { get; set; }
        public double BarUtilizationFraction { get; set; }
        public double MaterialPerPartMm { get; set; }
        public double PartsPerHour { get; set; }
        public double PartsPerDay { get; set; }
        public double LotMachineHours { get; set; }
        public double LotCalendarDays { get; set; }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("partNumber", PartNumber);
            j.Set("currency", Currency);
            j.Set("isQuotable", IsQuotable);
            j.Set("cycleSeconds", Math.Round(CycleSeconds, 3));
            j.Set("cycleFormatted", U.FormatDuration(CycleSeconds));
            j.Set("partsPerBar", PartsPerBar);
            j.Set("barUtilizationFraction", Math.Round(BarUtilizationFraction, 4));
            j.Set("materialPerPartMm", Math.Round(MaterialPerPartMm, 3));
            j.Set("partsPerHour", Math.Round(PartsPerHour, 2));
            j.Set("partsPerDay", Math.Round(PartsPerDay, 1));
            j.Set("lotMachineHours", Math.Round(LotMachineHours, 2));
            j.Set("lotCalendarDays", Math.Round(LotCalendarDays, 2));
            if (Primary != null) j["primary"] = Primary.ToJson();
            var br = JsonValue.Arr();
            foreach (var b in Breaks) br.Add(b.ToJson());
            j["breaks"] = br;
            var adv = JsonValue.Arr();
            foreach (var a in Advisories) adv.Add(a.ToJson());
            j["advisories"] = adv;
            return j;
        }
    }

    /// <summary>
    /// Costs and prices a Swiss job.
    /// <para>
    /// Two things this engine insists on that spreadsheet quoting usually gets wrong:
    /// </para>
    /// <list type="number">
    /// <item><b>The remnant is not free.</b> A 12 ft bar with a 300 mm unpushable tail yields
    /// fewer parts than the arithmetic suggests, and the whole bar was paid for. Material cost
    /// per part is bar cost divided by parts actually produced, not by parts theoretically
    /// contained.</item>
    /// <item><b>Scrap is priced on finished parts, not raw material.</b> A part scrapped at
    /// final inspection has consumed its material, its machine time, and its tooling. Costing
    /// scrap as lost bar stock systematically under-quotes tight-tolerance work.</item>
    /// </list>
    /// </summary>
    public sealed class QuoteEngine
    {
        public QuoteResult Quote(QuoteRequest req)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (req.Part == null) throw new ArgumentException("QuoteRequest.Part is required.");

            var part = req.Part;
            var machine = req.Machine ?? new MachineProfile();
            var policy = req.Policy ?? new QuotePolicy();

            var result = new QuoteResult
            {
                PartNumber = part.PartNumber,
                Currency = policy.Currency,
                CycleSeconds = req.CycleSeconds
            };

            // ---------------------------------------------------------- sanity
            if (req.CycleSeconds <= 0)
            {
                result.Advisories.Add(new Advisory(Severity.Critical, "NO_CYCLE_TIME",
                    "Cycle time is zero. Run the cycle-time estimator before quoting — otherwise " +
                    "the machine-time line is simply absent from the price."));
                result.IsQuotable = false;
            }

            var material = req.Materials?.Get(part.Stock.MaterialId);
            if (material == null)
            {
                result.Advisories.Add(new Advisory(Severity.Critical, "NO_MATERIAL",
                    $"Material '{part.Stock.MaterialId}' is not in the library, so there is no density " +
                    "or price to cost the bar with."));
                result.IsQuotable = false;
            }

            // ---------------------------------------------------------- material
            double materialPerPartMm = part.MaterialPerPartMm;
            result.MaterialPerPartMm = materialPerPartMm;

            int partsPerBar = 0;
            double materialCostPerPart = 0;

            if (materialPerPartMm <= 0)
            {
                result.Advisories.Add(new Advisory(Severity.Critical, "NO_PART_LENGTH",
                    "The part consumes zero length of bar. Set OverallLengthMm and CutoffWidthMm."));
                result.IsQuotable = false;
            }
            else if (material != null)
            {
                double usableLength = part.Stock.LengthMm - part.Stock.RemnantMm - part.Stock.BarFaceStockMm;
                partsPerBar = (int)Math.Floor(Math.Max(0, usableLength) / materialPerPartMm);

                if (partsPerBar <= 0)
                {
                    result.Advisories.Add(new Advisory(Severity.Critical, "NO_PARTS_PER_BAR",
                        $"A {part.Stock.LengthMm:F0} mm bar with a {part.Stock.RemnantMm:F0} mm remnant " +
                        $"yields no parts at {materialPerPartMm:F2} mm each. Check the stock length."));
                    result.IsQuotable = false;
                }
                else
                {
                    double barCost = part.Stock.CostPerBar;
                    if (barCost <= 0)
                    {
                        double massKg = U.BarMassKg(part.Stock.DiameterMm, part.Stock.LengthMm, material.DensityGPerCm3);
                        barCost = massKg * material.CostPerKg;

                        if (req.Materials != null && req.Materials.CostIsIndicative(material.Id))
                        {
                            var sev = policy.AllowIndicativeMaterialCost ? Severity.Warning : Severity.Critical;
                            result.Advisories.Add(new Advisory(sev, "INDICATIVE_MATERIAL_COST",
                                $"The price for {material.Name} is a built-in placeholder " +
                                $"({material.CostPerKg:F2}/kg), not your purchase price. " +
                                "Load your real cost, or set CostPerBar on the stock, before sending this out. " +
                                (policy.AllowIndicativeMaterialCost
                                    ? "Priced anyway because the policy allows it."
                                    : "Quote withheld.")));
                            if (!policy.AllowIndicativeMaterialCost) result.IsQuotable = false;
                        }
                    }

                    // The whole bar is paid for, including the tail nobody can machine.
                    materialCostPerPart = barCost / partsPerBar;

                    // Yield, not consumption. Every millimetre of bar is "consumed" by definition;
                    // what the shop is paying for is the fraction that leaves as finished part.
                    // Kerf, back-face stock and the remnant are all metal bought and thrown away.
                    double finishedLength = partsPerBar * part.OverallLengthMm;
                    result.BarUtilizationFraction = part.Stock.LengthMm > 0
                        ? finishedLength / part.Stock.LengthMm
                        : 0;

                    if (result.BarUtilizationFraction < 0.80)
                    {
                        double kerfLoss = partsPerBar * (part.CutoffWidthMm + part.BackFaceStockMm);
                        double remnantLoss = part.Stock.LengthMm - finishedLength - kerfLoss;
                        result.Advisories.Add(new Advisory(Severity.Warning, "LOW_BAR_UTILIZATION",
                            $"Only {result.BarUtilizationFraction * 100:F0}% of each bar leaves as finished part. " +
                            $"Roughly {kerfLoss:F0} mm goes to cutoff kerf and back-face stock and " +
                            $"{Math.Max(0, remnantLoss):F0} mm to the remnant and bar facing. " +
                            (part.CutoffWidthMm > part.OverallLengthMm * 0.15
                                ? $"A {part.CutoffWidthMm:F2} mm blade on a {part.OverallLengthMm:F1} mm part is the " +
                                  "dominant loss here — a thinner cutoff blade pays for itself quickly."
                                : "A longer bar, or a shorter remnant, is the usual fix.")));
                    }
                }

                if (part.Stock.DiameterMm > 0 && machine.MaxBarDiameterMm > 0 &&
                    part.Stock.DiameterMm > machine.MaxBarDiameterMm)
                {
                    result.Advisories.Add(new Advisory(Severity.Critical, "BAR_TOO_BIG",
                        $"{part.Stock.DiameterMm:F2} mm bar will not pass a machine rated for " +
                        $"{machine.MaxBarDiameterMm:F2} mm."));
                    result.IsQuotable = false;
                }

                if (part.MaxDiameterMm > 0 && part.Stock.DiameterMm > 0 &&
                    part.MaxDiameterMm > part.Stock.DiameterMm)
                {
                    result.Advisories.Add(new Advisory(Severity.Critical, "PART_BIGGER_THAN_BAR",
                        $"The part's largest diameter ({part.MaxDiameterMm:F3} mm) exceeds the bar " +
                        $"({part.Stock.DiameterMm:F3} mm). You cannot turn material that is not there."));
                    result.IsQuotable = false;
                }
            }

            result.PartsPerBar = partsPerBar;

            // ---------------------------------------------------------- machine time
            double cycleHours = req.CycleSeconds / 3600.0;

            // Bar changes are real machine minutes, spread across the parts that bar produced.
            double barChangePerPart = partsPerBar > 0 ? machine.BarChangeSeconds / partsPerBar / 3600.0 : 0;

            double machineHoursPerPart = (cycleHours + barChangePerPart) * policy.MachineOverheadFactor;
            double machineCostPerPart = machineHoursPerPart * machine.HourlyRate;

            result.PartsPerHour = req.CycleSeconds > 0 ? 3600.0 / req.CycleSeconds : 0;
            result.PartsPerDay = result.PartsPerHour * machine.ProductiveHoursPerDay * machine.UtilizationFactor;

            // ---------------------------------------------------------- tooling
            double toolingPerPart = 0;
            double toolLifeFactor = material?.ToolLifeFactor ?? 1.0;
            foreach (var tool in req.Tools ?? new List<ToolItem>())
            {
                if (tool.CostPerEdge <= 0) continue;

                double partsPerEdge = tool.PartsPerEdge * toolLifeFactor;
                if (partsPerEdge <= 0) partsPerEdge = 1;

                toolingPerPart += tool.CostPerEdge / partsPerEdge;

                // The minutes spent indexing a worn edge are machine minutes too.
                toolingPerPart += tool.EdgeChangeSeconds / partsPerEdge / 3600.0 * machine.HourlyRate;
            }

            if (req.Tools != null && req.Tools.Count > 0 && toolingPerPart <= 0)
                result.Advisories.Add(new Advisory(Severity.Warning, "NO_TOOLING_COST",
                    "None of the tools carry a cost per edge, so the quote contains no tooling line. " +
                    "In a hard material that is where the margin goes."));

            // ---------------------------------------------------------- secondary ops
            double secondaryPerPart = 0;
            double secondaryLot = 0;
            foreach (var s in part.SecondaryOps ?? new List<SecondaryOperation>())
            {
                secondaryPerPart += s.CostPerPart;
                secondaryLot += s.LotCharge;
            }

            // ---------------------------------------------------------- build the lines
            var quantities = new List<int>();
            int primaryQty = Math.Max(1, part.Quantity);
            quantities.Add(primaryQty);
            foreach (var q in req.QuantityBreaks ?? new List<int>())
                if (q > 0 && !quantities.Contains(q)) quantities.Add(q);
            quantities.Sort();

            foreach (var qty in quantities)
            {
                var line = BuildLine(qty, primaryQty, part, policy, materialCostPerPart,
                                     machineCostPerPart, toolingPerPart,
                                     secondaryPerPart, secondaryLot);
                if (qty == primaryQty) result.Primary = line;
                else result.Breaks.Add(line);
            }

            // Keep the primary in the break list too, so a quote sheet can render one table.
            if (result.Primary != null && !result.Breaks.Contains(result.Primary))
                result.Breaks.Add(result.Primary);
            result.Breaks.Sort((a, b) => a.Quantity.CompareTo(b.Quantity));

            // ---------------------------------------------------------- delivery
            result.LotMachineHours = machineHoursPerPart * primaryQty;
            result.LotCalendarDays = machine.ProductiveHoursPerDay > 0 && machine.UtilizationFactor > 0
                ? result.LotMachineHours / (machine.ProductiveHoursPerDay * machine.UtilizationFactor)
                : 0;

            AddCommercialAdvisories(result, part, policy);

            if (!result.IsQuotable)
                result.Advisories.Add(new Advisory(Severity.Critical, "NOT_QUOTABLE",
                    "This quote is incomplete. The numbers below are arithmetic, not a price — " +
                    "resolve the critical items above before sending it to a customer."));

            return result;
        }

        private static QuoteLine BuildLine(int qty, int primaryQty, PartSpec part, QuotePolicy policy,
                                           double materialPerPart, double machinePerPart, double toolingPerPart,
                                           double secondaryPerPart, double secondaryLot)
        {
            var line = new QuoteLine
            {
                Quantity = qty,
                MaterialPerPart = materialPerPart,
                MachinePerPart = machinePerPart,
                ToolingPerPart = toolingPerPart,
                SecondaryPerPart = secondaryPerPart + (qty > 0 ? secondaryLot / qty : 0),
                PackagingPerPart = policy.PackagingPerPart,
                SetupPerPart = qty > 0 ? policy.SetupHours * policy.SetupRatePerHour / qty : 0,
                FirstArticlePerPart = qty > 0 ? policy.FirstArticleCost / qty : 0
            };

            // Programming is a one-off. If the customer has told us the annual usage, amortise
            // across the year rather than punishing the first release for the whole thing.
            double programmingBase = policy.ProgrammingHours * policy.ProgrammingRatePerHour;
            int amortiseOver = part.AnnualUsage > qty ? part.AnnualUsage : qty;
            line.ProgrammingPerPart = amortiseOver > 0 ? programmingBase / amortiseOver : 0;

            // Scrap carries the full value of a finished part, not just its bar stock.
            double valueAtRisk = line.MaterialPerPart + line.MachinePerPart + line.ToolingPerPart;
            line.ScrapPerPart = valueAtRisk * Math.Max(0, part.ScrapRate);

            double margin = Math.Min(Math.Max(policy.TargetMarginFraction, 0), 0.95);
            line.PricePerPart = margin > 0 ? line.CostPerPart / (1.0 - margin) : line.CostPerPart;

            return line;
        }

        private static void AddCommercialAdvisories(QuoteResult result, PartSpec part, QuotePolicy policy)
        {
            var primary = result.Primary;
            if (primary == null) return;

            if (primary.SetupPerPart > primary.CostPerPart * 0.35)
                result.Advisories.Add(new Advisory(Severity.Warning, "SETUP_DOMINATES",
                    $"Setup is {primary.SetupPerPart / primary.CostPerPart * 100:F0}% of the cost at " +
                    $"{primary.Quantity} pieces. This is a short-run price, and the customer will feel it. " +
                    "Quoting a larger release, or offering a break, is usually the more persuasive answer."));

            if (primary.MaterialPerPart > primary.CostPerPart * 0.60)
                result.Advisories.Add(new Advisory(Severity.Info, "MATERIAL_DOMINATES",
                    $"Material is {primary.MaterialPerPart / primary.CostPerPart * 100:F0}% of the cost. " +
                    "Cycle-time improvements will barely move this price; a thinner cutoff blade, " +
                    "a shorter remnant, or a better material buy will."));

            if (result.LotCalendarDays > 20)
                result.Advisories.Add(new Advisory(Severity.Warning, "LONG_RUN",
                    $"This lot occupies the machine for about {result.LotCalendarDays:F0} days of " +
                    "production. Check that against the delivery you are about to promise."));

            if (policy.TargetMarginFraction <= 0)
                result.Advisories.Add(new Advisory(Severity.Warning, "ZERO_MARGIN",
                    "Target margin is zero, so this price is cost. Deliberate, or an unset field?"));
        }
    }
}
