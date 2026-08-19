using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SwissForge.Core.Integration;
using SwissForge.Core.Json;
using SwissForge.Core.Model;

namespace SwissForge.AddIn
{
    /// <summary>
    /// Add-in settings, read from disk so a shop can configure every seat identically
    /// without touching code.
    /// <para>
    /// Search order is deliberate: a per-user file wins over the machine-wide one, which
    /// wins over the defaults. That lets IT push a shared machine profile and rate card to
    /// every seat while a programmer still overrides the API port on their own machine.
    /// </para>
    /// </summary>
    public sealed class AddInConfig
    {
        public bool ApiEnabled { get; set; } = true;
        public int ApiPort { get; set; } = 8731;

        /// <summary>Bearer token. Generated and written back on first run when left empty.</summary>
        public string ApiToken { get; set; } = "";

        /// <summary>Bind beyond loopback. Off by default and should stay that way.</summary>
        public bool ApiAllowRemote { get; set; }

        /// <summary>Show the status window when ESPRIT starts.</summary>
        public bool ShowStatusOnStartup { get; set; }

        /// <summary>Path to a material override file.</summary>
        public string MaterialsPath { get; set; } = "";

        /// <summary>Path to a tool library file.</summary>
        public string ToolsPath { get; set; } = "";

        /// <summary>Path to a machine profile file.</summary>
        public string MachinePath { get; set; } = "";

        /// <summary>Directory for the integration outbox.</summary>
        public string OutboxDirectory { get; set; } = "";

        /// <summary>Where events are pushed.</summary>
        public List<ErpEndpoint> Endpoints { get; } = new List<ErpEndpoint>();

        /// <summary>Seconds between outbox drain attempts.</summary>
        public int OutboxIntervalSeconds { get; set; } = 60;

        /// <summary>The file this configuration was loaded from, for display.</summary>
        public string LoadedFrom { get; set; } = "(defaults)";

        public const string FileName = "swissforge.config.json";

        public static IEnumerable<string> SearchPaths(string assemblyDirectory)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwissForge", FileName);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SwissForge", FileName);
            if (!string.IsNullOrEmpty(assemblyDirectory))
                yield return Path.Combine(assemblyDirectory, FileName);
        }

        public static AddInConfig Load(string assemblyDirectory, Action<string> log)
        {
            foreach (var path in SearchPaths(assemblyDirectory))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var config = FromJson(JsonValue.Parse(File.ReadAllText(path)));
                    config.LoadedFrom = path;
                    log?.Invoke("Configuration loaded from " + path);
                    return config;
                }
                catch (Exception ex)
                {
                    // A broken config must not stop the add-in loading; fall through to defaults
                    // and say so loudly, because silently ignoring settings is worse.
                    log?.Invoke($"Ignoring {path}: {ex.Message}");
                }
            }

            log?.Invoke("No configuration file found; using defaults.");
            return new AddInConfig();
        }

        public static AddInConfig FromJson(JsonValue j)
        {
            var c = new AddInConfig
            {
                ApiEnabled = j["apiEnabled"].AsBool(true),
                ApiPort = j["apiPort"].AsInt(8731),
                ApiToken = j["apiToken"].AsString(""),
                ApiAllowRemote = j["apiAllowRemote"].AsBool(false),
                ShowStatusOnStartup = j["showStatusOnStartup"].AsBool(false),
                MaterialsPath = j["materialsPath"].AsString(""),
                ToolsPath = j["toolsPath"].AsString(""),
                MachinePath = j["machinePath"].AsString(""),
                OutboxDirectory = j["outboxDirectory"].AsString(""),
                OutboxIntervalSeconds = j["outboxIntervalSeconds"].AsInt(60)
            };

            foreach (var e in j["endpoints"]) c.Endpoints.Add(ErpEndpoint.FromJson(e));
            return c;
        }

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("apiEnabled", ApiEnabled);
            j.Set("apiPort", ApiPort);
            j.Set("apiToken", ApiToken);
            j.Set("apiAllowRemote", ApiAllowRemote);
            j.Set("showStatusOnStartup", ShowStatusOnStartup);
            j.Set("materialsPath", MaterialsPath);
            j.Set("toolsPath", ToolsPath);
            j.Set("machinePath", MachinePath);
            j.Set("outboxDirectory", OutboxDirectory);
            j.Set("outboxIntervalSeconds", OutboxIntervalSeconds);

            var endpoints = JsonValue.Arr();
            foreach (var e in Endpoints)
            {
                var ej = JsonValue.Obj();
                ej.Set("name", e.Name);
                ej.Set("url", e.Url);
                ej.Set("bearerToken", e.BearerToken);
                ej.Set("signingSecret", e.SigningSecret);
                ej.Set("timeoutSeconds", e.TimeoutSeconds);
                ej.Set("eventTypes", e.EventTypes ?? new List<string>());
                endpoints.Add(ej);
            }
            j["endpoints"] = endpoints;
            return j;
        }

        /// <summary>Writes the configuration back, creating the directory if needed.</summary>
        public void Save(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, ToJson().ToJson(indent: true), new UTF8Encoding(false));
        }

        /// <summary>The per-user path, which is where a generated token gets written.</summary>
        public static string DefaultUserPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwissForge", FileName);
    }
}
