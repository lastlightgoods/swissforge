using System;
using System.Diagnostics;

namespace SwissForge.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            var sw = Stopwatch.StartNew();
            Console.WriteLine("SwissForge core verification");

            TestJson.Run();
            TestUnits.Run();
            TestFeeds.Run();
            TestCycleTime.Run();
            TestQuoting.Run();
            TestGCode.Run();
            TestPlanner.Run();
            TestApi.Run();
            TestIntegration.Run();

            sw.Stop();
            return Check.Report(sw);
        }
    }
}
