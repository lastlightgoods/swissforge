using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SwissForge.Core.Json;
using SwissForge.Esprit;

namespace SwissForge.Probe
{
    /// <summary>
    /// Run this once on the machine that has ESPRIT installed.
    /// <para>
    /// It attaches to a running ESPRIT, reads the real COM type library, and writes it to
    /// JSON. The output is what lets the SwissForge adapter be corrected against your exact
    /// version instead of against assumptions. It reads only — it never modifies the document,
    /// never saves, and never posts.
    /// </para>
    /// </summary>
    public static class Program
    {
        public static int Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string outputPath = "swissforge-probe.json";
            bool full = false;
            int maxTypes = 60;
            string progId = null;

            for (int i = 0; i < argv.Length; i++)
            {
                switch (argv[i].ToLowerInvariant())
                {
                    case "--out": if (i + 1 < argv.Length) outputPath = argv[++i]; break;
                    case "--full": full = true; break;
                    case "--max": if (i + 1 < argv.Length) int.TryParse(argv[++i], out maxTypes); break;
                    case "--progid": if (i + 1 < argv.Length) progId = argv[++i]; break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                }
            }

            Console.WriteLine("SwissForge probe");
            Console.WriteLine("Reads ESPRIT's COM object model. Read-only: nothing is modified or saved.");
            Console.WriteLine();

