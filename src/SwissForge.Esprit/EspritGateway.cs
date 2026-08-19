using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SwissForge.Core.Esprit;

namespace SwissForge.Esprit
{
    /// <summary>
    /// The live ESPRIT adapter.
    /// <para>
    /// <b>Read this before trusting the member names below.</b> They are the conventional
    /// ESPRIT automation names and are correct for the classic object model, but they have
    /// not been verified against your specific ESPRIT EDGE build — Hexagon's API reference
    /// is not publicly retrievable and the type libraries only exist on a licensed install.
    /// </para>
    /// <para>
    /// Rather than guess and hope, every lookup goes through <see cref="Com.GetAny"/> with a
    /// list of candidate names, and <c>SwissForge.Probe</c> dumps your installation's real
    /// object model to JSON. Run the probe once, and the <see cref="MemberMap"/> below can be
    /// corrected in one place from ground truth. Nothing else in the codebase needs to change.
    /// </para>
    /// </summary>
    public sealed class EspritGateway : IEspritGateway
    {
        /// <summary>ProgIDs tried when attaching, most specific first.</summary>
        public static readonly string[] ProgIds =
        {
            "Esprit.Application",
            "ESPRIT.Application",
            "EspritApp.Application"
        };

        /// <summary>
        /// Candidate member names, in the order they are tried.
        /// <para>
        /// This is the single place that needs correcting if a name differs on your build.
        /// Add the real name to the front of the relevant array; leave the others as fallbacks
        /// so the add-in keeps working on other machines in the shop.
        /// </para>
        /// </summary>
        public static class MemberMap
        {
            public static string[] ActiveDocument = { "Document", "ActiveDocument" };
            public static string[] Version = { "Version", "ProductVersion", "AppVersion" };
            public static string[] ProductName = { "Name", "ProductName", "Application" };
            public static string[] DocumentPath = { "FullName", "Path", "FileName" };

            public static string[] Tools = { "Tools", "ToolList", "CuttingTools" };
            public static string[] Operations = { "Operations", "OperationList", "Processes" };
            public static string[] Stock = { "Stock", "StockGeometry", "BarStock" };
            public static string[] MachineSetup = { "MachineSetup", "Machine", "MachineDefinition" };

            public static string[] ItemName = { "Name", "Description", "Label", "Title" };
            public static string[] ItemId = { "ID", "Id", "Key", "Handle", "Index" };

            public static string[] ToolStation = { "StationNumber", "Station", "ToolNumber", "TurretPosition" };
            public static string[] ToolDiameter = { "Diameter", "ToolDiameter", "CutterDiameter" };
            public static string[] ToolNoseRadius = { "NoseRadius", "CornerRadius", "TipRadius" };
            public static string[] ToolType = { "ToolType", "Type", "TechnologyName" };

            public static string[] SpeedRpm = { "SpindleSpeed", "Speed", "RPM", "SpeedValue" };
            public static string[] FeedPerRev = { "FeedRate", "Feed", "FeedPerRevolution", "FeedValue" };
            public static string[] DepthOfCut = { "DepthOfCut", "StepDown", "CutDepth", "Depth" };
            public static string[] OperationTool = { "Tool", "CuttingTool", "ToolRef" };
            public static string[] OperationChannel = { "Channel", "ChannelIndex", "Path", "SpindleIndex" };
            public static string[] OperationCycleTime = { "CycleTime", "MachiningTime", "EstimatedTime" };
            public static string[] OperationTechnology = { "Technology", "TechnologyName", "OperationType" };

            public static string[] StockDiameter = { "Diameter", "OuterDiameter", "BarDiameter" };
            public static string[] StockLength = { "Length", "BarLength", "StockLength" };
            public static string[] StockMaterial = { "Material", "MaterialName", "StockMaterial" };

