using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SwissForge.Core.Model;

namespace SwissForge.Core.GCode
{
    /// <summary>
    /// How a particular control family writes channels and waitcodes.
    /// <para>
    /// Every Swiss builder solved multi-channel synchronisation slightly differently, and
    /// the syntax is where portable tooling usually falls over. This type isolates the
    /// differences so the analyser and linter above it stay dialect-agnostic.
    /// </para>
    /// </summary>
    public sealed class ControlProfile
    {
        public ControlDialect Dialect { get; set; } = ControlDialect.FanucGeneric;
        public string Name { get; set; } = "";

        /// <summary>Marker that opens a channel section, e.g. "$".</summary>
        public string ChannelPrefix { get; set; } = "$";

        /// <summary>True when waitcodes are written as M codes in a reserved numeric range.</summary>
        public bool UsesMCodeWaits { get; set; }

        public int WaitMCodeMin { get; set; } = 100;
        public int WaitMCodeMax { get; set; } = 199;

        /// <summary>True when waitcodes are written with a bang, e.g. <c>!1L20</c>.</summary>
        public bool UsesBangWaits { get; set; }

        /// <summary>Highest legal spindle speed the control will accept in an S word.</summary>
        public double MaxProgrammableS { get; set; } = 99999;

        /// <summary>Codes this control treats as end of program.</summary>
        public HashSet<int> EndOfProgramMCodes { get; set; } = new HashSet<int> { 2, 30, 99 };

        /// <summary>Coolant-on M codes.</summary>
        public HashSet<int> CoolantOnMCodes { get; set; } = new HashSet<int> { 7, 8, 51 };

        /// <summary>Coolant-off M codes.</summary>
        public HashSet<int> CoolantOffMCodes { get; set; } = new HashSet<int> { 9, 59 };

        /// <summary>Spindle-start M codes for the main spindle.</summary>
        public HashSet<int> SpindleOnMCodes { get; set; } = new HashSet<int> { 3, 4 };

        /// <summary>Spindle-stop M codes.</summary>
        public HashSet<int> SpindleOffMCodes { get; set; } = new HashSet<int> { 5 };

        /// <summary>
        /// Extracts the rendezvous label from a raw waitcode token, or null when the
        /// token is not a waitcode in this dialect.
        /// </summary>
        public string NormalizeWaitLabel(string raw, List<int> partnersOut)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            partnersOut?.Clear();

            if (raw[0] == '!')
            {
                // Citizen / Star form: !<channels>L<label>, e.g. "!2L20", "!23L5", or plain "!L20".
                var body = raw.Substring(1);
                int lIndex = body.IndexOf('L');
                if (lIndex < 0) lIndex = body.IndexOf('l');

                if (lIndex >= 0)
                {
                    var channelDigits = body.Substring(0, lIndex);
                    var label = body.Substring(lIndex + 1);

                    if (partnersOut != null)
                        foreach (var ch in channelDigits)
                            if (char.IsDigit(ch)) partnersOut.Add(ch - '0' - 1);   // 1-based in code, 0-based here

                    return "L" + label.Trim();
                }

                return body.Trim();
            }

            return null;
        }

        /// <summary>True when this M code is a synchronisation wait on this control.</summary>
        public bool IsWaitMCode(int m) =>
            UsesMCodeWaits && m >= WaitMCodeMin && m <= WaitMCodeMax;

        // ------------------------------------------------------------------ presets

        public static ControlProfile For(ControlDialect dialect)
        {
            switch (dialect)
            {
                case ControlDialect.CitizenCincom:
                    return new ControlProfile
                    {
                        Dialect = dialect,
                        Name = "Citizen Cincom (Mitsubishi Meldas)",
                        ChannelPrefix = "$",
                        UsesBangWaits = true,
                        UsesMCodeWaits = false,
                        MaxProgrammableS = 20000
                    };

                case ControlDialect.StarSR:
                    return new ControlProfile
                    {
                        Dialect = dialect,
                        Name = "Star SR / SB (Fanuc)",
                        ChannelPrefix = "$",
                        UsesBangWaits = true,
                        UsesMCodeWaits = true,
                        WaitMCodeMin = 100,
                        WaitMCodeMax = 199,
                        MaxProgrammableS = 20000
                    };

                case ControlDialect.TsugamiFanuc:
                    return new ControlProfile
                    {
                        Dialect = dialect,
                        Name = "Tsugami (Fanuc 31i)",
                        ChannelPrefix = "$",
                        UsesMCodeWaits = true,
                        WaitMCodeMin = 100,
                        WaitMCodeMax = 199,
                        MaxProgrammableS = 20000
                    };

                case ControlDialect.HanwhaXD:
                    return new ControlProfile
                    {
                        Dialect = dialect,
                        Name = "Hanwha XD (Fanuc)",
                        ChannelPrefix = "$",
                        UsesMCodeWaits = true,
                        MaxProgrammableS = 12000
                    };

                default:
                    return new ControlProfile
                    {
                        Dialect = ControlDialect.FanucGeneric,
                        Name = "Fanuc generic multipath",
                        ChannelPrefix = "$",
                        UsesMCodeWaits = true,
                        WaitMCodeMin = 100,
                        WaitMCodeMax = 199
                    };
            }
        }
    }
}
