using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Feeds;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Units;

namespace SwissForge.Core.CycleTime
{
    /// <summary>One operation placed on the timeline.</summary>
    public sealed class ScheduledOperation
    {
        public OperationSpec Operation { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }

        /// <summary>Time this channel sat idle at a waitcode immediately before this op.</summary>
        public double WaitSeconds { get; set; }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("operationId", Operation.Id);
            j.Set("name", Operation.Name);
            j.Set("channel", Operation.Channel);
            j.Set("startSeconds", Math.Round(StartSeconds, 3));
            j.Set("endSeconds", Math.Round(EndSeconds, 3));
            j.Set("durationSeconds", Math.Round(EndSeconds - StartSeconds, 3));
            j.Set("waitSeconds", Math.Round(WaitSeconds, 3));
            return j;
        }
    }

    /// <summary>The scheduled result: cycle time and where it actually goes.</summary>
    public sealed class CycleTimeResult
    {
        /// <summary>Wall-clock time for one part, seconds. This is the number that matters.</summary>
        public double CycleSeconds { get; set; }

        /// <summary>Sum of every operation's duration. Bigger than the cycle time when channels overlap.</summary>
        public double SumOfOperationSeconds { get; set; }

        public Dictionary<int, double> ChannelBusySeconds { get; } = new Dictionary<int, double>();
        public Dictionary<int, double> ChannelIdleSeconds { get; } = new Dictionary<int, double>();

        public List<ScheduledOperation> Schedule { get; } = new List<ScheduledOperation>();
        public List<Advisory> Advisories { get; } = new List<Advisory>();

        /// <summary>The channel that is busy longest. Shortening anything else will not help.</summary>
        public int BottleneckChannel { get; set; }

        /// <summary>Overlap achieved: 1.0 means perfectly serial, higher means channels are working together.</summary>
        public double OverlapRatio => CycleSeconds > 0 ? SumOfOperationSeconds / CycleSeconds : 0;

        public double PartsPerHour => CycleSeconds > 0 ? 3600.0 / CycleSeconds : 0;

        public double PartsPerDay(MachineProfile m) =>
            PartsPerHour * m.ProductiveHoursPerDay * m.UtilizationFactor;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("cycleSeconds", Math.Round(CycleSeconds, 3));
            j.Set("cycleFormatted", U.FormatDuration(CycleSeconds));
            j.Set("sumOfOperationSeconds", Math.Round(SumOfOperationSeconds, 3));
            j.Set("overlapRatio", Math.Round(OverlapRatio, 3));
            j.Set("partsPerHour", Math.Round(PartsPerHour, 2));
            j.Set("bottleneckChannel", BottleneckChannel);

            var busy = JsonValue.Obj();
            foreach (var kv in ChannelBusySeconds.OrderBy(k => k.Key))
                busy.Set(kv.Key.ToString(), Math.Round(kv.Value, 3));
            j["channelBusySeconds"] = busy;

            var idle = JsonValue.Obj();
            foreach (var kv in ChannelIdleSeconds.OrderBy(k => k.Key))
                idle.Set(kv.Key.ToString(), Math.Round(kv.Value, 3));
            j["channelIdleSeconds"] = idle;

            var sched = JsonValue.Arr();
            foreach (var s in Schedule.OrderBy(x => x.StartSeconds)) sched.Add(s.ToJson());
            j["schedule"] = sched;

            var adv = JsonValue.Arr();
            foreach (var a in Advisories) adv.Add(a.ToJson());
            j["advisories"] = adv;
            return j;
        }
    }

    /// <summary>
    /// Times a Swiss job.
    /// <para>
    /// The thing that makes Swiss cycle time different from every other kind of machining
    /// estimate is that adding up the operations gives you the wrong answer. Two, three, or
    /// four channels cut at the same time, and the cycle is the critical path through the
    /// waitcode graph, not the sum of the work. A job with 40 seconds of operations can run
    /// in 18 seconds — or in 40, if the waitcodes are in the wrong places.
    /// </para>
    /// <para>
    /// This estimator schedules the operations against their waitcodes, reports the real
    /// cycle time, and — more usefully — reports how much each channel spent waiting, which
    /// is where the recoverable seconds live.
    /// </para>
    /// </summary>
    public sealed class CycleTimeEstimator
    {
        /// <summary>Peck depth as a multiple of drill diameter before a full retract.</summary>
        public double PeckDepthFactor { get; set; } = 3.0;

        /// <summary>Seconds lost per peck retract on a deep hole.</summary>
        public double PeckRetractSeconds { get; set; } = 0.15;

        /// <summary>
        /// Fills in <see cref="OperationSpec.CutSeconds"/> and
        /// <see cref="OperationSpec.NonCutSeconds"/> for one operation.
        /// </summary>
        public void ComputeOperationTime(OperationSpec op, MachineProfile machine)
        {
            if (op == null) throw new ArgumentNullException(nameof(op));
            machine = machine ?? new MachineProfile();

            double feedMmPerMin = U.FeedMmPerMin(op.FeedMmPerRev, op.Rpm);
            double cut = 0;
            double nonCut = 0;

            if (op.IsSyncMarker)
            {
                // A waitcode placeholder. The waiting itself is modelled by the scheduler.
                op.CutSeconds = 0;
                op.NonCutSeconds = 0;
                return;
            }

            switch (op.Type)
            {
                case OperationType.Transfer:
                    op.CutSeconds = 0;
                    op.NonCutSeconds = machine.TransferSeconds;
                    return;

                case OperationType.BarFeed:
                    op.CutSeconds = 0;
                    op.NonCutSeconds = machine.BarFeedSeconds;
                    return;
            }

            if (feedMmPerMin <= 0)
            {
                // No cutting data yet. Leave the time at zero rather than inventing one —
                // a silently fabricated cycle time is worse than an obviously missing one.
                op.CutSeconds = 0;
                op.NonCutSeconds = machine.ToolChangeSeconds;
                return;
            }

            double distancePerPass = Math.Max(op.CutLengthMm, 0);

            switch (op.Type)
            {
                case OperationType.Drill:
                case OperationType.BackDrill:
                case OperationType.CrossDrill:
                {
                    // Add the drill point's own length to the travel, then account for pecking.
                    double dia = op.WorkDiameterMm > 0 ? op.WorkDiameterMm : 1.0;
                    double pointLength = dia * 0.3;   // 118 degree point
                    double travel = distancePerPass + pointLength;
                    cut = travel / feedMmPerMin * 60.0;

                    double peckDepth = PeckDepthFactor * dia;
                    if (peckDepth > 0 && travel > peckDepth)
                    {
                        int pecks = (int)Math.Ceiling(travel / peckDepth) - 1;
                        if (pecks > 0)
                        {
                            // Each retract pulls clear and rapids back down.
                            double retractDistance = travel * 0.5;
                            double rapidBack = machine.RapidRateMmPerMin > 0
                                ? retractDistance / machine.RapidRateMmPerMin * 60.0
                                : 0;
                            nonCut += pecks * (PeckRetractSeconds + rapidBack);
                        }
                    }
                    break;
                }

                case OperationType.Tap:
                case OperationType.BackTap:
                {
                    // In and back out at the same synchronised feed.
                    cut = distancePerPass / feedMmPerMin * 60.0 * 2.0;
                    nonCut += 0.2;   // reversal
                    break;
                }

                case OperationType.Thread:
                {
                    // Each infeed pass runs the thread length, then rapids back to start.
                    double perPass = distancePerPass / feedMmPerMin * 60.0;
                    double retract = machine.RapidRateMmPerMin > 0
                        ? distancePerPass / machine.RapidRateMmPerMin * 60.0
                        : 0;
                    cut = perPass * Math.Max(op.Passes, 1);
                    nonCut += retract * Math.Max(op.Passes, 1);
                    break;
                }

                case OperationType.Cutoff:
                {
                    // Travel is from the OD to centre, plus a small overrun.
                    double radius = op.WorkDiameterMm / 2.0;
                    cut = (radius + 0.2) / feedMmPerMin * 60.0;
                    break;
                }

                case OperationType.Groove:
                {
                    cut = distancePerPass / feedMmPerMin * 60.0 * Math.Max(op.Passes, 1);
                    break;
                }

                default:
                {
                    cut = distancePerPass / feedMmPerMin * 60.0 * Math.Max(op.Passes, 1);
                    break;
                }
            }

            // Approach and retract at rapid, once per pass.
            if (machine.RapidRateMmPerMin > 0 && op.ApproachLengthMm > 0)
                nonCut += op.ApproachLengthMm * 2.0 / machine.RapidRateMmPerMin * 60.0 * Math.Max(op.Passes, 1);

            nonCut += machine.ToolChangeSeconds;
            nonCut += machine.SpindleRampSeconds;
            nonCut += op.DwellSeconds;

            op.CutSeconds = cut;
            op.NonCutSeconds = nonCut;
        }

        /// <summary>Times every operation in the plan.</summary>
        public void ComputeAllOperationTimes(IEnumerable<OperationSpec> ops, MachineProfile machine)
        {
            foreach (var op in ops) ComputeOperationTime(op, machine);
        }

        /// <summary>
        /// Schedules the operations across channels, honouring waitcodes and exclusive
        /// operations, and returns the resulting cycle time.
        /// </summary>
        public CycleTimeResult Schedule(IEnumerable<OperationSpec> operations, MachineProfile machine)
        {
            var result = new CycleTimeResult();
            machine = machine ?? new MachineProfile();

            var ops = operations?.ToList() ?? new List<OperationSpec>();
            if (ops.Count == 0)
            {
                result.Advisories.Add(new Advisory(Severity.Warning, "EMPTY_PLAN", "No operations to schedule."));
                return result;
            }

            // Operations grouped by channel, in programmed order.
            var channels = ops.GroupBy(o => o.Channel)
                              .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Sequence).ToList());

            var channelIds = channels.Keys.OrderBy(k => k).ToList();
            var cursor = channelIds.ToDictionary(c => c, c => 0.0);
            var index = channelIds.ToDictionary(c => c, c => 0);
            var busy = channelIds.ToDictionary(c => c, c => 0.0);
            var pendingWait = channelIds.ToDictionary(c => c, c => 0.0);

            // A channel is "released" once the rendezvous guarding its current operation has
            // fired. The flag is cleared as soon as that operation is consumed, so a waitcode
            // label reused later in the same program fires again — which is normal practice,
            // not an error.
            var released = channelIds.ToDictionary(c => c, c => false);

            // Which channels reference each waitcode label anywhere in their program.
            var labelChannels = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in ops)
            {
                if (string.IsNullOrEmpty(op.WaitBefore)) continue;
                if (!labelChannels.TryGetValue(op.WaitBefore, out var set))
                    labelChannels[op.WaitBefore] = set = new HashSet<int>();
                set.Add(op.Channel);
            }
            var labelOrder = labelChannels.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            // A waitcode referenced by only one channel is almost always a defect. Nobody
            // writes a rendezvous to wait for themselves: either the partner channel's
            // matching code was deleted, or the label was mistyped in one place. On a control
            // that expects a partner path this hangs the cycle; on one that does not it is
            // dead code. Either way the programmer wants to know.
            foreach (var label in labelOrder)
            {
                if (labelChannels[label].Count >= 2) continue;
                int lonely = labelChannels[label].First();
                result.Advisories.Add(new Advisory(Severity.Warning, "ORPHAN_WAIT",
                    $"Waitcode '{label}' appears only in channel {lonely}. A rendezvous needs at " +
                    "least two channels, so either the matching code in the other channel is missing " +
                    "or the label is mistyped. Scheduled as a no-op here so the estimate completes, " +
                    "but on the machine this is the line that hangs."));
            }

            bool Remaining(int ch) => index[ch] < channels[ch].Count;
            OperationSpec Next(int ch) => channels[ch][index[ch]];

            // A channel is parked when it cannot advance on its own: finished, blocked at an
            // unreleased waitcode, or holding an exclusive operation that needs the whole machine.
            bool Parked(int ch)
            {
                if (!Remaining(ch)) return true;
                var op = Next(ch);
                if (!string.IsNullOrEmpty(op.WaitBefore) && !released[ch]) return true;
                if (op.IsExclusive && channelIds.Count > 1) return true;
                return false;
            }

            void Emit(OperationSpec op, int ch, double start, double end)
            {
                result.Schedule.Add(new ScheduledOperation
                {
                    Operation = op,
                    StartSeconds = start,
                    EndSeconds = end,
                    WaitSeconds = pendingWait[ch]
                });
                pendingWait[ch] = 0;
                busy[ch] += op.TotalSeconds;
                index[ch]++;
                released[ch] = false;
            }

            int guard = 0;
            int maxIterations = ops.Count * 8 + 64;

            while (channelIds.Any(Remaining))
            {
                if (++guard > maxIterations)
                {
                    result.Advisories.Add(new Advisory(Severity.Critical, "SCHEDULER_STALL",
                        "The scheduler could not make progress. This normally means a waitcode cycle: " +
                        "two channels each parked on a label the other only reaches later."));
                    break;
                }

                bool progressed = false;

                // --- 1. run everything that is free to run right now
                foreach (var ch in channelIds)
                {
                    while (Remaining(ch))
                    {
                        var op = Next(ch);
                        if (!string.IsNullOrEmpty(op.WaitBefore) && !released[ch]) break;
                        if (op.IsExclusive && channelIds.Count > 1) break;

                        double start = cursor[ch];
                        double end = start + op.TotalSeconds;
                        Emit(op, ch, start, end);
                        cursor[ch] = end;
                        progressed = true;
                    }
                }

                if (!channelIds.Any(Remaining)) break;

                // --- 2. fire any rendezvous whose participants have all arrived
                bool fired = false;
                foreach (var label in labelOrder)
                {
                    var participants = labelChannels[label];

                    bool allArrived = participants.All(ch =>
                        Remaining(ch) &&
                        !released[ch] &&
                        string.Equals(Next(ch).WaitBefore, label, StringComparison.OrdinalIgnoreCase));

                    if (!allArrived) continue;

                    double rendezvous = participants.Max(ch => cursor[ch]);
                    foreach (var ch in participants)
                    {
                        pendingWait[ch] += rendezvous - cursor[ch];
                        cursor[ch] = rendezvous;
                        released[ch] = true;
                    }
                    fired = true;
                    progressed = true;
                }

                if (fired) continue;

                // --- 3. exclusive operations: cutoff, transfer, bar feed. The whole machine stops.
                int exclusiveChannel = -1;
                foreach (var ch in channelIds)
                {
                    if (!Remaining(ch)) continue;
                    var op = Next(ch);
                    if (!op.IsExclusive) continue;
                    if (!string.IsNullOrEmpty(op.WaitBefore) && !released[ch]) continue;
                    exclusiveChannel = ch;
                    break;
                }

                if (exclusiveChannel >= 0 && channelIds.All(Parked))
                {
                    var op = Next(exclusiveChannel);
                    double start = channelIds.Max(c => cursor[c]);
                    double end = start + op.TotalSeconds;

                    foreach (var ch in channelIds)
                    {
                        pendingWait[ch] += (ch == exclusiveChannel ? start : end) - cursor[ch];
                        cursor[ch] = end;
                    }

                    Emit(op, exclusiveChannel, start, end);
                    progressed = true;
                    continue;
                }

                if (progressed) continue;

                // --- nothing can advance: report the orphaned waits precisely
                foreach (var ch in channelIds.Where(Remaining))
                {
                    var op = Next(ch);
                    if (string.IsNullOrEmpty(op.WaitBefore)) continue;

                    var participants = labelChannels[op.WaitBefore];
                    var missing = participants
                        .Where(p => !Remaining(p) ||
                                    !string.Equals(Next(p).WaitBefore, op.WaitBefore, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    result.Advisories.Add(new Advisory(Severity.Critical, "DEADLOCK",
                        $"Channel {ch} is parked at waitcode '{op.WaitBefore}' before operation " +
                        $"'{op.Name}' and will never be released. " +
                        (missing.Count > 0
                            ? $"Channel(s) {string.Join(", ", missing)} declare the same label but do not reach it in step."
                            : "No other channel reaches this label.") +
                        " On the machine this hangs the cycle with every channel lit and nothing moving."));
                }

                // Drain the remainder serially so the caller still gets an upper-bound estimate
                // rather than a silently truncated one.
                foreach (var ch in channelIds.Where(Remaining).ToList())
                {
                    while (Remaining(ch))
                    {
                        var op = Next(ch);
                        double start = cursor[ch];
                        double end = start + op.TotalSeconds;
                        Emit(op, ch, start, end);
                        cursor[ch] = end;
                    }
                }
                break;
            }

            result.CycleSeconds = cursor.Count > 0 ? cursor.Values.Max() : 0;
            result.SumOfOperationSeconds = ops.Sum(o => o.TotalSeconds);

            foreach (var ch in channelIds)
            {
                result.ChannelBusySeconds[ch] = busy[ch];
                result.ChannelIdleSeconds[ch] = Math.Max(0, result.CycleSeconds - busy[ch]);
            }

            result.BottleneckChannel = busy.Count > 0
                ? busy.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key
                : 0;

            AddBalanceAdvisories(result, machine);
            return result;
        }

        private static void AddBalanceAdvisories(CycleTimeResult result, MachineProfile machine)
        {
            if (result.CycleSeconds <= 0 || result.ChannelBusySeconds.Count < 2) return;

            foreach (var kv in result.ChannelIdleSeconds.OrderByDescending(k => k.Value))
            {
                double idleFraction = kv.Value / result.CycleSeconds;
                if (idleFraction < 0.25) continue;

                result.Advisories.Add(new Advisory(
                    idleFraction > 0.5 ? Severity.Warning : Severity.Info, "CHANNEL_IDLE",
                    $"Channel {kv.Key} is idle {kv.Value:F1} s of a {result.CycleSeconds:F1} s cycle " +
                    $"({idleFraction * 100:F0}%). Moving work off channel {result.BottleneckChannel} onto it " +
                    $"is the cheapest cycle-time reduction available here."));
            }

            if (result.OverlapRatio < 1.15 && result.ChannelBusySeconds.Count >= 2)
                result.Advisories.Add(new Advisory(Severity.Warning, "NO_OVERLAP",
                    $"Overlap ratio is {result.OverlapRatio:F2}: the channels are running almost entirely " +
                    "in series. On a multi-channel Swiss that is leaving most of the machine on the table. " +
                    "Look for operations that do not need to wait on each other."));

            double bottleneckBusy = result.ChannelBusySeconds[result.BottleneckChannel];
            double theoretical = result.SumOfOperationSeconds / result.ChannelBusySeconds.Count;
            if (bottleneckBusy > theoretical * 1.4)
                result.Advisories.Add(new Advisory(Severity.Info, "UNBALANCED",
                    $"Channel {result.BottleneckChannel} carries {bottleneckBusy:F1} s against a " +
                    $"{theoretical:F1} s average. A perfectly balanced split would put the cycle near " +
                    $"{theoretical:F1} s."));
        }
    }
}