            public static string[] PostProcessors = { "PostProcessors", "Posts", "PostList" };
            public static string[] RunPost = { "PostProcess", "Post", "GenerateNCCode", "RunPost" };
            public static string[] NcCode = { "NCCode", "Code", "Output", "Text" };

            public static string[] OutputWindow = { "OutputWindow", "MessageWindow", "Output" };
            public static string[] WriteLine = { "WriteLine", "AddMessage", "Write", "Print" };
        }

        private Com _app;
        private string _connectError;
        private string _version = "";
        private string _productName = "";

        /// <summary>Called for every diagnostic. Wired to the add-in's log window.</summary>
        public Action<string> Diagnostic { get; set; }

        public bool IsConnected => _app != null && !_app.IsNull;

        /// <summary>Injects an already-obtained Application object, as the add-in does on connect.</summary>
        public void AttachTo(object espritApplication)
        {
            _app = new Com(espritApplication);
            _connectError = null;
            CacheIdentity();
        }

        public EspritConnectionInfo Connect()
        {
            if (IsConnected)
                return new EspritConnectionInfo
                {
                    Connected = true,
                    ProductName = _productName,
                    Version = _version,
                    DocumentPath = SafeDocumentPath(),
                    Message = "Attached."
                };

            string lastError = null;
            foreach (var progId in ProgIds)
            {
                var app = Com.GetRunningInstance(progId, out var error);
                if (app != null)
                {
                    _app = app;
                    CacheIdentity();
                    return new EspritConnectionInfo
                    {
                        Connected = true,
                        ProductName = _productName,
                        Version = _version,
                        DocumentPath = SafeDocumentPath(),
                        Message = $"Attached to a running instance via '{progId}'."
                    };
                }
                lastError = error;
            }

            _connectError = lastError;
            return new EspritConnectionInfo
            {
                Connected = false,
                Message = lastError ?? "No running ESPRIT instance was found."
            };
        }

        private void CacheIdentity()
        {
            _version = _app.GetAny(MemberMap.Version).AsString("unknown");
            _productName = _app.GetAny(MemberMap.ProductName).AsString("ESPRIT");
        }

        private string SafeDocumentPath()
        {
            try { return Document()?.GetAny(MemberMap.DocumentPath).AsString("") ?? ""; }
            catch { return ""; }
        }

        private Com Document()
        {
            if (!IsConnected) return null;
            var doc = _app.GetAny(MemberMap.ActiveDocument);
            return doc.IsNull ? null : doc;
        }

        // ------------------------------------------------------------------ probe

        public EspritCapabilityReport Probe()
        {
            var report = new EspritCapabilityReport { Version = _version };

            if (!IsConnected)
            {
                report.Features["connected"] = false;
                report.MissingExpectedMembers.Add(_connectError ?? "not connected");
                return report;
            }

            report.Features["connected"] = true;

            void CheckMember(string label, Com parent, string[] candidates)
            {
                if (parent == null || parent.IsNull)
                {
                    report.MissingExpectedMembers.Add(label + " (parent unavailable)");
                    report.Features[label] = false;
                    return;
                }

                foreach (var name in candidates)
                {
                    var v = parent.TryGet(name);
                    if (v.IsNull) continue;
                    report.AvailableInterfaces.Add($"{label} -> {name}");
                    report.Features[label] = true;
                    return;
                }

                report.MissingExpectedMembers.Add(
                    $"{label}: none of [{string.Join(", ", candidates)}] resolved");
                report.Features[label] = false;
            }

            CheckMember("application.document", _app, MemberMap.ActiveDocument);

            var doc = Document();
            CheckMember("document.tools", doc, MemberMap.Tools);
            CheckMember("document.operations", doc, MemberMap.Operations);
            CheckMember("document.stock", doc, MemberMap.Stock);
            CheckMember("document.machineSetup", doc, MemberMap.MachineSetup);
            CheckMember("application.postProcessors", _app, MemberMap.PostProcessors);
            CheckMember("application.outputWindow", _app, MemberMap.OutputWindow);

            foreach (var name in _app.KnownMemberNames().Take(200))
                report.AvailableInterfaces.Add("application." + name);

            return report;
        }