            var report = JsonValue.Obj();
            report.Set("schema", "swissforge.probe/1");
            report.Set("generatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            report.Set("machine", Environment.MachineName);
            report.Set("os", Environment.OSVersion.ToString());
            report.Set("clr", Environment.Version.ToString());
            report.Set("processBitness", IntPtr.Size == 8 ? "x64" : "x86");

            // ---------------------------------------------------------- attach
            object application = null;
            string attachError = null;

            var progIds = progId != null ? new[] { progId } : EspritGateway.ProgIds;
            var attempts = JsonValue.Arr();

            foreach (var id in progIds)
            {
                Console.Write($"  Trying ProgID '{id}' ... ");
                try
                {
                    application = Marshal.GetActiveObject(id);
                    Console.WriteLine("attached.");
                    attempts.Add(JsonValue.Obj().Set("progId", id).Set("result", "attached"));
                    report.Set("progId", id);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("not running.");
                    attempts.Add(JsonValue.Obj().Set("progId", id).Set("result", ex.Message));
                    attachError = ex.Message;
                }
            }

            report["attachAttempts"] = attempts;

            if (application == null)
            {
                Console.WriteLine();
                Console.WriteLine("  Could not attach to ESPRIT.");
                Console.WriteLine();
                Console.WriteLine("  Check all of these:");
                Console.WriteLine("    1. ESPRIT is running, with a document open.");
                Console.WriteLine("    2. This probe and ESPRIT are running as the same Windows user.");
                Console.WriteLine("       If ESPRIT is elevated and this is not (or vice versa), the");
                Console.WriteLine("       Running Object Table is not shared and the attach always fails.");
                Console.WriteLine("    3. The bitness matches. This process is " +
                                  (IntPtr.Size == 8 ? "x64" : "x86") + ".");
                Console.WriteLine("    4. If your ProgID differs, pass it: --progid Your.ProgID");
                Console.WriteLine();

                report.Set("error", attachError ?? "no running instance found");
                Write(report, outputPath);
                return 1;
            }

            // ---------------------------------------------------------- identity
            var gateway = new EspritGateway();
            gateway.AttachTo(application);

            var connection = gateway.Connect();
            report["connection"] = connection.ToJson();
            Console.WriteLine($"  Product : {connection.ProductName}");
            Console.WriteLine($"  Version : {connection.Version}");
            Console.WriteLine($"  Document: {(string.IsNullOrEmpty(connection.DocumentPath) ? "(none open)" : connection.DocumentPath)}");
            Console.WriteLine();

            // ---------------------------------------------------------- member map check
            Console.WriteLine("  Checking the members SwissForge expects...");
            var capability = gateway.Probe();
            report["capability"] = capability.ToJson();

            foreach (var found in capability.AvailableInterfaces.Where(a => a.Contains("->")))
                Console.WriteLine($"    ok      {found}");
            foreach (var missing in capability.MissingExpectedMembers)
                Console.WriteLine($"    MISSING {missing}");
            Console.WriteLine();

            // ---------------------------------------------------------- live reads
            Console.WriteLine("  Reading the open document...");
            try
            {
                var stock = gateway.GetStock();
                report["stock"] = stock?.ToJson() ?? JsonValue.Null;
                Console.WriteLine(stock == null
                    ? "    stock      : not readable"
                    : $"    stock      : {stock.DiameterMm:F2} mm x {stock.LengthMm:F0} mm {stock.MaterialName}");

                var tools = gateway.GetTools();
                var toolsJson = JsonValue.Arr();
                foreach (var t in tools) toolsJson.Add(t.ToJson());
                report["tools"] = toolsJson;
                Console.WriteLine($"    tools      : {tools.Count} read");

                var operations = gateway.GetOperations();
                var opsJson = JsonValue.Arr();
                foreach (var o in operations) opsJson.Add(o.ToJson());
                report["operations"] = opsJson;
                Console.WriteLine($"    operations : {operations.Count} read");

                var posts = gateway.GetPostProcessors();
                report.Set("postProcessors", posts.ToList());
                Console.WriteLine($"    posts      : {posts.Count} found");
            }
            catch (Exception ex)
            {
                report.Set("readError", ex.Message);
                Console.WriteLine("    read failed: " + ex.Message);
            }
            Console.WriteLine();

            // ---------------------------------------------------------- type library
            Console.WriteLine("  Dumping the type library" + (full ? " (full)" : $" (first {maxTypes} types)") + "...");
            try
            {
                report["typeLibrary"] = TypeLibraryDumper.DumpObject(application, includeWholeLibrary: true,
                                                                    maxTypes: full ? 0 : maxTypes);
                Console.WriteLine("    done.");
            }
            catch (Exception ex)
            {
                report.Set("typeLibraryError", ex.Message);
                Console.WriteLine("    failed: " + ex.Message);
            }

            Console.WriteLine();
            Write(report, outputPath);

            Console.WriteLine();
            Console.WriteLine("  Send this file back and the SwissForge adapter can be corrected");
            Console.WriteLine("  against your exact ESPRIT build in one pass.");
            Console.WriteLine();
            Console.WriteLine("  It contains: your machine name, ESPRIT version, and the names of");
            Console.WriteLine("  tools, operations and post processors in the open document. If any");
            Console.WriteLine("  of that is sensitive, open a scratch document before running it, or");
            Console.WriteLine("  strip the 'tools' and 'operations' sections before sending.");

            gateway.Dispose();
            return 0;
        }

        private static void Write(JsonValue report, string path)
        {
            try
            {
                File.WriteAllText(path, report.ToJson(indent: true), new UTF8Encoding(false));
                var info = new FileInfo(path);
                Console.WriteLine($"  Written: {info.FullName}  ({info.Length / 1024.0:F1} KB)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Could not write the report: " + ex.Message);
                Console.WriteLine();
                Console.WriteLine(report.ToJson(indent: true));
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
SwissForge probe - dumps the ESPRIT COM object model

USAGE
  swissforge-probe [options]

OPTIONS
  --out <file>     Where to write the report   (default swissforge-probe.json)
  --full           Dump every type in the library, not just the first --max
  --max <n>        How many types to dump      (default 60, 0 for all)
  --progid <id>    Attach using this ProgID instead of the defaults

NOTES
  ESPRIT must already be running, ideally with a document open.
  Both processes must run as the same Windows user, or the Running Object
  Table is not shared and the attach cannot succeed.

  The probe only reads. It does not modify, save, or post anything.
");
        }
    }
}
