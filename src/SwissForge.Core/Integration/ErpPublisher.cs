using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using SwissForge.Core.Json;

namespace SwissForge.Core.Integration
{
    /// <summary>Where outbound events go.</summary>
    public sealed class ErpEndpoint
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";

        /// <summary>Sent as "Authorization: Bearer ..." when set.</summary>
        public string BearerToken { get; set; } = "";

        /// <summary>Shared secret for the X-SwissForge-Signature HMAC header.</summary>
        public string SigningSecret { get; set; } = "";

        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>Event types this endpoint wants. Empty means all of them.</summary>
        public List<string> EventTypes { get; set; } = new List<string>();

        public bool Accepts(string eventType) =>
            EventTypes == null || EventTypes.Count == 0 ||
            EventTypes.Exists(t => string.Equals(t, eventType, StringComparison.OrdinalIgnoreCase));

        public static ErpEndpoint FromJson(JsonValue j)
        {
            var e = new ErpEndpoint
            {
                Name = j["name"].AsString(""),
                Url = j["url"].AsString(""),
                BearerToken = j["bearerToken"].AsString(""),
                SigningSecret = j["signingSecret"].AsString(""),
                TimeoutSeconds = j["timeoutSeconds"].AsInt(30),
                EventTypes = new List<string>()
            };
            foreach (var t in j["eventTypes"]) e.EventTypes.Add(t.AsString(""));
            return e;
        }
    }

    /// <summary>The outcome of one delivery attempt.</summary>
    public sealed class DeliveryResult
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string Error { get; set; } = "";
        public string ResponseBody { get; set; } = "";

        /// <summary>
        /// True when retrying could plausibly work: a timeout, a connection failure, or a
        /// 5xx. A 400 means the payload is wrong and retrying it forever is pointless noise.
        /// </summary>
        public bool IsRetryable { get; set; }
    }

    /// <summary>
    /// Delivers outbox events to an external system over HTTP.
    /// <para>
    /// Built on <see cref="HttpWebRequest"/> rather than HttpClient so the same code runs on
    /// the .NET Framework 4.8 that hosts the ESPRIT add-in and on modern .NET, with no
    /// package reference either way.
    /// </para>
    /// </summary>
    public sealed class ErpPublisher
    {
        private readonly Outbox _outbox;

        public List<ErpEndpoint> Endpoints { get; } = new List<ErpEndpoint>();

        /// <summary>Called for every attempt. Useful for surfacing progress in the UI.</summary>
        public Action<OutboxEvent, ErpEndpoint, DeliveryResult> AttemptCompleted { get; set; }

        public ErpPublisher(Outbox outbox)
        {
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        }

        /// <summary>Queues an event for every endpoint that wants it.</summary>
        public string Publish(string eventType, JsonValue payload, DateTime utcNow) =>
            _outbox.Enqueue(eventType, payload, utcNow);

        /// <summary>
        /// Attempts delivery of everything currently due. Returns how many succeeded.
        /// Safe to call on a timer; it never throws for a network problem.
        /// </summary>
        public int Drain(DateTime utcNow)
        {
            int delivered = 0;

            foreach (var ev in _outbox.Due(utcNow))
            {
                bool allOk = true;
                string lastError = "";

                foreach (var endpoint in Endpoints)
                {
                    if (!endpoint.Accepts(ev.Type)) continue;

                    var result = Send(endpoint, ev);
                    AttemptCompleted?.Invoke(ev, endpoint, result);

                    if (result.Success) continue;

                    allOk = false;
                    lastError = $"{endpoint.Name}: {result.Error}";

                    if (!result.IsRetryable)
                    {
                        // Burn the remaining attempts immediately rather than retrying a
                        // payload the far end has already rejected on its merits.
                        for (int i = ev.Attempts; i < _outbox.MaxAttempts; i++)
                            _outbox.Fail(ev.Id, lastError + " (not retryable)", utcNow);
                        break;
                    }
                }

                if (allOk) { _outbox.Complete(ev.Id); delivered++; }
                else _outbox.Fail(ev.Id, lastError, utcNow);
            }

            return delivered;
        }

        private static DeliveryResult Send(ErpEndpoint endpoint, OutboxEvent ev)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Url))
                return new DeliveryResult { Success = false, Error = "Endpoint has no URL.", IsRetryable = false };

            var envelope = JsonValue.Obj();
            envelope.Set("eventId", ev.Id);
            envelope.Set("eventType", ev.Type);
            envelope.Set("createdUtc", ev.CreatedUtc);
            envelope.Set("attempt", ev.Attempts + 1);
            envelope.Set("source", "SwissForge");
            envelope["data"] = ev.Payload ?? JsonValue.Obj();

            var body = envelope.ToJson();
            var bytes = Encoding.UTF8.GetBytes(body);

            try
            {
                // HttpWebRequest is obsolete on modern .NET in favour of HttpClient, but it is
                // the only HTTP client available on .NET Framework 4.8 without a package
                // reference, and this assembly must load cleanly inside ESPRIT's process.
                // The warning is suppressed deliberately rather than by accident.
#pragma warning disable SYSLIB0014
                var request = (HttpWebRequest)WebRequest.Create(endpoint.Url);
#pragma warning restore SYSLIB0014
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.ContentLength = bytes.Length;
                request.Timeout = Math.Max(1, endpoint.TimeoutSeconds) * 1000;
                request.ReadWriteTimeout = request.Timeout;
                request.UserAgent = "SwissForge";

                // These payloads are small. The 100-continue handshake costs a round trip and
                // buys nothing here, and not every receiver implements it correctly.
                request.ServicePoint.Expect100Continue = false;

                if (!string.IsNullOrEmpty(endpoint.BearerToken))
                    request.Headers["Authorization"] = "Bearer " + endpoint.BearerToken;

                request.Headers["X-SwissForge-Event"] = ev.Type;
                request.Headers["X-SwissForge-Event-Id"] = ev.Id;

                // An idempotency key lets the receiver drop duplicates safely, which matters
                // because a timeout after the far end committed looks identical to a failure.
                request.Headers["Idempotency-Key"] = ev.Id;

                if (!string.IsNullOrEmpty(endpoint.SigningSecret))
                    request.Headers["X-SwissForge-Signature"] =
                        "sha256=" + PayloadSignature.Compute(body, endpoint.SigningSecret);

                using (var stream = request.GetRequestStream())
                    stream.Write(bytes, 0, bytes.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null))
                {
                    int status = (int)response.StatusCode;
                    return new DeliveryResult
                    {
                        Success = status >= 200 && status < 300,
                        StatusCode = status,
                        ResponseBody = Truncate(reader.ReadToEnd(), 2000),
                        IsRetryable = status >= 500
                    };
                }
            }
            catch (WebException wex)
            {
                var http = wex.Response as HttpWebResponse;
                int status = http != null ? (int)http.StatusCode : 0;
                string responseBody = "";

                if (http != null)
                {
                    try
                    {
                        using (var reader = new StreamReader(http.GetResponseStream() ?? Stream.Null))
                            responseBody = Truncate(reader.ReadToEnd(), 2000);
                    }
                    catch { /* the body is a nicety, not worth failing over */ }
                }

                return new DeliveryResult
                {
                    Success = false,
                    StatusCode = status,
                    Error = status > 0 ? $"HTTP {status}: {wex.Message}" : wex.Message,
                    ResponseBody = responseBody,
                    // No response at all means a transport problem: retry. 5xx: retry.
                    // 408 and 429 are explicitly "try again". Everything else in 4xx is our fault.
                    IsRetryable = status == 0 || status >= 500 || status == 408 || status == 429
                };
            }
            catch (Exception ex)
            {
                return new DeliveryResult { Success = false, Error = ex.Message, IsRetryable = true };
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
