using System.Linq;
using SwissForge.Core.Feeds;
using SwissForge.Core.Model;
using SwissForge.Core.Units;

namespace SwissForge.Tests
{
    public static class TestFeeds
    {
        private static MachineProfile Machine() => new MachineProfile
        {
            Name = "Citizen L20",
            MaxBarDiameterMm = 20,
            ChannelCount = 3,
            MainSpindleMaxRpm = 10000,
            SubSpindleMaxRpm = 8000,
            LiveToolMaxRpm = 6000,
            MainSpindlePowerKw = 3.7
        };

        public static void Run()
        {
            Check.Suite("Material database");

            var db = MaterialDatabase.CreateDefault();
            Check.Greater(db.Count, 20, "the built-in library covers a real spread of materials");
            Check.True(db.Get("12L14") != null, "finds 12L14 by id");
            Check.True(db.Find("303") != null, "finds 303 stainless by fuzzy name");
            Check.True(db.CostIsIndicative("12L14"), "built-in material costs are flagged indicative");

            var brass = db.GetOrThrow("C360");
            var inconel = db.GetOrThrow("IN718");
            Check.Greater(brass.BaseSurfaceSpeedMPerMin, inconel.BaseSurfaceSpeedMPerMin,
                "brass runs faster than Inconel, as anyone who has cut both would insist");
            Check.Greater(brass.ToolLifeFactor, inconel.ToolLifeFactor,
                "brass is kinder to edges than Inconel");

            Check.Throws<System.Collections.Generic.KeyNotFoundException>(
                () => db.GetOrThrow("UNOBTAINIUM"), "an unknown material fails loudly");

            // hardness correction
            var steel = db.GetOrThrow("4140A");
            double softSpeed = steel.EffectiveBaseSpeed();
            steel.HardnessBhn = 300;
            double hardSpeed = steel.EffectiveBaseSpeed();
            Check.Less(hardSpeed, softSpeed, "harder material gets a lower cutting speed");

            // overrides
            var db2 = MaterialDatabase.CreateDefault();
            db2.ApplyOverrides(SwissForge.Core.Json.JsonValue.Parse(
                @"{""materials"":[{""id"":""12L14"",""costPerKg"":2.15,""baseSurfaceSpeedMPerMin"":240}]}"));
            Check.Near(2.15, db2.GetOrThrow("12L14").CostPerKg, 1e-9, "shop cost override is applied");
            Check.Near(240, db2.GetOrThrow("12L14").BaseSurfaceSpeedMPerMin, 1e-9, "shop speed override is applied");
            Check.False(db2.CostIsIndicative("12L14"), "supplying a real cost clears the indicative flag");
            Check.True(db2.CostIsIndicative("SS303"), "untouched materials stay flagged indicative");

            // ------------------------------------------------------------------
            Check.Suite("Feed and speed engine");

            var engine = new FeedSpeedEngine();
            var machine = Machine();

            // --- ordinary OD roughing in free-machining steel
            var rough = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"),
                Tool = new ToolItem { Id = "T1", Substrate = ToolMaterial.CoatedCarbide, NoseRadiusMm = 0.4 },
                Machine = machine,
                Operation = OperationType.TurnRough,
                WorkDiameterMm = 12,
                RadialStockMm = 1.0,
                CutLengthMm = 20,
                UnsupportedLengthMm = 20
            });

            Check.Greater(rough.Rpm, 0, "roughing produces a spindle speed");
            Check.Between(rough.SurfaceSpeedMPerMin, 150, 400, "12L14 roughing speed lands in a sane band");
            Check.Between(rough.FeedMmPerRev, 0.05, 0.35, "roughing feed lands in a sane band");
            Check.Greater(rough.Passes, 0, "roughing plans at least one pass");
            Check.Near(1.0, rough.DepthOfCutMm * rough.Passes, 0.01, "the passes add up to the stock removed");
            Check.False(rough.HasBlocker, "an ordinary roughing cut has no blocking advisory");