        // ------------------------------------------------------------------ reads

        public EspritStockInfo GetStock()
        {
            var doc = Document();
            if (doc == null) return null;

            var stock = doc.GetAny(MemberMap.Stock);
            if (stock.IsNull) return null;

            return new EspritStockInfo
            {
                DiameterMm = stock.GetAny(MemberMap.StockDiameter).AsDouble(0),
                LengthMm = stock.GetAny(MemberMap.StockLength).AsDouble(0),
                MaterialName = stock.GetAny(MemberMap.StockMaterial).AsString("")
            };
        }

        public IReadOnlyList<EspritToolInfo> GetTools()
        {
            var result = new List<EspritToolInfo>();
            var doc = Document();
            if (doc == null) return result;

            var tools = doc.GetAny(MemberMap.Tools);
            if (tools.IsNull) return result;

            int index = 0;
            foreach (var t in tools.Enumerate())
            {
                index++;
                try
                {
                    result.Add(new EspritToolInfo
                    {
                        Id = t.GetAny(MemberMap.ItemId).AsString(index.ToString(CultureInfo.InvariantCulture)),
                        Name = t.GetAny(MemberMap.ItemName).AsString("tool " + index),
                        StationNumber = t.GetAny(MemberMap.ToolStation).AsInt(0),
                        DiameterMm = t.GetAny(MemberMap.ToolDiameter).AsDouble(0),
                        NoseRadiusMm = t.GetAny(MemberMap.ToolNoseRadius).AsDouble(0),
                        ToolTypeName = t.GetAny(MemberMap.ToolType).AsString(""),
                        SpeedRpm = t.GetAny(MemberMap.SpeedRpm).AsDouble(0),
                        FeedMmPerRev = t.GetAny(MemberMap.FeedPerRev).AsDouble(0)
                    });
                }
                catch (Exception ex)
                {
                    // One unreadable tool must not lose the other forty.
                    Diagnostic?.Invoke($"Skipped tool {index}: {ex.Message}");
                }
            }

            return result;
        }

