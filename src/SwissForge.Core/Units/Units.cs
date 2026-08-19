using System;
using System.Globalization;

namespace SwissForge.Core.Units
{
    /// <summary>Which unit system the operator sees. Internally SwissForge is always metric.</summary>
    public enum UnitSystem { Metric, Imperial }

    /// <summary>
    /// Conversion helpers. SwissForge stores every dimension canonically in
    /// millimetres, every cutting speed in metres/minute, and every feed in
    /// millimetres/revolution, then converts only at UI and post-processor boundaries.
    /// Mixing units mid-calculation is the classic way to scrap a bar, so the rule is:
    /// convert on the way in, convert on the way out, never in the middle.
    /// </summary>
    public static class U
    {
        public const double MmPerInch = 25.4;

        // ---- length -------------------------------------------------------
        public static double InchToMm(double inch) => inch * MmPerInch;
        public static double MmToInch(double mm) => mm / MmPerInch;

        // ---- cutting speed ------------------------------------------------
        /// <summary>Surface feet per minute -> metres per minute.</summary>
        public static double SfmToMPerMin(double sfm) => sfm * 0.3048;
        /// <summary>Metres per minute -> surface feet per minute.</summary>
        public static double MPerMinToSfm(double mpm) => mpm / 0.3048;

        // ---- feed ---------------------------------------------------------
        /// <summary>Inches per revolution -> millimetres per revolution.</summary>
        public static double IprToMmRev(double ipr) => ipr * MmPerInch;
        /// <summary>Millimetres per revolution -> inches per revolution.</summary>
        public static double MmRevToIpr(double mmRev) => mmRev / MmPerInch;

        // ---- derived machining relationships -------------------------------

        /// <summary>
        /// Spindle speed (rev/min) for a given surface speed and diameter.
        /// n = (Vc * 1000) / (pi * D). Returns 0 for a zero diameter, which is the
        /// correct answer for facing to centre — the caller is expected to clamp
        /// to the machine's max RPM rather than divide by zero.
        /// </summary>
        public static double Rpm(double surfaceSpeedMPerMin, double diameterMm)
        {
            if (diameterMm <= 0) return 0;
            return surfaceSpeedMPerMin * 1000.0 / (Math.PI * diameterMm);
        }

        /// <summary>Surface speed (m/min) actually achieved at a given RPM and diameter.</summary>
        public static double SurfaceSpeed(double rpm, double diameterMm) =>
            Math.PI * diameterMm * rpm / 1000.0;

        /// <summary>Linear feed rate in mm/min from feed-per-rev and spindle speed.</summary>
        public static double FeedMmPerMin(double feedMmPerRev, double rpm) => feedMmPerRev * rpm;

        /// <summary>Material removal rate in cm3/min for turning: Vc * ap * fn.</summary>
        public static double TurningMrrCm3PerMin(double surfaceSpeedMPerMin, double depthOfCutMm, double feedMmPerRev) =>
            surfaceSpeedMPerMin * depthOfCutMm * feedMmPerRev;

        /// <summary>
        /// Theoretical turned surface roughness Ra in micrometres from feed and nose radius.
        /// Ra ~= (f^2 / (32 * r)) * 1000. Good enough to warn an operator that the
        /// finish pass they programmed cannot hit the print.
        /// </summary>
        public static double TheoreticalRaMicron(double feedMmPerRev, double noseRadiusMm)
        {
            if (noseRadiusMm <= 0) return double.PositiveInfinity;
            return feedMmPerRev * feedMmPerRev / (32.0 * noseRadiusMm) * 1000.0;
        }

        /// <summary>Inverse of <see cref="TheoreticalRaMicron"/>: the feed that yields a target Ra.</summary>
        public static double FeedForRa(double targetRaMicron, double noseRadiusMm)
        {
            if (noseRadiusMm <= 0 || targetRaMicron <= 0) return 0;
            return Math.Sqrt(targetRaMicron / 1000.0 * 32.0 * noseRadiusMm);
        }

        /// <summary>Mass in kilograms of a solid round bar.</summary>
        public static double BarMassKg(double diameterMm, double lengthMm, double densityGPerCm3)
        {
            double radiusCm = diameterMm / 20.0;
            double lengthCm = lengthMm / 10.0;
            double volumeCm3 = Math.PI * radiusCm * radiusCm * lengthCm;
            return volumeCm3 * densityGPerCm3 / 1000.0;
        }

        // ---- formatting ----------------------------------------------------

        public static string FormatLength(double mm, UnitSystem system, int decimalsMetric = 3, int decimalsImperial = 4) =>
            system == UnitSystem.Metric
                ? mm.ToString("F" + decimalsMetric, CultureInfo.InvariantCulture) + " mm"
                : MmToInch(mm).ToString("F" + decimalsImperial, CultureInfo.InvariantCulture) + " in";

        public static string FormatSpeed(double mPerMin, UnitSystem system) =>
            system == UnitSystem.Metric
                ? mPerMin.ToString("F0", CultureInfo.InvariantCulture) + " m/min"
                : MPerMinToSfm(mPerMin).ToString("F0", CultureInfo.InvariantCulture) + " SFM";

        public static string FormatFeed(double mmPerRev, UnitSystem system) =>
            system == UnitSystem.Metric
                ? mmPerRev.ToString("F4", CultureInfo.InvariantCulture) + " mm/rev"
                : MmRevToIpr(mmPerRev).ToString("F5", CultureInfo.InvariantCulture) + " in/rev";

        /// <summary>Seconds -> "1m 23.4s" for cycle-time display.</summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds < 60) return seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
            int minutes = (int)(seconds / 60);
            double rem = seconds - minutes * 60;
            if (minutes < 60) return minutes + "m " + rem.ToString("F1", CultureInfo.InvariantCulture) + "s";
            int hours = minutes / 60;
            minutes %= 60;
            return hours + "h " + minutes + "m " + rem.ToString("F0", CultureInfo.InvariantCulture) + "s";
        }
    }
}
