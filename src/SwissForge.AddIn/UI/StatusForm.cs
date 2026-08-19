using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SwissForge.Core.Esprit;
using SwissForge.Core.Feeds;
using SwissForge.Core.GCode;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.AddIn.UI
{
    /// <summary>
    /// The add-in's one window: what SwissForge is doing, and the three actions worth a button.
    /// <para>
    /// Kept deliberately small. A CAM programmer does not want another application to learn —
    /// the useful surface is the API and the CLI, and this window mostly exists so someone can
    /// find the token, confirm the thing is alive, and read why it is not.
    /// </para>
    /// </summary>
    public sealed class StatusForm : Form
    {
        private readonly SwissForgeAddIn _addIn;
        private readonly TextBox _logBox;
        private readonly Label _apiLabel;
        private readonly Label _espritLabel;

        public StatusForm(SwissForgeAddIn addIn)
        {
            _addIn = addIn ?? throw new ArgumentNullException(nameof(addIn));

            Text = "SwissForge " + SwissForgeAddIn.Version;
            Width = 820;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(640, 400);

            var header = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(12) };

            _espritLabel = new Label { Dock = DockStyle.Top, Height = 24, AutoEllipsis = true };
            _apiLabel = new Label { Dock = DockStyle.Top, Height = 24, AutoEllipsis = true };
            var configLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                AutoEllipsis = true,
                Text = "Config: " + (addIn.Config?.LoadedFrom ?? "(defaults)")
            };

            header.Controls.Add(configLabel);
            header.Controls.Add(_apiLabel);
            header.Controls.Add(_espritLabel);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(8, 4, 8, 4)
            };

            buttons.Controls.Add(Button("Copy API token", CopyToken));
            buttons.Controls.Add(Button("Copy API URL", CopyUrl));
            buttons.Controls.Add(Button("Probe ESPRIT", Probe));
            buttons.Controls.Add(Button("Read document", ReadDocument));
            buttons.Controls.Add(Button("Post and lint", PostAndLint));
            buttons.Controls.Add(Button("Copy log", CopyLog));

            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                BackColor = Color.White
            };

            Controls.Add(_logBox);
            Controls.Add(buttons);
            Controls.Add(header);

            _addIn.LogChanged += OnLogChanged;
            FormClosed += (s, e) => _addIn.LogChanged -= OnLogChanged;

            RefreshAll();
        }

        private Button Button(string text, Action action)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28 };
            b.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception ex) { _addIn.Log("Action failed: " + ex.Message); }
            };
            return b;
        }

        private void OnLogChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)RefreshLog); } catch { } return; }
            RefreshLog();
        }

        private void RefreshAll()
        {
            var connection = _addIn.Gateway?.Connect() ?? new EspritConnectionInfo();
            _espritLabel.Text = connection.Connected
                ? $"ESPRIT: {connection.ProductName} {connection.Version}   " +
                  $"{(string.IsNullOrEmpty(connection.DocumentPath) ? "(no document open)" : connection.DocumentPath)}"
                : "ESPRIT: not attached — " + connection.Message;

            _apiLabel.Text = $"API: {_addIn.ApiUrl}    token {Mask(_addIn.ApiToken)}";
            RefreshLog();
        }

        private static string Mask(string token) =>
            string.IsNullOrEmpty(token) ? "(none)"
            : token.Length <= 8 ? "****"
            : token.Substring(0, 4) + new string('*', token.Length - 8) + token.Substring(token.Length - 4);

        private void RefreshLog()
        {
            _logBox.Lines = _addIn.LogLines.ToArray();
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        // ------------------------------------------------------------------ actions

        private void CopyToken()
        {
            if (string.IsNullOrEmpty(_addIn.ApiToken)) { _addIn.Log("No API token to copy."); return; }
            Clipboard.SetText(_addIn.ApiToken);
            _addIn.Log("API token copied to the clipboard.");
        }

        private void CopyUrl()
        {
            Clipboard.SetText(_addIn.ApiUrl);
            _addIn.Log("API URL copied to the clipboard.");
        }

        private void CopyLog() => Clipboard.SetText(string.Join(Environment.NewLine, _addIn.LogLines));

        private void Probe()
        {
            var report = _addIn.Gateway?.Probe();
            if (report == null) { _addIn.Log("No gateway to probe."); return; }

            _addIn.Log($"Probe: {report.AvailableInterfaces.Count(a => a.Contains("->"))} expected members " +
                       $"resolved, {report.MissingExpectedMembers.Count} missing.");

            foreach (var found in report.AvailableInterfaces.Where(a => a.Contains("->")))
                _addIn.Log("  ok      " + found);
            foreach (var missing in report.MissingExpectedMembers)
                _addIn.Log("  MISSING " + missing);

            if (report.MissingExpectedMembers.Count > 0)
                _addIn.Log("  Run swissforge-probe.exe for a full type-library dump, and correct " +
                           "EspritGateway.MemberMap from it.");

            RefreshAll();
        }

        private void ReadDocument()
        {
            var gateway = _addIn.Gateway;
            if (gateway == null || !gateway.IsConnected) { _addIn.Log("Not attached to ESPRIT."); return; }

            var stock = gateway.GetStock();
            _addIn.Log(stock == null
                ? "Stock: not readable."
                : $"Stock: {stock.DiameterMm:F2} mm x {stock.LengthMm:F0} mm, {stock.MaterialName}");

            var tools = gateway.GetTools();
            _addIn.Log($"Tools: {tools.Count}");
            foreach (var t in tools.Take(30))
                _addIn.Log($"  T{t.StationNumber,-3} {t.Name}  dia {t.DiameterMm:F2}  nose {t.NoseRadiusMm:F2}");

            var operations = gateway.GetOperations();
            _addIn.Log($"Operations: {operations.Count}");
            foreach (var o in operations.Take(40))
                _addIn.Log($"  ch{o.Channel} {o.Name}  {o.SpeedRpm:F0} rpm  {o.FeedMmPerRev:F4} mm/rev");

            if (operations.Count > 0)
            {
                double espritTotal = operations.Sum(o => o.EspritCycleSeconds);
                if (espritTotal > 0)
                    _addIn.Log($"ESPRIT reports {espritTotal:F1} s of operation time in total. " +
                               "Note that summing operations is not the multi-channel cycle time — " +
                               "use /api/v1/cycle-time for the scheduled figure.");
            }
        }

        private void PostAndLint()
        {
            var gateway = _addIn.Gateway;
            if (gateway == null || !gateway.IsConnected) { _addIn.Log("Not attached to ESPRIT."); return; }

            var posts = gateway.GetPostProcessors();
            _addIn.Log($"Post processors available: {posts.Count}");

            var postName = posts.FirstOrDefault() ?? "";
            var nc = gateway.PostProcess(postName);

            if (string.IsNullOrEmpty(nc))
            {
                _addIn.Log("No NC output came back. Check the probe output for the correct post method name.");
                return;
            }

            _addIn.Log($"Posted {nc.Split('\n').Length} lines with '{postName}'. Linting...");

            var machine = _addIn.Context?.Machine ?? new MachineProfile();
            var control = ControlProfile.For(machine.Dialect);
            var program = new GParser().Parse(nc, postName);
            program.SplitChannels(control);

            var report = new GCodeLinter { Machine = machine, Control = control }.Lint(program);

            _addIn.Log($"Lint: {report.Count(Severity.Critical)} critical, {report.Count(Severity.Error)} error, " +
                       $"{report.Count(Severity.Warning)} warning.");

            foreach (var f in report.Findings
                        .Where(x => x.Severity >= Severity.Warning)
                        .OrderByDescending(x => x.Severity)
                        .Take(40))
            {
                _addIn.Log($"  [{f.Severity}] line {f.LineNumber} ch{f.Channel}  {f.Code}: {f.Message}");
                if (!string.IsNullOrEmpty(f.Suggestion)) _addIn.Log("      " + f.Suggestion);
            }

            if (report.HasCritical)
                MessageBox.Show(this,
                    $"{report.Count(Severity.Critical)} critical finding(s) in the posted program.\r\n\r\n" +
                    "The log lists each one with its line number. These are the kind that stop the job, " +
                    "not style points.",
                    "SwissForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            _addIn.PublishEvent("program.posted", JsonValue.Obj()
                .Set("postProcessor", postName)
                .Set("lineCount", nc.Split('\n').Length)
                .Set("criticalFindings", report.Count(Severity.Critical))
                .Set("errorFindings", report.Count(Severity.Error)));
        }
    }
}
