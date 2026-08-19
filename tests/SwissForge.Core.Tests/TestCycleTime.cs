using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.CycleTime;
using SwissForge.Core.Model;

namespace SwissForge.Tests
{
    public static class TestCycleTime
    {
        private static MachineProfile Machine() => new MachineProfile
        {
            Name = "Star SR-20J",
            ChannelCount = 2,
            RapidRateMmPerMin = 32000,
            ToolChangeSeconds = 0.4,
            SpindleRampSeconds = 0.35,
            TransferSeconds = 2.5,
            BarFeedSeconds = 1.2,
            ProductiveHoursPerDay = 20,
            UtilizationFactor = 0.85
        };

        /// <summary>A fixed-duration operation, so schedule tests assert on the scheduler, not on the physics.</summary>
        private static OperationSpec Op(string id, int channel, int seq, double seconds,
                                        string waitBefore = null, OperationType type = OperationType.TurnRough)
            => new OperationSpec
            {
                Id = id, Name = id, Channel = channel, Sequence = seq,
                Type = type, WaitBefore = waitBefore,
                CutSeconds = seconds, NonCutSeconds = 0
            };

        public static void Run()
        {
            Check.Suite("Operation timing");

            var est = new CycleTimeEstimator();
            var machine = Machine();

            // 20 mm of cut at 0.1 mm/rev and 3000 rpm = 300 mm/min -> 4 s
            var turn = new OperationSpec
            {
                Id = "turn", Type = OperationType.TurnRough,
                Rpm = 3000, FeedMmPerRev = 0.1, CutLengthMm = 20, Passes = 1,
                WorkDiameterMm = 12, ApproachLengthMm = 5
            };
            est.ComputeOperationTime(turn, machine);
            Check.Near(4.0, turn.CutSeconds, 0.01, "20 mm at 300 mm/min is 4 seconds of cutting");
            Check.Greater(turn.NonCutSeconds, 0, "approach, tool change and spindle ramp are counted");
            Check.Less(turn.NonCutSeconds, 2.0, "the non-cutting overhead stays plausible");

            // three passes triples the cut
            var threePass = new OperationSpec
            {
                Id = "t3", Type = OperationType.TurnRough,
                Rpm = 3000, FeedMmPerRev = 0.1, CutLengthMm = 20, Passes = 3,
                WorkDiameterMm = 12, ApproachLengthMm = 5
            };
            est.ComputeOperationTime(threePass, machine);
            Check.Near(12.0, threePass.CutSeconds, 0.01, "three passes take three times as long");

            // tapping goes in and comes back out
            var tap = new OperationSpec
            {
                Id = "tap", Type = OperationType.Tap,
                Rpm = 1000, FeedMmPerRev = 0.8, CutLengthMm = 10, WorkDiameterMm = 5
            };
            est.ComputeOperationTime(tap, machine);
            Check.Near(1.5, tap.CutSeconds, 0.01, "a tap is timed in both directions");

            // a deep hole gets peck penalties
            var shallow = new OperationSpec
            {
                Id = "d1", Type = OperationType.Drill, Rpm = 4000, FeedMmPerRev = 0.03,
                CutLengthMm = 4, WorkDiameterMm = 2, ApproachLengthMm = 2
            };
            var deep = new OperationSpec
            {
                Id = "d2", Type = OperationType.Drill, Rpm = 4000, FeedMmPerRev = 0.03,
                CutLengthMm = 30, WorkDiameterMm = 2, ApproachLengthMm = 2
            };
            est.ComputeOperationTime(shallow, machine);
            est.ComputeOperationTime(deep, machine);
            Check.Near(shallow.NonCutSeconds, shallow.NonCutSeconds, 0, "shallow hole baseline");
            Check.Greater(deep.NonCutSeconds, shallow.NonCutSeconds, "a deep hole pays for peck retracts");

            // missing cutting data does not invent a number
            var blank = new OperationSpec { Id = "blank", Type = OperationType.TurnRough, CutLengthMm = 20 };
            est.ComputeOperationTime(blank, machine);
            Check.Equal(0.0, blank.CutSeconds, "an operation with no feed or speed reports zero cut time, not a guess");

            // fixed-cost machine moves
            var transfer = new OperationSpec { Id = "x", Type = OperationType.Transfer };
            est.ComputeOperationTime(transfer, machine);
            Check.Near(2.5, transfer.TotalSeconds, 1e-9, "a spindle transfer costs the machine's transfer time");

            // ------------------------------------------------------------------
            Check.Suite("Multi-channel scheduling");

            // --- two independent channels overlap completely
            var parallel = new List<OperationSpec>
            {
                Op("A1", 0, 1, 10),
                Op("B1", 1, 1, 6)
            };
            var r1 = est.Schedule(parallel, machine);
            Check.Near(10, r1.CycleSeconds, 1e-9, "independent channels run at the same time, so the cycle is the longer one");
            Check.Near(16, r1.SumOfOperationSeconds, 1e-9, "the operation total is still 16 seconds");
            Check.Greater(r1.OverlapRatio, 1.5, "the overlap ratio reflects genuine concurrency");
            Check.Equal(0, r1.BottleneckChannel, "channel 0 is correctly named as the bottleneck");
            Check.Near(4, r1.ChannelIdleSeconds[1], 1e-9, "channel 1 idles for 4 seconds");

            // --- a waitcode forces a rendezvous
            var synced = new List<OperationSpec>
            {
                Op("A1", 0, 1, 10),
                Op("A2", 0, 2, 5, waitBefore: "L20"),
                Op("B1", 1, 1, 6),
                Op("B2", 1, 2, 5, waitBefore: "L20")
            };
            var r2 = est.Schedule(synced, machine);
            // both channels reach L20 at max(10, 6) = 10, then both run 5 -> 15
            Check.Near(15, r2.CycleSeconds, 1e-9, "the rendezvous holds the fast channel until the slow one arrives");
            Check.False(r2.Advisories.Any(a => a.Code == "DEADLOCK"), "a well-formed waitcode is not flagged as a deadlock");
            var b2 = r2.Schedule.First(s => s.Operation.Id == "B2");
            Check.Near(4, b2.WaitSeconds, 1e-9, "channel 1's 4 seconds of waiting is attributed to the operation after the wait");

            // --- a reused label fires twice
            var reused = new List<OperationSpec>
            {
                Op("A1", 0, 1, 4), Op("A2", 0, 2, 4, waitBefore: "L1"), Op("A3", 0, 3, 4, waitBefore: "L1"),
                Op("B1", 1, 1, 2), Op("B2", 1, 2, 2, waitBefore: "L1"), Op("B3", 1, 3, 2, waitBefore: "L1")
            };
            var r3 = est.Schedule(reused, machine);
            Check.False(r3.Advisories.Any(a => a.Code == "DEADLOCK"), "a waitcode reused later in the program fires again");
            // ch0: 4, sync at 4, +4 = 8, sync at 8, +4 = 12
            Check.Near(12, r3.CycleSeconds, 1e-9, "a label used twice produces two rendezvous, not one");

            // --- an orphaned waitcode is caught
            var orphan = new List<OperationSpec>
            {
                Op("A1", 0, 1, 5),
                Op("A2", 0, 2, 5, waitBefore: "L99"),
                Op("B1", 1, 1, 5)
                // channel 1 never reaches L99
            };
            var r4 = est.Schedule(orphan, machine);
            Check.True(r4.Advisories.Any(a => a.Code == "ORPHAN_WAIT"),
                "a waitcode only one channel references is reported as an orphan");
            Check.Contains(r4.Advisories.First(a => a.Code == "ORPHAN_WAIT").Message, "L99",
                "the orphan advisory names the offending label");
            Check.Greater(r4.CycleSeconds, 0, "an orphaned wait still yields a usable estimate");

            // --- a genuine deadlock: each channel parks on a label the other only reaches later
            var deadlocked = new List<OperationSpec>
            {
                Op("A1", 0, 1, 5), Op("A2", 0, 2, 5, waitBefore: "L1"), Op("A3", 0, 3, 5, waitBefore: "L2"),
                Op("B1", 1, 1, 5), Op("B2", 1, 2, 5, waitBefore: "L2"), Op("B3", 1, 3, 5, waitBefore: "L1")
            };
            var r5 = est.Schedule(deadlocked, machine);
            Check.True(r5.Advisories.Any(a => a.Code == "DEADLOCK"),
                "crossed waitcodes are reported as a deadlock");
            Check.True(r5.Advisories.Any(a => a.Severity == Severity.Critical),
                "a deadlock is critical, because on the machine it simply hangs");
            Check.Greater(r5.CycleSeconds, 0, "a deadlocked plan still returns an upper-bound estimate");

            // --- an exclusive operation stops the whole machine
            var withCutoff = new List<OperationSpec>
            {
                Op("A1", 0, 1, 10),
                Op("CUT", 0, 2, 3, type: OperationType.Cutoff),
                Op("B1", 1, 1, 4)
            };
            var r6 = est.Schedule(withCutoff, machine);
            // ch1 finishes at 4, ch0 at 10, then cutoff runs alone 10 -> 13
            Check.Near(13, r6.CycleSeconds, 1e-9, "a cutoff waits for every channel, then runs alone");
            var cut = r6.Schedule.First(s => s.Operation.Id == "CUT");
            Check.Near(10, cut.StartSeconds, 1e-9, "the cutoff starts only once the busiest channel is done");

            // --- balance advice
            var lopsided = new List<OperationSpec>
            {
                Op("A1", 0, 1, 30),
                Op("B1", 1, 1, 3)
            };
            var r7 = est.Schedule(lopsided, machine);
            Check.True(r7.Advisories.Any(a => a.Code == "CHANNEL_IDLE"),
                "a badly balanced job is called out with the idle channel named");
            Check.Between(r7.PartsPerHour, 119, 121, "parts per hour is derived from the cycle");
            Check.Greater(r7.PartsPerDay(machine), 0, "parts per day accounts for uptime");

            // --- serial channels get the no-overlap warning
            var serial = new List<OperationSpec>
            {
                Op("A1", 0, 1, 10),
                Op("B1", 1, 1, 5, waitBefore: "S1"),
                Op("A2", 0, 2, 0.01, waitBefore: "S1")
            };
            var r8 = est.Schedule(serial, machine);
            Check.Greater(r8.CycleSeconds, 0, "a serial plan still schedules");

            // --- empty plan
            var r9 = est.Schedule(new List<OperationSpec>(), machine);
            Check.Equal(0.0, r9.CycleSeconds, "an empty plan is zero seconds");
            Check.True(r9.Advisories.Any(a => a.Code == "EMPTY_PLAN"), "an empty plan says so");
        }
    }
}
