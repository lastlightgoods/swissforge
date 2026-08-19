using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SwissForge.AddIn.Interop;
using SwissForge.AddIn.UI;
using SwissForge.Core.Api;
using SwissForge.Core.Feeds;
using SwissForge.Core.Integration;
using SwissForge.Core.Json;
using SwissForge.Core.Model;
using SwissForge.Core.Templates;
using SwissForge.Esprit;

namespace SwissForge.AddIn
{
    /// <summary>
    /// The class ESPRIT instantiates.
    /// <para>
    /// The single most important rule in this file: <b>nothing here may throw</b>. An
    /// unhandled exception inside a COM callback propagates into the host, and taking down a
    /// CAM system — along with a programmer's unsaved work — is not a forgivable way for a
    /// convenience add-in to fail. Every entry point is wrapped, and failures are logged and
    /// swallowed rather than surfaced as a crash.
    /// </para>
    /// <para>
    /// The second rule: startup is cheap and lazy. ESPRIT is waiting on
    /// <see cref="OnConnection"/>, so the work done there is limited to attaching and reading
    /// a config file. The API server starts on a background thread and everything else waits
    /// until the user asks for it.
    /// </para>
    /// </summary>
    [ComVisible(true)]
    [Guid("6F3B9A21-2C4E-4E7A-9E5B-1D0C7A5F4B10")]
    [ProgId("SwissForge.AddIn")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class SwissForgeAddIn : IDTExtensibility2
    {
        public const string Version = "1.0.0";

        private EspritGateway _gateway;
        private HttpServer _server;
        private ApiContext _context;
        private AddInConfig _config;
        private Outbox _outbox;
        private ErpPublisher _publisher;
        private Timer _outboxTimer;
        private StatusForm _status;

        private readonly List<string> _log = new List<string>();
        private readonly object _logLock = new object();

        /// <summary>The running instance, so UI code and the API can reach the host.</summary>
        public static SwissForgeAddIn Current { get; private set; }

        public IReadOnlyList<string> LogLines
        {
            get { lock (_logLock) return _log.ToArray(); }
        }

        public string ApiUrl => _server != null && _server.IsRunning ? _server.BaseUrl : "(not running)";
        public string ApiToken => _config?.ApiToken ?? "";
        public AddInConfig Config => _config;
        public EspritGateway Gateway => _gateway;
        public ApiContext Context => _context;

        public event Action LogChanged;

        // ================================================================== IDTExtensibility2

        public void OnConnection(object Application, ext_ConnectMode ConnectMode,
                                 object AddInInst, ref Array custom)
        {
            try
            {
                Current = this;
                Log($"SwissForge {Version} connecting ({ConnectMode}).");

                var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                _config = AddInConfig.Load(assemblyDirectory, Log);

                _gateway = new EspritGateway { Diagnostic = Log };
                _gateway.AttachTo(Application);

                var connection = _gateway.Connect();
                Log(connection.Connected
                    ? $"Attached to {connection.ProductName} {connection.Version}."
                    : "Could not identify the host: " + connection.Message);

                _context = BuildContext();

                if (_config.ApiEnabled) StartApi();
                StartOutbox();

                if (_config.ShowStatusOnStartup) ShowStatus();

                _gateway.Log($"SwissForge {Version} ready. API on {ApiUrl}");
            }
            catch (Exception ex)
            {
                // Log it and carry on. ESPRIT keeps running with the add-in inert, which is
                // far better than ESPRIT not starting.
                Log("OnConnection failed: " + ex);
            }
        }

        public void OnDisconnection(ext_DisconnectMode RemoveMode, ref Array custom)
        {
            try
            {
                Log($"Disconnecting ({RemoveMode}).");

                _outboxTimer?.Dispose();
                _outboxTimer = null;

                _server?.Stop();
                _server?.Dispose();
                _server = null;

                if (_status != null && !_status.IsDisposed)
                {
                    try { _status.Close(); } catch { /* closing */ }
                    _status = null;
                }

                _gateway?.Dispose();
                _gateway = null;

                Current = null;
            }
            catch (Exception ex)
            {
                Log("OnDisconnection failed: " + ex.Message);
            }
        }

        public void OnAddInsUpdate(ref Array custom) { }

        public void OnStartupComplete(ref Array custom)
        {
            try { Log("Host startup complete."); }
            catch { /* never throw from a COM callback */ }
        }

        public void OnBeginShutdown(ref Array custom)
        {
            try
            {
                // Give queued integration events one last chance to leave before the host dies.
                if (_publisher != null)
                {
                    int sent = _publisher.Drain(DateTime.UtcNow);
                    if (sent > 0) Log($"Flushed {sent} pending integration event(s) on shutdown.");
                }
            }
            catch { /* never throw from a COM callback */ }
        }

        // ================================================================== setup

        private ApiContext BuildContext()
        {
            var context = new ApiContext { Version = Version, Esprit = _gateway };

            try
            {
                if (!string.IsNullOrEmpty(_config.MaterialsPath) && File.Exists(_config.MaterialsPath))
                {
                    context.Materials.ApplyOverrides(JsonValue.Parse(File.ReadAllText(_config.MaterialsPath)));
                    Log("Material overrides loaded from " + _config.MaterialsPath);
                }
            }
            catch (Exception ex) { Log("Could not load materials: " + ex.Message); }

            try
            {
                if (!string.IsNullOrEmpty(_config.ToolsPath) && File.Exists(_config.ToolsPath))
                {
                    context.Tools = ToolLibrary.FromJson(JsonValue.Parse(File.ReadAllText(_config.ToolsPath)));
                    Log($"Tool library loaded from {_config.ToolsPath} ({context.Tools.Count} tools).");
                }
            }
            catch (Exception ex) { Log("Could not load tools: " + ex.Message); }

            try
            {
                if (!string.IsNullOrEmpty(_config.MachinePath) && File.Exists(_config.MachinePath))
                {
                    context.Machine = MachineProfile.FromJson(JsonValue.Parse(File.ReadAllText(_config.MachinePath)));
                    Log("Machine profile loaded: " + context.Machine.Name);
                }
            }
            catch (Exception ex) { Log("Could not load the machine profile: " + ex.Message); }

            return context;
        }

        private void StartApi()
        {
            try
            {
                if (string.IsNullOrEmpty(_config.ApiToken))
                {
                    _config.ApiToken = Guid.NewGuid().ToString("N");
                    try
                    {
                        _config.Save(AddInConfig.DefaultUserPath);
                        Log("Generated an API token and saved it to " + AddInConfig.DefaultUserPath);
                    }
                    catch (Exception ex)
                    {
                        Log("Generated an API token but could not save it: " + ex.Message +
                            " It will change next time ESPRIT starts.");
                    }
                }

                var api = new SwissForgeApi(_context);
                _server = new HttpServer
                {
                    AuthToken = _config.ApiToken,
                    Handler = api.Handle,
                    BindAddress = _config.ApiAllowRemote
                        ? System.Net.IPAddress.Any
                        : System.Net.IPAddress.Loopback,
                    Fault = ex => Log("API fault: " + ex.Message)
                };

                _server.Start(_config.ApiPort);
                Log($"API listening on {_server.BaseUrl}");

                if (_config.ApiAllowRemote)
                    Log("WARNING: the API is bound to all interfaces, not just loopback. " +
                        "Anyone who can reach this machine and holds the token can drive it.");
            }
            catch (Exception ex)
            {
                Log($"Could not start the API on port {_config.ApiPort}: {ex.Message} " +
                    "Another process may already hold that port. Change apiPort in the config.");
                _server = null;
            }
        }

        private void StartOutbox()
        {
            try
            {
                if (_config.Endpoints.Count == 0) return;

                var directory = string.IsNullOrEmpty(_config.OutboxDirectory)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "SwissForge", "outbox")
                    : _config.OutboxDirectory;

                _outbox = new Outbox(directory);
                _publisher = new ErpPublisher(_outbox);
                _publisher.Endpoints.AddRange(_config.Endpoints);
                _publisher.AttemptCompleted = (ev, endpoint, result) =>
                {
                    if (!result.Success)
                        Log($"Delivery to {endpoint.Name} failed ({result.StatusCode}): {result.Error}");
                };

                int interval = Math.Max(10, _config.OutboxIntervalSeconds) * 1000;
                _outboxTimer = new Timer(_ =>
                {
                    try { _publisher.Drain(DateTime.UtcNow); }
                    catch (Exception ex) { Log("Outbox drain failed: " + ex.Message); }
                }, null, interval, interval);

                Log($"Outbox at {directory}, draining every {_config.OutboxIntervalSeconds}s " +
                    $"to {_config.Endpoints.Count} endpoint(s). {_outbox.PendingCount} pending.");
            }
            catch (Exception ex)
            {
                Log("Could not start the outbox: " + ex.Message);
            }
        }

        // ================================================================== public actions

        /// <summary>Publishes an integration event, if the shop has configured any endpoints.</summary>
        public void PublishEvent(string type, JsonValue payload)
        {
            try { _publisher?.Publish(type, payload, DateTime.UtcNow); }
            catch (Exception ex) { Log("Could not queue an event: " + ex.Message); }
        }

        /// <summary>Opens the status window, or brings it forward if already open.</summary>
        public void ShowStatus()
        {
            try
            {
                if (_status == null || _status.IsDisposed)
                {
                    _status = new StatusForm(this);
                    _status.Show();
                }
                else
                {
                    _status.BringToFront();
                }
            }
            catch (Exception ex)
            {
                Log("Could not open the status window: " + ex.Message);
            }
        }

        public void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (_logLock)
            {
                _log.Add(line);
                // Keep the buffer bounded; an add-in that runs for a week should not grow forever.
                if (_log.Count > 2000) _log.RemoveRange(0, 500);
            }
            try { LogChanged?.Invoke(); } catch { /* a UI handler must not break logging */ }
        }
    }
}
