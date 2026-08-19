using System.Linq;
using SwissForge.Core.GCode;
using SwissForge.Core.Model;

namespace SwissForge.Tests
{
    public static class TestGCode
    {
        private const string CleanProgram = @"%
O1000 (SF-1001 MAIN)
$1
G21 G90 G97
G50 S6000
T0101 (OD ROUGH)
M8
G97 S4000 M3
G0 X10.5 Z0.1
G96 S200
G1 X-0.2 F0.08
G0 X10.5
G1 Z-20.0 F0.15
G0 X12.0 Z1.0
!1L20
T0303 (CUTOFF)
G97 S2500
G0 X10.5 Z-25.0
G1 X-0.2 F0.03
G0 X12.0
M9
M5
M30
$2
G21 G90 G97
T0202 (BACK DRILL)
M8
S3000 M3
!2L20
G0 Z1.0
G1 Z-8.0 F0.06
G0 Z5.0
M9
M5
M30
%";

        private const string BrokenProgram = @"%
O2000 (BROKEN)
$1
G21 G90
T0101
S4000 M3
G96 S250
G0 X10.5 Z0.1
G1 X-0.2
G0 X12.0
!1L30
M30
$2
G21 G90
G0 Z1.0
G1 Z-8.0
G2 X5.0 Z-10.0
M30
%";

