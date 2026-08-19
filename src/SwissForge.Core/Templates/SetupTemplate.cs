using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Core.Templates
{
    /// <summary>
    /// A shop's house style for how a Swiss job gets laid out: what order operations run in,
    /// which channel each kind of work goes on, and how waitcodes are named.
    /// <para>
    /// Templates are the difference between a planner that produces something technically
    /// valid and one that produces something your setup guys recognise. Every shop has an
    /// order they like; this is where that gets encoded once instead of re-argued per job.
    /// </para>
    /// </summary>
    public sealed class SetupTemplate
    {
        public string Id { get; set; } = "default";
        public string Name { get; set; } = "Default Swiss layout";
        public string Description { get; set; } = "";

        /// <summary>Order operations are laid out in on the main spindle.</summary>
        public List<OperationType> MainSpindleOrder { get; set; } = new List<OperationType>
        {
            OperationType.Face,
            OperationType.Drill,
            OperationType.TurnRough,
            OperationType.TurnFinish,
            OperationType.Groove,
            OperationType.Thread,
            OperationType.CrossDrill,
            OperationType.Knurl
        };

        /// <summary>Order operations are laid out in on the sub spindle.</summary>
        public List<OperationType> SubSpindleOrder { get; set; } = new List<OperationType>
        {
            OperationType.BackFace,
            OperationType.BackDrill,
            OperationType.BackTurn,
            OperationType.BackTap
        };

        /// <summary>Which channel a given tool station is programmed on.</summary>
        public Dictionary<ToolStation, int> StationChannel { get; set; } = new Dictionary<ToolStation, int>
        {
            { ToolStation.GangSlide, 0 },
            { ToolStation.TurretStation, 0 },
            { ToolStation.EndworkingSlide, 2 },
            { ToolStation.LiveToolFront, 0 },
            { ToolStation.LiveToolBack, 1 },
            { ToolStation.BackWorking, 1 },
            { ToolStation.Auxiliary, 0 }
        };

        /// <summary>Prefix for generated waitcode labels.</summary>
        public string WaitLabelPrefix { get; set; } = "L";

        /// <summary>First waitcode number the planner will allocate.</summary>
        public int FirstWaitNumber { get; set; } = 20;

        /// <summary>
        /// True to overlap sub-spindle work on the finished part with main-spindle work on
        /// the next one. This is the whole point of a Swiss machine and is on by default,
        /// but a shop running very short parts sometimes turns it off for simplicity.
        /// </summary>
        public bool OverlapBackWorking { get; set; } = true;

        /// <summary>Rapid approach distance the planner assigns to each operation, mm.</summary>
        public double ApproachLengthMm { get; set; } = 2.0;

        /// <summary>Insert a facing operation on a fresh bar even when the print does not call one out.</summary>
        public bool AlwaysFaceFirst { get; set; } = true;

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("description", Description);
            j.Set("mainSpindleOrder", MainSpindleOrder.Select(o => o.ToString()).ToList());
            j.Set("subSpindleOrder", SubSpindleOrder.Select(o => o.ToString()).ToList());
            var sc = JsonValue.Obj();
            foreach (var kv in StationChannel) sc.Set(kv.Key.ToString(), kv.Value);
            j["stationChannel"] = sc;
            j.Set("waitLabelPrefix", WaitLabelPrefix);
            j.Set("firstWaitNumber", FirstWaitNumber);
            j.Set("overlapBackWorking", OverlapBackWorking);
            j.Set("approachLengthMm", ApproachLengthMm);
            j.Set("alwaysFaceFirst", AlwaysFaceFirst);
            return j;
        }

        public static SetupTemplate FromJson(JsonValue j)
        {
            var t = new SetupTemplate
            {
                Id = j["id"].AsString("default"),
                Name = j["name"].AsString("Default Swiss layout"),
                Description = j["description"].AsString(""),
                WaitLabelPrefix = j["waitLabelPrefix"].AsString("L"),
                FirstWaitNumber = j["firstWaitNumber"].AsInt(20),
                OverlapBackWorking = j["overlapBackWorking"].AsBool(true),
                ApproachLengthMm = j["approachLengthMm"].AsDouble(2.0),
                AlwaysFaceFirst = j["alwaysFaceFirst"].AsBool(true)
            };

            if (j["mainSpindleOrder"].Count > 0)
                t.MainSpindleOrder = j["mainSpindleOrder"]
                    .Select(v => v.AsEnum(OperationType.Custom)).ToList();

            if (j["subSpindleOrder"].Count > 0)
                t.SubSpindleOrder = j["subSpindleOrder"]
                    .Select(v => v.AsEnum(OperationType.Custom)).ToList();

            if (j["stationChannel"].Count > 0)
            {
                t.StationChannel = new Dictionary<ToolStation, int>();
                foreach (var key in j["stationChannel"].Keys)
                    if (Enum.TryParse<ToolStation>(key, true, out var station))
                        t.StationChannel[station] = j["stationChannel"][key].AsInt(0);
            }

            return t;
        }

        /// <summary>
        /// Which channel this tool's work belongs on.
        /// <para>
        /// When the template asks for a channel the machine does not have, the fallback is
        /// deliberately not "the highest channel it does have". On a two-channel machine the
        /// highest channel is the sub spindle, and quietly moving endworking there would
        /// schedule front drilling against a part that is no longer in the main spindle.
        /// Back-working falls back to the last channel; everything else falls back to the main.
        /// </para>
        /// </summary>
        public int ChannelFor(ToolItem tool, MachineProfile machine)
        {
            int channel = StationChannel.TryGetValue(tool.Station, out var c) ? c : 0;
            int last = Math.Max(1, machine?.ChannelCount ?? 1) - 1;

            if (channel <= last) return channel;

            bool worksTheSubSpindle =
                tool.Station == ToolStation.BackWorking ||
                tool.Station == ToolStation.LiveToolBack ||
                tool.Side == SpindleSide.Sub;

            return worksTheSubSpindle ? last : 0;
        }
    }
}
