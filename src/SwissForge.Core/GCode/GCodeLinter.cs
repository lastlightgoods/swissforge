using System;
using System.Collections.Generic;
using System.Linq;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Core.GCode
{
    /// <summary>One problem found in an NC program.</summary>
    public sealed class LintFinding
    {
        public Severity Severity { get; set; }
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string Suggestion { get; set; } = "";
        public int LineNumber { get; set; }
        public int Channel { get; set; }
        public string Raw { get; set; } = "";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("severity", Severity.ToString());
            j.Set("code", Code);
            j.Set("message", Message);
            j.Set("suggestion", Suggestion);
            j.Set("line", LineNumber);
            j.Set("channel", Channel);
            j.Set("raw", Raw);
            return j;
        }

        public override string ToString() =>
            $"{Severity,-8} {Code,-24} ch{Channel} line {LineNumber}: {Message}";
    }

    /// <summary>The result of linting one program.</summary>
    public sealed class LintReport
    {
        public string ProgramName { get; set; } = "";
        public List<LintFinding> Findings { get; } = new List<LintFinding>();

        public int Count(Severity s) => Findings.Count(f => f.Severity == s);
        public bool HasCritical => Findings.Any(f => f.Severity == Severity.Critical);
        public bool HasErrors => Findings.Any(f => f.Severity >= Severity.Error);

        /// <summary>True when nothing worse than an informational note was found.</summary>
        public bool IsClean => !Findings.Any(f => f.Severity >= Severity.Warning);

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("programName", ProgramName);
            j.Set("critical", Count(Severity.Critical));
            j.Set("errors", Count(Severity.Error));
            j.Set("warnings", Count(Severity.Warning));
            j.Set("info", Count(Severity.Info));
            j.Set("isClean", IsClean);
            var arr = JsonValue.Arr();
            foreach (var f in Findings.OrderByDescending(x => x.Severity).ThenBy(x => x.LineNumber))
                arr.Add(f.ToJson());
            j["findings"] = arr;
            return j;
        }
    }

    /// <summary>Per-channel modal state as the linter walks the program.</summary>
    internal sealed class ModalState
    {
        public int MotionMode = -1;
        public bool FeedSet;
        public double Feed;
        public bool SpindleRunning;
        public double SpindleSpeed;
        public bool ConstantSurfaceSpeed;      // G96 active
        public bool SpindleClamped;            // G50 S limit seen while CSS is in play
        public bool CoolantOn;
        public bool AnyCoolantEverOn;
        public bool Incremental;               // G91
        public int ActiveTool = -1;
        public bool SawEndOfProgram;
        public bool SawCuttingMove;
        public bool SawToolCall;
        public int LastRapidLine = -1;
        public int LastCuttingLine = -1;
        public double? LastZ;
    }

    /// <summary>
    /// Static analysis for Swiss NC programs.
    /// <para>
    /// The rules here are the ones that cost money: a cut with no feed rate, constant
    /// surface speed with no spindle clamp, a waitcode with no partner. None of them are
    /// exotic — they are what actually shows up in hand-edited multi-channel programs after
    /// a rushed engineering change, and every one of them is cheaper to catch here than at
    /// the machine.
    /// </para>
    /// <para>
    /// The linter is deliberately conservative about what it calls Critical. A tool that
    /// cries wolf gets switched off, and then it catches nothing at all.
    /// </para>
    /// </summary>
    public sealed class GCodeLinter
    {
        public MachineProfile Machine { get; set; }
        public ControlProfile Control { get; set; }

        /// <summary>Report block-delete lines. Off by default; plenty of shops use them legitimately.</summary>
        public bool ReportBlockDeletes { get; set; }

        public LintReport Lint(GProgram program)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));

            var machine = Machine ?? new MachineProfile();
            var control = Control ?? ControlProfile.For(machine.Dialect);
            var report = new LintReport { ProgramName = program.Name };

            void Add(Severity sev, string code, string message, GBlock b, string suggestion = "")
            {
                report.Findings.Add(new LintFinding
                {
                    Severity = sev,
                    Code = code,
                    Message = message,
                    Suggestion = suggestion,
                    LineNumber = b?.LineNumber ?? 0,
                    Channel = b?.Channel ?? 0,
                    Raw = b?.Raw?.Trim() ?? ""
                });
            }

            // ---------------------------------------------------------- file-level checks
            foreach (var line in program.UnterminatedCommentLines)
                report.Findings.Add(new LintFinding
                {
                    Severity = Severity.Error,
                    Code = "UNTERMINATED_COMMENT",
                    Message = $"A '(' comment on line {line} is never closed.",
                    Suggestion = "Close the parenthesis. Most controls swallow the rest of the block, " +
                                 "which silently deletes whatever came after it.",
                    LineNumber = line
                });

            foreach (var token in program.UnparsedTokens.Take(50))
                report.Findings.Add(new LintFinding
                {
                    Severity = Severity.Info,
                    Code = "UNPARSED_TOKEN",
                    Message = $"Not understood by the parser: {token}",
                    Suggestion = "Usually a macro expression or a vendor extension. Reported so you know " +
                                 "the analysis below did not account for it."
                });

            if (program.UnparsedTokens.Count > 50)
                report.Findings.Add(new LintFinding
                {
                    Severity = Severity.Info,
                    Code = "UNPARSED_TOKEN",
                    Message = $"...and {program.UnparsedTokens.Count - 50} more unparsed tokens not listed."
                });

            var oNumbers = program.Blocks.Where(b => b.Has('O'))
                                         .Select(b => new { Block = b, N = (int)Math.Round(b.Get('O') ?? 0) })
                                         .ToList();
            foreach (var dup in oNumbers.GroupBy(x => x.N).Where(g => g.Count() > 1))
                Add(Severity.Error, "DUPLICATE_O_NUMBER",
                    $"Program number O{dup.Key} is declared {dup.Count()} times.",
                    dup.First().Block,
                    "Two programs with the same number in one file: whichever loads second wins, " +
                    "and it will not be the one you expect.");

            bool sawG20 = program.Blocks.Any(b => b.HasG(20));
            bool sawG21 = program.Blocks.Any(b => b.HasG(21));
            if (sawG20 && sawG21)
                Add(Severity.Critical, "MIXED_UNITS",
                    "The program contains both G20 (inch) and G21 (metric).",
                    program.Blocks.First(b => b.HasG(20) || b.HasG(21)),
                    "Pick one. A units switch mid-program turns every subsequent coordinate into " +
                    "a different number than it looks like.");

            // ---------------------------------------------------------- per-channel walk
            foreach (var channel in program.Channels)
            {
                var blocks = program.BlocksIn(channel).ToList();
                var st = new ModalState();

                // A missing F word or an unclamped G96 stays true for every subsequent block,
                // so reporting per block buries the program in identical findings. Each of
                // these state defects is reported once per channel, at the first line where it
                // bites. A linter that repeats itself forty times gets switched off, and then
                // it catches nothing at all.
                var reportedOnce = new HashSet<string>(StringComparer.Ordinal);
                bool ReportOnce(string code) => reportedOnce.Add(code);

                foreach (var b in blocks)
                {
                    if (b.IsBlockDelete && ReportBlockDeletes)
                        Add(Severity.Info, "BLOCK_DELETE",
                            "Block-delete line. Whether this runs depends on a switch on the machine.",
                            b);

                    // --- modal updates: motion
                    foreach (var g in b.GCodes)
                    {
                        switch (g)
                        {
                            case 0: case 1: case 2: case 3: st.MotionMode = g; break;
                            case 90: st.Incremental = false; break;
                            case 91: st.Incremental = true; break;
                            case 96: st.ConstantSurfaceSpeed = true; break;
                            case 97: st.ConstantSurfaceSpeed = false; break;
                            case 50:
                                if (b.Has('S'))
                                {
                                    st.SpindleClamped = true;
                                }
                                else if (!b.HasMotionWord)
                                {
                                    Add(Severity.Warning, "G50_NO_S",
                                        "G50 with no S value does not clamp the spindle.",
                                        b,
                                        "If this was meant to be a speed clamp for G96, give it an S. " +
                                        "If it was a work-shift, ignore this.");
                                }
                                break;
                        }
                    }

                    if (b.Has('F'))
                    {
                        var f = b.Get('F') ?? 0;
                        st.Feed = f;
                        st.FeedSet = true;

                        if (f <= 0)
                            Add(Severity.Critical, "ZERO_FEED",
                                $"F{f} commands a zero or negative feed rate.",
                                b,
                                "The control will either alarm or sit in the cut. Neither is what you want.");
                        else if (machine.RapidRateMmPerMin > 0 && f > machine.RapidRateMmPerMin)
                            Add(Severity.Warning, "FEED_ABOVE_RAPID",
                                $"F{f:F0} is above the machine's rapid rate of {machine.RapidRateMmPerMin:F0} mm/min.",
                                b,
                                "Almost always a decimal-point slip or an inch/metric mix-up.");
                    }

                    if (b.Has('S'))
                    {
                        var s = b.Get('S') ?? 0;
                        if (!b.HasG(50)) st.SpindleSpeed = s;

                        double ceiling = b.Channel > 0 && machine.HasSubSpindle
                            ? Math.Max(machine.MainSpindleMaxRpm, machine.SubSpindleMaxRpm)
                            : machine.MainSpindleMaxRpm;

                        if (!st.ConstantSurfaceSpeed && ceiling > 0 && s > ceiling)
                            Add(Severity.Error, "SPEED_ABOVE_MACHINE",
                                $"S{s:F0} exceeds the {ceiling:F0} rpm this machine can turn.",
                                b,
                                "The control will clamp it, so the part is safe but the cutting data in " +
                                "your setup sheet is fiction.");

                        if (s < 0)
                            Add(Severity.Error, "NEGATIVE_SPEED", $"S{s} is negative.", b);
                    }

                    foreach (var m in b.MCodes)
                    {
                        if (control.SpindleOnMCodes.Contains(m)) st.SpindleRunning = true;
                        else if (control.SpindleOffMCodes.Contains(m)) st.SpindleRunning = false;
                        else if (control.CoolantOnMCodes.Contains(m)) { st.CoolantOn = true; st.AnyCoolantEverOn = true; }
                        else if (control.CoolantOffMCodes.Contains(m)) st.CoolantOn = false;
                        else if (control.EndOfProgramMCodes.Contains(m)) st.SawEndOfProgram = true;
                    }

                    if (b.Has('T'))
                    {
                        int t = (int)Math.Round(b.Get('T') ?? 0);
                        st.SawToolCall = true;

                        // A tool change while the last thing we did was cut, with no rapid in
                        // between, means the turret indexes from wherever the tool stopped.
                        if (st.LastCuttingLine > st.LastRapidLine && st.LastCuttingLine > 0 && t != st.ActiveTool)
                            Add(Severity.Warning, "TOOLCHANGE_NO_RETRACT",
                                $"Tool change to T{t} with no rapid move since the cut on line {st.LastCuttingLine}.",
                                b,
                                "Add a retract to a safe position before indexing, or the next tool " +
                                "may arrive somewhere the part already is.");

                        st.ActiveTool = t;
                    }

                    // --- motion analysis
                    bool isCuttingMove = (st.MotionMode == 1 || st.MotionMode == 2 || st.MotionMode == 3)
                                          && b.HasMotionWord;
                    bool isRapid = st.MotionMode == 0 && b.HasMotionWord;

                    if (isRapid) st.LastRapidLine = b.LineNumber;

                    if (isCuttingMove)
                    {
                        st.SawCuttingMove = true;
                        st.LastCuttingLine = b.LineNumber;

                        if (!st.FeedSet && ReportOnce("FEED_MOVE_NO_F"))
                            Add(Severity.Critical, "FEED_MOVE_NO_F",
                                "A feed move runs before any F word has been commanded in this channel.",
                                b,
                                "The control uses whatever feed was left over from the last program, " +
                                "or alarms. Both are bad, and which one you get depends on the machine.");

                        if (!st.SpindleRunning && ReportOnce("CUT_WITHOUT_SPINDLE"))
                            Add(Severity.Error, "CUT_WITHOUT_SPINDLE",
                                "A feed move runs with no spindle command active in this channel.",
                                b,
                                "If the spindle is genuinely started elsewhere (another channel, or a " +
                                "sub-program), ignore this. Otherwise it is a tool pushed into stationary stock.");

                        if (st.ConstantSurfaceSpeed && !st.SpindleClamped && ReportOnce("CSS_NO_CLAMP"))
                            Add(Severity.Critical, "CSS_NO_CLAMP",
                                "Constant surface speed (G96) is active with no G50 spindle clamp.",
                                b,
                                "As X approaches centre the commanded rpm approaches infinity. On a bar " +
                                "machine this is the single most expensive missing line in the program. " +
                                "Add G50 S<max> before the G96.");
                    }

                    // --- arcs
                    if ((b.HasG(2) || b.HasG(3)) && !(b.Has('R') || b.Has('I') || b.Has('J') || b.Has('K')))
                        Add(Severity.Error, "ARC_NO_GEOMETRY",
                            "G2/G3 with no R and no I/J/K.",
                            b,
                            "The control cannot know where the arc centre is.");

                    // --- Z travel sanity
                    if (b.Has('Z') && !st.Incremental && machine.MaxZStrokePerPassMm > 0)
                    {
                        double z = b.Get('Z') ?? 0;
                        if (Math.Abs(z) > machine.MaxZStrokePerPassMm * 1.5)
                            Add(Severity.Warning, "Z_BEYOND_STROKE",
                                $"Z{z:F3} is well beyond the {machine.MaxZStrokePerPassMm:F0} mm stroke " +
                                "this machine can make in one pass.",
                                b,
                                "Either the part needs re-gripping, or a decimal point moved.");
                        st.LastZ = z;
                    }
                }

                // ------------------------------------------------------ end-of-channel checks
                var lastBlock = blocks.LastOrDefault(x => !x.IsEmpty) ?? blocks.LastOrDefault();

                if (!st.SawEndOfProgram && st.SawCuttingMove)
                    Add(Severity.Error, "NO_END_OF_PROGRAM",
                        $"Channel {channel} never reaches M30, M2, or M99.",
                        lastBlock,
                        "The control runs off the end of the program.");

                if (st.SawCuttingMove && !st.AnyCoolantEverOn)
                    Add(Severity.Warning, "COOLANT_NEVER_ON",
                        $"Channel {channel} cuts metal but never turns coolant on.",
                        lastBlock,
                        "Fine if coolant is handled in another channel or left on at the panel. " +
                        "Worth a look otherwise.");

                if (st.CoolantOn && st.SawEndOfProgram)
                    Add(Severity.Info, "COOLANT_LEFT_ON",
                        $"Channel {channel} reaches end of program with coolant still commanded on.",
                        lastBlock,
                        "Harmless on most controls, which reset at M30. Untidy on the ones that do not.");

                if (st.SpindleRunning && st.SawEndOfProgram)
                    Add(Severity.Warning, "SPINDLE_LEFT_ON",
                        $"Channel {channel} reaches end of program with the spindle still running.",
                        lastBlock,
                        "Add an M5 before the end so the next setup does not start with a live spindle.");

                if (st.Incremental)
                    Add(Severity.Warning, "INCREMENTAL_LEFT_ON",
                        $"Channel {channel} ends in G91 incremental mode.",
                        lastBlock,
                        "The next program to run inherits it, and every absolute coordinate in that " +
                        "program becomes a relative move.");

                if (st.SawCuttingMove && !st.SawToolCall)
                    Add(Severity.Info, "NO_TOOL_CALL",
                        $"Channel {channel} cuts without ever calling a tool.",
                        lastBlock,
                        "Normal for a gang-tool channel that relies on offsets. Worth confirming.");

                if (!st.SawCuttingMove && blocks.Count(x => !x.IsEmpty) > 3)
                    Add(Severity.Info, "CHANNEL_NO_CUTTING",
                        $"Channel {channel} has code but never makes a cutting move.",
                        lastBlock);
            }

            LintWaitCodes(program, report);
            return report;
        }

        /// <summary>
        /// Cross-channel waitcode analysis. This is where multi-channel programs actually break.
        /// </summary>
        private static void LintWaitCodes(GProgram program, LintReport report)
        {
            var waits = program.Blocks
                .Where(b => !string.IsNullOrEmpty(b.WaitLabel))
                .ToList();

            if (waits.Count == 0) return;

            var byLabel = waits.GroupBy(b => b.WaitLabel, StringComparer.OrdinalIgnoreCase);

            foreach (var group in byLabel)
            {
                var channels = group.Select(b => b.Channel).Distinct().ToList();

                if (channels.Count < 2)
                {
                    var first = group.First();
                    report.Findings.Add(new LintFinding
                    {
                        Severity = Severity.Critical,
                        Code = "UNPAIRED_WAITCODE",
                        Message = $"Waitcode '{group.Key}' appears only in channel {channels[0]}.",
                        Suggestion = "A rendezvous needs at least two channels. Either the matching code " +
                                     "in the partner channel was deleted, or the label is mistyped. " +
                                     "On the machine this is the line the cycle hangs on.",
                        LineNumber = first.LineNumber,
                        Channel = first.Channel,
                        Raw = first.Raw.Trim()
                    });
                    continue;
                }

                // Counts must match: three of a label in channel 0 and two in channel 1 means
                // the channels will drift out of step partway through the cycle.
                var counts = group.GroupBy(b => b.Channel).ToDictionary(g => g.Key, g => g.Count());
                if (counts.Values.Distinct().Count() > 1)
                {
                    var first = group.First();
                    var detail = string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"channel {k.Key}: {k.Value}"));
                    report.Findings.Add(new LintFinding
                    {
                        Severity = Severity.Critical,
                        Code = "WAITCODE_COUNT_MISMATCH",
                        Message = $"Waitcode '{group.Key}' appears a different number of times in each channel ({detail}).",
                        Suggestion = "The channels rendezvous a different number of times, so after the last " +
                                     "matched pair they are permanently out of step. This one is worth " +
                                     "resolving before the machine ever sees the program.",
                        LineNumber = first.LineNumber,
                        Channel = first.Channel,
                        Raw = first.Raw.Trim()
                    });
                }

                // Explicit partner declarations, where the dialect provides them
                foreach (var b in group)
                {
                    if (b.WaitPartners.Count == 0) continue;
                    foreach (var partner in b.WaitPartners)
                    {
                        if (partner == b.Channel) continue;
                        if (channels.Contains(partner)) continue;

                        report.Findings.Add(new LintFinding
                        {
                            Severity = Severity.Critical,
                            Code = "WAIT_PARTNER_MISSING",
                            Message = $"Waitcode '{b.WaitCodeRaw}' in channel {b.Channel} names channel " +
                                      $"{partner + 1} as its partner, but channel {partner} never reaches '{b.WaitLabel}'.",
                            Suggestion = "The named channel will never post this label, so this channel waits forever.",
                            LineNumber = b.LineNumber,
                            Channel = b.Channel,
                            Raw = b.Raw.Trim()
                        });
                    }
                }
            }
        }
    }
}
