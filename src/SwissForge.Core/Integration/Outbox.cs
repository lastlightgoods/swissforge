using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SwissForge.Core.Json;

namespace SwissForge.Core.Integration
{
    /// <summary>One thing that happened and needs to reach an external system.</summary>
    public sealed class OutboxEvent
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public JsonValue Payload { get; set; } = JsonValue.Obj();
        public int Attempts { get; set; }
        public string LastError { get; set; } = "";
        public string NextAttemptUtc { get; set; } = "";

        public JsonValue ToJson()
        {
            var j = JsonValue.Obj();
            j.Set("id", Id);
            j.Set("type", Type);
            j.Set("createdUtc", CreatedUtc);
            j["payload"] = Payload ?? JsonValue.Obj();
            j.Set("attempts", Attempts);
            j.Set("lastError", LastError);
            j.Set("nextAttemptUtc", NextAttemptUtc);
            return j;
        }

        public static OutboxEvent FromJson(JsonValue j) => new OutboxEvent
        {
            Id = j["id"].AsString(""),
            Type = j["type"].AsString(""),
            CreatedUtc = j["createdUtc"].AsString(""),
            Payload = j["payload"],
            Attempts = j["attempts"].AsInt(0),
            LastError = j["lastError"].AsString(""),
            NextAttemptUtc = j["nextAttemptUtc"].AsString("")
        };
    }

    /// <summary>
    /// A durable, file-backed outbox for outbound integration events.
    /// <para>
    /// Shop-floor PCs lose their network connection. They get rebooted mid-shift. The
    /// ERP goes down for maintenance on a Sunday. If a quote or a posted program is pushed
    /// with a straight HTTP call and that call fails, the event is simply gone and nobody
    /// finds out until someone notices the ERP is missing a job.
    /// </para>
    /// <para>
    /// Writing the event to disk first and draining the queue separately turns "the network
    /// was down" from data loss into a delay. Every event is written to its own file with an
    /// atomic rename, so a power cut mid-write leaves a complete previous state rather than
    /// a truncated record.
    /// </para>
    /// </summary>
    public sealed class Outbox
    {
        private readonly string _directory;
        private readonly object _lock = new object();

        /// <summary>Attempts before an event is parked in the dead-letter folder.</summary>
        public int MaxAttempts { get; set; } = 8;

        /// <summary>Base delay for exponential backoff, seconds.</summary>
        public double BackoffBaseSeconds { get; set; } = 5;

        /// <summary>Longest backoff between attempts, seconds.</summary>
        public double BackoffCapSeconds { get; set; } = 3600;

        public Outbox(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(DeadLetterDirectory);
        }

        public string DeadLetterDirectory => Path.Combine(_directory, "dead-letter");

        public int PendingCount => Directory.GetFiles(_directory, "*.json").Length;
        public int DeadLetterCount => Directory.GetFiles(DeadLetterDirectory, "*.json").Length;

        /// <summary>Queues an event. Returns its id.</summary>
        public string Enqueue(string type, JsonValue payload, DateTime utcNow)
        {
            var ev = new OutboxEvent
            {
                Id = NewId(type, utcNow),
                Type = type,
                CreatedUtc = utcNow.ToString("o", CultureInfo.InvariantCulture),
                Payload = payload ?? JsonValue.Obj(),
                NextAttemptUtc = utcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            lock (_lock) Write(ev);
            return ev.Id;
        }

        /// <summary>Events whose next attempt is due.</summary>
        public List<OutboxEvent> Due(DateTime utcNow)
        {
            lock (_lock)
            {
                var due = new List<OutboxEvent>();
                foreach (var file in Directory.GetFiles(_directory, "*.json").OrderBy(f => f))
                {
                    var ev = TryRead(file);
                    if (ev == null) continue;

                    if (DateTime.TryParse(ev.NextAttemptUtc, CultureInfo.InvariantCulture,
                                          DateTimeStyles.RoundtripKind, out var next) && next > utcNow)
                        continue;

                    due.Add(ev);
                }
                return due;
            }
        }

        /// <summary>Marks an event delivered and removes it.</summary>
        public void Complete(string id)
        {
            lock (_lock)
            {
                var path = PathFor(id);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// Records a failed attempt and schedules a retry with exponential backoff.
        /// Past <see cref="MaxAttempts"/> the event is moved to the dead-letter folder,
        /// where it stays visible rather than being deleted.
        /// </summary>
        public void Fail(string id, string error, DateTime utcNow)
        {
            lock (_lock)
            {
                var path = PathFor(id);
                var ev = TryRead(path);
                if (ev == null) return;

                ev.Attempts++;
                ev.LastError = error ?? "";

                if (ev.Attempts >= MaxAttempts)
                {
                    var dead = Path.Combine(DeadLetterDirectory, Path.GetFileName(path));
                    WriteAtomic(dead, ev.ToJson().ToJson(indent: true));
                    File.Delete(path);
                    return;
                }

                double delay = Math.Min(BackoffCapSeconds, BackoffBaseSeconds * Math.Pow(2, ev.Attempts - 1));
                ev.NextAttemptUtc = utcNow.AddSeconds(delay).ToString("o", CultureInfo.InvariantCulture);
                Write(ev);
            }
        }

        /// <summary>Moves a dead-lettered event back into the queue for another try.</summary>
        public bool Requeue(string id, DateTime utcNow)
        {
            lock (_lock)
            {
                var dead = Path.Combine(DeadLetterDirectory, id + ".json");
                if (!File.Exists(dead)) return false;

                var ev = TryRead(dead);
                if (ev == null) return false;

                ev.Attempts = 0;
                ev.NextAttemptUtc = utcNow.ToString("o", CultureInfo.InvariantCulture);
                Write(ev);
                File.Delete(dead);
                return true;
            }
        }

        // ------------------------------------------------------------------ storage

        private string PathFor(string id) => Path.Combine(_directory, id + ".json");

        private void Write(OutboxEvent ev) => WriteAtomic(PathFor(ev.Id), ev.ToJson().ToJson(indent: true));

        /// <summary>Write to a temp file then rename, so a crash never leaves a half-written event.</summary>
        private static void WriteAtomic(string path, string content)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static OutboxEvent TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return OutboxEvent.FromJson(JsonValue.Parse(File.ReadAllText(path)));
            }
            catch
            {
                // A corrupt event file must not stop the whole queue draining.
                return null;
            }
        }

        private static string NewId(string type, DateTime utcNow)
        {
            var safeType = new string((type ?? "event").Where(char.IsLetterOrDigit).ToArray());
            if (safeType.Length == 0) safeType = "event";

            // Timestamp first so the directory sorts chronologically, then a short random
            // suffix so two events in the same millisecond do not collide.
            var stamp = utcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            return $"{stamp}-{safeType}-{suffix}";
        }
    }

    /// <summary>Signs outbound payloads so the receiving system can verify they came from this shop.</summary>
    public static class PayloadSignature
    {
        /// <summary>Lowercase hex HMAC-SHA256 of the body, keyed with the shared secret.</summary>
        public static string Compute(string body, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