        public IReadOnlyList<EspritOperationInfo> GetOperations()
        {
            var result = new List<EspritOperationInfo>();
            var doc = Document();
            if (doc == null) return result;

            var operations = doc.GetAny(MemberMap.Operations);
            if (operations.IsNull) return result;

            int index = 0;
            foreach (var op in operations.Enumerate())
            {
                index++;
                try
                {
                    var toolRef = op.GetAny(MemberMap.OperationTool);

                    result.Add(new EspritOperationInfo
                    {
                        Id = op.GetAny(MemberMap.ItemId).AsString(index.ToString(CultureInfo.InvariantCulture)),
                        Name = op.GetAny(MemberMap.ItemName).AsString("operation " + index),
                        TechnologyName = op.GetAny(MemberMap.OperationTechnology).AsString(""),
                        ToolId = toolRef.IsNull ? "" : toolRef.GetAny(MemberMap.ItemId).AsString(""),
                        Channel = op.GetAny(MemberMap.OperationChannel).AsInt(0),
                        SpeedRpm = op.GetAny(MemberMap.SpeedRpm).AsDouble(0),
                        FeedMmPerRev = op.GetAny(MemberMap.FeedPerRev).AsDouble(0),
                        DepthOfCutMm = op.GetAny(MemberMap.DepthOfCut).AsDouble(0),
                        EspritCycleSeconds = op.GetAny(MemberMap.OperationCycleTime).AsDouble(0)
                    });
                }
                catch (Exception ex)
                {
                    Diagnostic?.Invoke($"Skipped operation {index}: {ex.Message}");
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ writes

        public int ApplyCuttingData(IEnumerable<CuttingDataUpdate> updates)
        {
            var doc = Document();
            if (doc == null) return 0;

            var operations = doc.GetAny(MemberMap.Operations);
            if (operations.IsNull) return 0;

            var byId = new Dictionary<string, Com>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var op in operations.Enumerate())
            {
                index++;
                var id = op.GetAny(MemberMap.ItemId).AsString(index.ToString(CultureInfo.InvariantCulture));
                byId[id] = op;
            }

            int applied = 0;
            foreach (var update in updates ?? Enumerable.Empty<CuttingDataUpdate>())
            {
                if (!byId.TryGetValue(update.OperationId ?? "", out var op))
                {
                    Diagnostic?.Invoke($"No operation '{update.OperationId}' in the document; skipped.");
                    continue;
                }

                bool changed = false;

                if (update.SpeedRpm.HasValue)
                    changed |= op.SetAny(update.SpeedRpm.Value, MemberMap.SpeedRpm) != null;

                if (update.FeedMmPerRev.HasValue)
                    changed |= op.SetAny(update.FeedMmPerRev.Value, MemberMap.FeedPerRev) != null;

                if (update.DepthOfCutMm.HasValue)
                    changed |= op.SetAny(update.DepthOfCutMm.Value, MemberMap.DepthOfCut) != null;

                if (changed)
                {
                    applied++;
                    Diagnostic?.Invoke($"Updated '{update.OperationId}'" +
                                       (string.IsNullOrEmpty(update.Reason) ? "" : ": " + update.Reason));
                }
                else
                {
                    Diagnostic?.Invoke($"No writable cutting-data property found on '{update.OperationId}'.");
                }
            }

            return applied;
        }

        // ------------------------------------------------------------------ posting

        public IReadOnlyList<string> GetPostProcessors()
        {
            var result = new List<string>();
            if (!IsConnected) return result;

            var posts = _app.GetAny(MemberMap.PostProcessors);
            if (posts.IsNull)
            {
                var doc = Document();
                if (doc != null) posts = doc.GetAny(MemberMap.PostProcessors);
            }
            if (posts.IsNull) return result;

            foreach (var p in posts.Enumerate())
            {
                var name = p.GetAny(MemberMap.ItemName).AsString("");
                if (string.IsNullOrEmpty(name)) name = p.AsString("");
                if (!string.IsNullOrEmpty(name)) result.Add(name);
            }

            return result;
        }

        public string PostProcess(string postProcessorName)
        {
            var doc = Document();
            if (doc == null) return null;

            foreach (var method in MemberMap.RunPost)
            {
                var outcome = string.IsNullOrEmpty(postProcessorName)
                    ? doc.TryCall(method)
                    : doc.TryCall(method, postProcessorName);

                if (outcome.IsNull) continue;

                // Some builds return the NC text directly; others return a result object
                // that carries it on a property.
                var direct = outcome.AsString("");
                if (direct.Length > 0 && direct.IndexOf('\n') >= 0) return direct;

                var carried = outcome.GetAny(MemberMap.NcCode).AsString("");
                if (carried.Length > 0) return carried;
            }

            Diagnostic?.Invoke(
                "Could not run a post processor. None of " + string.Join(", ", MemberMap.RunPost) +
                " resolved on the document. Run the SwissForge probe and correct MemberMap.RunPost.");
            return null;
        }

        // ------------------------------------------------------------------ logging

        public void Log(string message)
        {
            Diagnostic?.Invoke(message);
            if (!IsConnected) return;

            try
            {
                var window = _app.GetAny(MemberMap.OutputWindow);
                if (window.IsNull) return;

                foreach (var method in MemberMap.WriteLine)
                {
                    var r = window.TryCall(method, "[SwissForge] " + message);
                    if (!r.IsNull) return;
                }
            }
            catch
            {
                // Logging must never be the thing that breaks the add-in.
            }
        }

        public void Dispose()
        {
            try { _app?.Release(); } catch { /* shutting down */ }
            _app = null;
        }
    }
}