        public static void Run()
        {
            Check.Suite("G-code parsing");

            var parser = new GParser();
            var profile = ControlProfile.For(ControlDialect.CitizenCincom);

            var prog = parser.Parse(CleanProgram, "SF-1001");
            prog.SplitChannels(profile);

            Check.True(prog.HasTapeMarkers, "the % tape markers are noticed");
            Check.Equal(2, prog.Channels.Count, "two channels are found from the $1/$2 markers");
            Check.True(prog.ProgramNumbers.Contains(1000), "the O number is read");

            var g1Blocks = prog.Blocks.Where(b => b.HasG(1)).ToList();
            Check.Greater(g1Blocks.Count, 0, "G1 feed moves are recognised");

            var feedBlock = prog.Blocks.First(b => b.Has('F'));
            Check.True(feedBlock.Get('F').HasValue, "F values are read");

            var comment = prog.Blocks.First(b => b.Comment.Contains("OD ROUGH"));
            Check.Contains(comment.Comment, "OD ROUGH", "parenthesised comments are captured");

            var negative = prog.Blocks.First(b => b.Has('X') && (b.Get('X') ?? 0) < 0);
            Check.Near(-0.2, negative.Get('X').Value, 1e-9, "negative coordinates parse correctly");

            var wait = prog.Blocks.First(b => !string.IsNullOrEmpty(b.WaitCodeRaw));
            Check.Equal("!1L20", wait.WaitCodeRaw, "the raw waitcode token is preserved");
            Check.Equal("L20", wait.WaitLabel, "the waitcode normalises to a comparable label");

            // channel assignment
            var ch0 = prog.BlocksIn(0).ToList();
            var ch1 = prog.BlocksIn(1).ToList();
            Check.Greater(ch0.Count, 5, "channel 0 has blocks");
            Check.Greater(ch1.Count, 5, "channel 1 has blocks");
            Check.True(ch1.Any(b => b.Comment.Contains("BACK DRILL")), "the $2 section lands in channel 1");

            // block delete and semicolon comment
            var odd = parser.Parse("/G0 X1.0 ; skip me\nG1 X2.0 F0.1");
            Check.True(odd.Blocks[0].IsBlockDelete, "a leading slash marks block delete");
            Check.Contains(odd.Blocks[0].Comment, "skip me", "a semicolon comment is captured");

            // unterminated comment
            var bad = parser.Parse("G0 X1.0 (never closed\nG1 X2.0");
            Check.Equal(1, bad.UnterminatedCommentLines.Count, "an unterminated comment is recorded");

            // ------------------------------------------------------------------
            Check.Suite("G-code linting: a clean program");

            var machine = new MachineProfile
            {
                Name = "Citizen L20", Dialect = ControlDialect.CitizenCincom,
                MainSpindleMaxRpm = 10000, SubSpindleMaxRpm = 8000,
                RapidRateMmPerMin = 32000, MaxZStrokePerPassMm = 200
            };

            var linter = new GCodeLinter { Machine = machine, Control = profile };
            var clean = linter.Lint(prog);

            Check.False(clean.Findings.Any(f => f.Code == "FEED_MOVE_NO_F"),
                "a program that sets F before cutting is not flagged");
            Check.False(clean.Findings.Any(f => f.Code == "CSS_NO_CLAMP"),
                "G96 preceded by G50 S is not flagged");
            Check.False(clean.Findings.Any(f => f.Code == "UNPAIRED_WAITCODE"),
                "a matched L20 across both channels is not flagged");
            Check.False(clean.Findings.Any(f => f.Code == "NO_END_OF_PROGRAM"),
                "both channels reach M30");
            Check.False(clean.HasCritical, "the clean program has no critical findings");

            // ------------------------------------------------------------------
            Check.Suite("G-code linting: catching real defects");

            var broken = parser.Parse(BrokenProgram, "BROKEN");
            broken.SplitChannels(profile);
            var report = linter.Lint(broken);

            Check.True(report.Findings.Any(f => f.Code == "CSS_NO_CLAMP"),
                "G96 with no G50 spindle clamp is caught");
            Check.Equal(Severity.Critical,
                report.Findings.First(f => f.Code == "CSS_NO_CLAMP").Severity,
                "an unclamped G96 on a bar machine is critical");
            Check.Contains(report.Findings.First(f => f.Code == "CSS_NO_CLAMP").Suggestion, "G50",
                "the fix is spelled out");

            Check.True(report.Findings.Any(f => f.Code == "FEED_MOVE_NO_F"),
                "a G1 with no F ever commanded is caught");

            Check.True(report.Findings.Any(f => f.Code == "CUT_WITHOUT_SPINDLE" && f.Channel == 1),
                "channel 1 cutting with no M3 is caught");

            Check.True(report.Findings.Any(f => f.Code == "UNPAIRED_WAITCODE"),
                "a waitcode with no partner channel is caught");
            Check.Equal(Severity.Critical,
                report.Findings.First(f => f.Code == "UNPAIRED_WAITCODE").Severity,
                "an unpaired waitcode is critical, because it hangs the machine");

            Check.True(report.Findings.Any(f => f.Code == "ARC_NO_GEOMETRY"),
                "a G2 with no R or I/K is caught");

            Check.True(report.Findings.Any(f => f.Code == "COOLANT_NEVER_ON"),
                "cutting with no coolant anywhere is flagged");

            Check.True(report.HasCritical, "the broken program reports critical findings");
            Check.False(report.IsClean, "the broken program is not clean");
            Check.Greater(report.Count(Severity.Critical), 2, "several critical defects are found");

            // findings carry enough context to act on
            var first = report.Findings.First(f => f.Severity == Severity.Critical);
            Check.Greater(first.LineNumber, 0, "a finding names the line");
            Check.True(first.Raw.Length > 0, "a finding quotes the offending code");

            // ------------------------------------------------------------------
            Check.Suite("G-code linting: individual rules");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS1000 M3\nG1 X1.0 F0\nM30", "ZERO_FEED",
                "F0 is caught");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS1000 M3\nG1 X1.0 F99999\nM30", "FEED_ABOVE_RAPID",
                "a feed above rapid rate is caught");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS50000 M3\nG1 X1.0 F0.1\nM30", "SPEED_ABOVE_MACHINE",
                "an S word above the machine's ceiling is caught");

            LintOne(linter, parser, profile, "O1\nG20\nG21\nS100 M3\nG1 X1 F1\nM30", "MIXED_UNITS",
                "G20 and G21 in the same program is caught");

            LintOne(linter, parser, profile, "O1\nO1\nG21\nM30", "DUPLICATE_O_NUMBER",
                "a duplicated O number is caught");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS100 M3 M8\nG1 X1 F1\nG91\nM30", "INCREMENTAL_LEFT_ON",
                "a program left in G91 is caught");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS100 M3 M8\nG1 X1 F1\nM9\nM30", "SPINDLE_LEFT_ON",
                "a program that ends without M5 is caught");

            LintOne(linter, parser, profile, "O1\nG21 G90\nS100 M3 M8\nG1 X1 F1\nM9 M5", "NO_END_OF_PROGRAM",
                "a program with no M30 is caught");

            LintOne(linter, parser, profile,
                "O1\nG21 G90\nT0101\nS100 M3 M8\nG1 X1 F1\nT0202\nG1 X2\nM9 M5 M30", "TOOLCHANGE_NO_RETRACT",
                "a tool change straight out of a cut is caught");

            // a waitcode that names a partner channel which never reaches it
            var mismatch = parser.Parse("O1\n$1\nS1 M3\n!2L40\nG1 X1 F1\nM30\n$2\nS1 M3\nG1 Z1 F1\nM30");
            mismatch.SplitChannels(profile);
            var mr = linter.Lint(mismatch);
            Check.True(mr.Findings.Any(f => f.Code == "UNPAIRED_WAITCODE" || f.Code == "WAIT_PARTNER_MISSING"),
                "a waitcode naming an absent partner is caught");

            // mismatched waitcode counts across channels
            var counts = parser.Parse(
                "O1\n$1\nS1 M3\n!2L1\nG1 X1 F1\n!2L1\nG1 X2\nM30\n$2\nS1 M3\n!1L1\nG1 Z1 F1\nM30");
            counts.SplitChannels(profile);
            var cr = linter.Lint(counts);
            Check.True(cr.Findings.Any(f => f.Code == "WAITCODE_COUNT_MISMATCH"),
                "a label used twice in one channel and once in another is caught");
        }

        private static void LintOne(GCodeLinter linter, GParser parser, ControlProfile profile,
                                    string code, string expectedFinding, string what)
        {
            var p = parser.Parse(code);
            p.SplitChannels(profile);
            var r = linter.Lint(p);
            if (r.Findings.Any(f => f.Code == expectedFinding)) Check.True(true, what);
            else Check.True(false, what + " -- found instead: " +
                string.Join(", ", r.Findings.Select(f => f.Code).Distinct()));
        }
    }
}