            // --- finishing is faster in surface speed but lighter in feed
            var finish = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"),
                Tool = new ToolItem { Id = "T2", Substrate = ToolMaterial.CoatedCarbide, NoseRadiusMm = 0.4 },
                Machine = machine,
                Operation = OperationType.TurnFinish,
                WorkDiameterMm = 12,
                RadialStockMm = 0.15,
                CutLengthMm = 20
            });
            Check.Less(finish.FeedMmPerRev, rough.FeedMmPerRev, "finishing feeds lighter than roughing");
            Check.Greater(finish.SurfaceSpeedMPerMin, rough.SurfaceSpeedMPerMin, "finishing runs a higher surface speed");

            // --- RPM clamp on a small diameter
            var tiny = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("C360"),
                Tool = new ToolItem { Id = "T3", Substrate = ToolMaterial.Carbide, NoseRadiusMm = 0.2 },
                Machine = machine,
                Operation = OperationType.TurnFinish,
                WorkDiameterMm = 1.0,
                RadialStockMm = 0.1,
                CutLengthMm = 3
            });
            Check.Near(machine.MainSpindleMaxRpm, tiny.Rpm, 1.0, "speed is clamped to the spindle ceiling on tiny diameters");
            Check.True(tiny.Advisories.Any(a => a.Code == "RPM_CLAMPED"), "the clamp is reported, not hidden");
            Check.Near(U.SurfaceSpeed(machine.MainSpindleMaxRpm, 1.0), tiny.SurfaceSpeedMPerMin, 0.1,
                "the reported surface speed reflects the clamp, not the wish");

            // --- HSS is much slower than coated carbide
            var hss = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"),
                Tool = new ToolItem { Id = "T4", Substrate = ToolMaterial.HSS, NoseRadiusMm = 0.4 },
                Machine = machine,
                Operation = OperationType.TurnRough,
                WorkDiameterMm = 12,
                RadialStockMm = 1.0,
                CutLengthMm = 20
            });
            Check.Less(hss.SurfaceSpeedMPerMin, rough.SurfaceSpeedMPerMin * 0.5, "HSS runs well under half of coated carbide");

            // --- slenderness derate ahead of the guide bushing
            var slender = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"),
                Tool = new ToolItem { Id = "T5", Substrate = ToolMaterial.CoatedCarbide, NoseRadiusMm = 0.4 },
                Machine = machine,
                Operation = OperationType.TurnRough,
                WorkDiameterMm = 6,
                RadialStockMm = 0.5,
                CutLengthMm = 60,
                UnsupportedLengthMm = 60      // 10 diameters out
            });
            Check.Less(slender.FeedMmPerRev, rough.FeedMmPerRev, "a slender cut is fed more gently");
            Check.True(slender.Advisories.Any(a => a.Code == "SLENDER_DERATE"), "the slenderness derate is reported");
            Check.True(slender.Advisories.Any(a => a.Severity == Severity.Error),
                "10 diameters unsupported is escalated to an error, not a shrug");

            // --- threading locks the feed to the pitch
            var thread = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("SS303"),
                Tool = new ToolItem { Id = "T6", Substrate = ToolMaterial.CoatedCarbide, NoseRadiusMm = 0.1 },
                Machine = machine,
                Operation = OperationType.Thread,
                WorkDiameterMm = 8,
                CutLengthMm = 12,
                ThreadPitchMm = 1.25
            });
            Check.Near(1.25, thread.FeedMmPerRev, 1e-9, "thread feed equals the pitch exactly");
            Check.Greater(thread.Passes, 3, "a single-point thread is taken in several infeed passes");
            Check.True(thread.Advisories.Any(a => a.Code == "PITCH_LOCKED"), "the pitch lock is explained");

            // --- a thread with no pitch is a blocking error, not a guess
            var noPitch = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("SS303"),
                Tool = new ToolItem { Id = "T7" },
                Machine = machine,
                Operation = OperationType.Tap,
                WorkDiameterMm = 5,
                CutLengthMm = 10
            });
            Check.True(noPitch.HasBlocker, "a tap with no pitch refuses to produce numbers");

            // --- drilling feed scales with diameter
            var smallDrill = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"), Machine = machine, Operation = OperationType.Drill,
                Tool = new ToolItem { Id = "D1", DiameterMm = 1.0, Substrate = ToolMaterial.Carbide, Flutes = 2 },
                WorkDiameterMm = 1.0, CutLengthMm = 5
            });
            var bigDrill = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"), Machine = machine, Operation = OperationType.Drill,
                Tool = new ToolItem { Id = "D2", DiameterMm = 6.0, Substrate = ToolMaterial.Carbide, Flutes = 2 },
                WorkDiameterMm = 6.0, CutLengthMm = 20
            });
            Check.Less(smallDrill.FeedMmPerRev, bigDrill.FeedMmPerRev, "a 1 mm drill is fed lighter than a 6 mm drill");
            Check.Less(smallDrill.FeedMmPerRev, 0.05, "a 1 mm drill stays under 0.05 mm/rev");

            // --- stickout derate
            var longDrill = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"), Machine = machine, Operation = OperationType.Drill,
                Tool = new ToolItem { Id = "D3", DiameterMm = 2.0, StickoutMm = 20, Substrate = ToolMaterial.Carbide },
                WorkDiameterMm = 2.0, CutLengthMm = 18
            });
            Check.True(longDrill.Advisories.Any(a => a.Code == "STICKOUT_DERATE"), "long stickout is derated and reported");

            // --- an unreachable finish is called out rather than quietly missed
            var fineFinish = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("SS316"), Machine = machine, Operation = OperationType.TurnFinish,
                Tool = new ToolItem { Id = "T8", NoseRadiusMm = 0.05, Substrate = ToolMaterial.CoatedCarbide },
                WorkDiameterMm = 10, RadialStockMm = 0.1, CutLengthMm = 15,
                TargetRaMicron = 0.2
            });
            Check.True(fineFinish.PredictedRaMicron > 0, "a finish prediction is produced");

            // --- power limit on a heavy cut in a tough material
            var heavy = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("IN718"), Machine = machine, Operation = OperationType.TurnRough,
                Tool = new ToolItem { Id = "T9", NoseRadiusMm = 0.8, Substrate = ToolMaterial.CoatedCarbide },
                WorkDiameterMm = 20, RadialStockMm = 3.0, CutLengthMm = 30
            });
            Check.Greater(heavy.SpindlePowerKw, 0, "spindle power is estimated");
            Check.Greater(heavy.CuttingForceN, 0, "cutting force is estimated");

            // --- a missing diameter is a hard stop
            var noDia = engine.Recommend(new FeedSpeedRequest
            {
                Material = db.GetOrThrow("12L14"), Machine = machine, Tool = new ToolItem { Id = "X" },
                Operation = OperationType.TurnRough
            });
            Check.True(noDia.HasBlocker, "no diameter anywhere is a blocking error");
        }
    }
}
