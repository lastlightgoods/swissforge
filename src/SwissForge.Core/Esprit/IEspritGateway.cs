using System;
using System.Collections.Generic;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.Core.Esprit
{
    /// <summary>Result of attaching to a running ESPRIT session.</summary>
    public sealed class EspritConnectionInfo
    {
        public bool Connected { get; set; }
        public string ProductName { get; set; } = "";
        public string Version { get; set; } = "";
        public string DocumentPath { get; set; } = "";
        public string Message { get; set; } = "";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("connected", Connected);
            j.Set("productName", ProductName);
            j.Set("version", Version);
            j.Set("documentPath", DocumentPath);
            j.Set("message", Message);
            return j;
        }
    }

    /// <summary>A tool as ESPRIT holds it.</summary>
    public sealed class EspritToolInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int StationNumber { get; set; }
        public double DiameterMm { get; set; }
        public double NoseRadiusMm { get; set; }
        public string ToolTypeName { get; set; } = "";
        public double SpeedRpm { get; set; }
        public double FeedMmPerRev { get; set; }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("stationNumber", StationNumber);
            j.Set("diameterMm", DiameterMm);
            j.Set("noseRadiusMm", NoseRadiusMm);
            j.Set("toolTypeName", ToolTypeName);
            j.Set("speedRpm", SpeedRpm);
            j.Set("feedMmPerRev", FeedMmPerRev);
            return j;
        }
    }

    /// <summary>An operation as ESPRIT holds it.</summary>
    public sealed class EspritOperationInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string TechnologyName { get; set; } = "";
        public string ToolId { get; set; } = "";
        public int Channel { get; set; }
        public double SpeedRpm { get; set; }
        public double FeedMmPerRev { get; set; }
        public double DepthOfCutMm { get; set; }

        /// <summary>Cycle time ESPRIT itself reports, seconds. Zero when unavailable.</summary>
        public double EspritCycleSeconds { get; set; }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("name", Name);
            j.Set("technologyName", TechnologyName);
            j.Set("toolId", ToolId);
            j.Set("channel", Channel);
            j.Set("speedRpm", SpeedRpm);
            j.Set("feedMmPerRev", FeedMmPerRev);
            j.Set("depthOfCutMm", DepthOfCutMm);
            j.Set("espritCycleSeconds", EspritCycleSeconds);
            return j;
        }
    }

    /// <summary>Stock definition read from the ESPRIT document.</summary>
    public sealed class EspritStockInfo
    {
        public double DiameterMm { get; set; }
        public double LengthMm { get; set; }
        public string MaterialName { get; set; } = "";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("diameterMm", DiameterMm);
            j.Set("lengthMm", LengthMm);
            j.Set("materialName", MaterialName);
            return j;
        }
    }

    /// <summary>A cutting-data change to push back into ESPRIT.</summary>
    public sealed class CuttingDataUpdate
    {
        public string OperationId { get; set; } = "";
        public double? SpeedRpm { get; set; }
        public double? FeedMmPerRev { get; set; }
        public double? DepthOfCutMm { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>What this build of ESPRIT actually exposes, as discovered at runtime.</summary>
    public sealed class EspritCapabilityReport
    {
        public string Version { get; set; } = "";
        public List<string> AvailableInterfaces { get; } = new List<string>();
        public List<string> MissingExpectedMembers { get; } = new List<string>();
        public Dictionary<string, bool> Features { get; } = new Dictionary<string, bool>();

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("version", Version);
            j.Set("availableInterfaces", AvailableInterfaces);
            j.Set("missingExpectedMembers", MissingExpectedMembers);
            var f = JsonValue.Obj();
            foreach (var kv in Features) f.Set(kv.Key, kv.Value);
            j["features"] = f;
            return j;
        }
    }

    /// <summary>
    /// Everything SwissForge asks of ESPRIT, in one place.
    /// <para>
    /// This interface exists to keep the COM surface contained. Every call into ESPRIT's
    /// object model happens behind an implementation of this type and nowhere else, which
    /// means three useful things: the engine layer can be developed and unit-tested with no
    /// ESPRIT installed, a version difference in the host application changes exactly one
    /// file, and a failure in the CAM system degrades into a handled error rather than
    /// taking the add-in down with it.
    /// </para>
    /// <para>
    /// Implementations must not throw for an ordinary "not available" condition — return an
    /// empty collection or a report saying so. An add-in that throws inside a COM callback
    /// can take the host down with it, and taking a programmer's unsaved work with it is not
    /// a forgivable bug.
    /// </para>
    /// </summary>
    public interface IEspritGateway : IDisposable
    {
        bool IsConnected { get; }

        /// <summary>Attaches to a running ESPRIT session, or reports why it could not.</summary>
        EspritConnectionInfo Connect();

        /// <summary>Interrogates the live object model and reports what this build supports.</summary>
        EspritCapabilityReport Probe();

        /// <summary>Reads the active document's stock definition.</summary>
        EspritStockInfo GetStock();

        /// <summary>Reads the tool list from the active document.</summary>
        IReadOnlyList<EspritToolInfo> GetTools();

        /// <summary>Reads the operation list from the active document.</summary>
        IReadOnlyList<EspritOperationInfo> GetOperations();

        /// <summary>Pushes cutting-data changes back into ESPRIT. Returns how many applied.</summary>
        int ApplyCuttingData(IEnumerable<CuttingDataUpdate> updates);

        /// <summary>Lists the post-processors configured in this installation.</summary>
        IReadOnlyList<string> GetPostProcessors();

        /// <summary>Runs a post and returns the generated NC text, or null on failure.</summary>
        string PostProcess(string postProcessorName);

        /// <summary>Writes a line to ESPRIT's output window, when the host provides one.</summary>
        void Log(string message);
    }
}
