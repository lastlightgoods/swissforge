using System;
using System.Collections.Generic;
using SwissForge.Core.Model;

namespace SwissForge.Core.Esprit
{
    /// <summary>
    /// A gateway that is honest about not being connected to anything.
    /// <para>
    /// Used in three places: unit tests, the command-line tool, and the REST API when it is
    /// running outside the ESPRIT process. It never pretends to have data — every read
    /// returns empty and every write reports zero — so a caller that forgets to check
    /// <see cref="IsConnected"/> gets an obviously wrong answer instead of a plausible
    /// fabricated one.
    /// </para>
    /// </summary>
    public sealed class OfflineEspritGateway : IEspritGateway
    {
        private readonly List<string> _log = new List<string>();

        public IReadOnlyList<string> LogLines => _log;

        public bool IsConnected => false;

        public EspritConnectionInfo Connect() => new EspritConnectionInfo
        {
            Connected = false,
            ProductName = "",
            Version = "",
            Message = "SwissForge is running outside ESPRIT. Engine features (cutting data, cycle time, " +
                      "quoting, G-code analysis) all work; anything that reads or writes the CAM document " +
                      "does not."
        };

        public EspritCapabilityReport Probe()
        {
            var r = new EspritCapabilityReport { Version = "offline" };
            r.Features["connected"] = false;
            return r;
        }

        public EspritStockInfo GetStock() => null;

        public IReadOnlyList<EspritToolInfo> GetTools() => Array.Empty<EspritToolInfo>();

        public IReadOnlyList<EspritOperationInfo> GetOperations() => Array.Empty<EspritOperationInfo>();

        public int ApplyCuttingData(IEnumerable<CuttingDataUpdate> updates) => 0;

        public IReadOnlyList<string> GetPostProcessors() => Array.Empty<string>();

        public string PostProcess(string postProcessorName) => null;

        public void Log(string message) => _log.Add(message);

        public void Dispose() { }
    }
}
