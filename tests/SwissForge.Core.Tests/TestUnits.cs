using SwissForge.Core.Units;

namespace SwissForge.Tests
{
    public static class TestUnits
    {
        public static void Run()
        {
            Check.Suite("Units and machining relationships");

            Check.Near(25.4, U.InchToMm(1.0), 1e-9, "one inch is 25.4 mm");
            Check.Near(1.0, U.MmToInch(25.4), 1e-9, "25.4 mm is one inch");
            Check.Near(0.3048, U.SfmToMPerMin(1.0), 1e-9, "1 SFM is 0.3048 m/min");
            Check.Near(328.084, U.MPerMinToSfm(100), 0.01, "100 m/min is about 328 SFM");

            // A textbook check: 200 m/min on 12 mm stock.
            // n = 200 * 1000 / (pi * 12) = 5305 rpm
            Check.Near(5305, U.Rpm(200, 12), 1.0, "5305 rpm for 200 m/min on 12 mm");
            Check.Near(200, U.SurfaceSpeed(5305, 12), 0.1, "surface speed inverts back to 200 m/min");
            Check.Equal(0.0, U.Rpm(200, 0), "facing to centre reports zero rpm rather than dividing by zero");

            Check.Near(530.5, U.FeedMmPerMin(0.1, 5305), 0.1, "0.1 mm/rev at 5305 rpm is 530 mm/min");

            // Ra = f^2 / (32 r) * 1000. f=0.1, r=0.4 -> 0.01/12.8*1000 = 0.781 um
            Check.Near(0.781, U.TheoreticalRaMicron(0.1, 0.4), 0.001, "theoretical Ra for 0.1 mm/rev on a 0.4 nose");
            Check.Near(0.1, U.FeedForRa(0.781, 0.4), 0.001, "the Ra formula inverts to give the feed back");

            // A 12 mm x 1000 mm steel bar: pi * 0.6^2 * 100 cm3 = 113.1 cm3 * 7.85 g/cm3 = 888 g
            Check.Near(0.888, U.BarMassKg(12, 1000, 7.85), 0.002, "bar mass for 12 mm x 1 m of steel");

            Check.Equal("45.0s", U.FormatDuration(45), "formats seconds");
            Check.Equal("1m 23.4s", U.FormatDuration(83.4), "formats minutes and seconds");
            Check.Contains(U.FormatDuration(3725), "1h", "formats hours");

            Check.Contains(U.FormatSpeed(200, UnitSystem.Imperial), "656", "converts m/min to SFM for display");
            Check.Contains(U.FormatLength(25.4, UnitSystem.Imperial), "1.0000", "converts mm to inches for display");
        }
    }
}
