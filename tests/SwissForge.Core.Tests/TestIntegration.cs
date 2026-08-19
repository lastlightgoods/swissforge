using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwissForge.Core.Api;
using SwissForge.Core.Integration;
using SwissForge.Core.Json;

namespace SwissForge.Tests
{
    public static class TestIntegration
    {
        public static void Run()
        {
            Check.Suite("Outbox durability");

            var dir = Path.Combine(Path.GetTempPath(), "swissforge-test-" + Guid.NewGuid().ToString("N"));
            var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

            try
            {
                var outbox = new Outbox(dir) { MaxAttempts = 3, BackoffBaseSeconds = 10 };

                var id = outbox.Enqueue("quote.created", JsonValue.Obj().Set("partNumber", "SF-1"), now);
                Check.True(id.Length > 0, "enqueue returns an id");
                Check.Equal(1, outbox.PendingCount, "the event is on disk");

                var due = outbox.Due(now);
                Check.Equal(1, due.Count, "a new event is immediately due");
                Check.Equal("quote.created", due[0].Type, "the event type survives the round trip");
                Check.Equal("SF-1", due[0].Payload["partNumber"].AsString(), "the payload survives the round trip");

                // --- failure schedules a backoff rather than losing the event
                outbox.Fail(id, "connection refused", now);
                Check.Equal(1, outbox.PendingCount, "a failed event stays queued");
                Check.Equal(0, outbox.Due(now).Count, "it is not due again immediately");
                Check.Equal(1, outbox.Due(now.AddSeconds(11)).Count, "it becomes due after the backoff");

                var retried = outbox.Due(now.AddSeconds(11))[0];
                Check.Equal(1, retried.Attempts, "the attempt count is recorded");
                Check.Contains(retried.LastError, "connection refused", "the failure reason is recorded");

                // --- backoff grows
                outbox.Fail(id, "still down", now.AddSeconds(11));
                Check.Equal(0, outbox.Due(now.AddSeconds(20)).Count, "the second backoff is longer than the first");
                Check.Equal(1, outbox.Due(now.AddSeconds(60)).Count, "and it does become due eventually");

                // --- exhausting attempts parks the event instead of deleting it
                outbox.Fail(id, "gave up", now.AddSeconds(60));
                Check.Equal(0, outbox.PendingCount, "the event leaves the main queue");
                Check.Equal(1, outbox.DeadLetterCount, "and lands in dead-letter rather than vanishing");

                Check.True(outbox.Requeue(id, now.AddHours(1)), "a dead-lettered event can be requeued");
                Check.Equal(1, outbox.PendingCount, "the requeued event is pending again");
                Check.Equal(0, outbox.DeadLetterCount, "and is out of dead-letter");

                outbox.Complete(id);
                Check.Equal(0, outbox.PendingCount, "completing an event removes it");

                // --- a corrupt file does not stop the queue
                var good = outbox.Enqueue("job.planned", JsonValue.Obj().Set("x", 1), now);
                File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ this is not json");
                var stillDue = outbox.Due(now);
                Check.Equal(1, stillDue.Count, "a corrupt event file is skipped, not fatal");
                Check.Equal(good, stillDue[0].Id, "the good event still comes through");

                // --- signing
                var sig = PayloadSignature.Compute("{\"a\":1}", "shared-secret");
                Check.Equal(64, sig.Length, "the HMAC is 64 hex characters");
                Check.Equal(sig, PayloadSignature.Compute("{\"a\":1}", "shared-secret"), "signing is deterministic");
                Check.False(sig == PayloadSignature.Compute("{\"a\":2}", "shared-secret"),
                    "a different body gives a different signature");
                Check.False(sig == PayloadSignature.Compute("{\"a\":1}", "other-secret"),
                    "a different secret gives a different signature");
                Check.Equal("", PayloadSignature.Compute("{}", ""), "no secret means no signature header");

                // ------------------------------------------------------------------
                Check.Suite("ERP delivery");

                // This section drains against the real clock, so events must be enqueued
                // against it too - an event stamped with a fixed future time is simply not due.
                var realNow = DateTime.UtcNow;

                // Stand up a receiver using SwissForge's own HTTP server, so this is a real
                // socket round trip rather than a mocked transport.
                var received = new List<JsonValue>();
                var headersSeen = new List<string>();
                int failuresRemaining = 2;

                using (var receiver = new HttpServer())
                {
                    receiver.Handler = req =>
                    {
                        if (req.Path == "/flaky")
                        {
                            if (failuresRemaining-- > 0)
                                return HttpResponse.Error(503, "down", "try again");
                            received.Add(JsonValue.Parse(req.Body));
                            return HttpResponse.Json("{\"ok\":true}");
                        }

                        if (req.Path == "/reject")
                            return HttpResponse.Error(400, "bad", "malformed payload");

                        headersSeen.Add(req.Header("X-SwissForge-Signature") ?? "");
                        headersSeen.Add(req.Header("Idempotency-Key") ?? "");
                        received.Add(JsonValue.Parse(req.Body));
                        return HttpResponse.Json("{\"ok\":true}");
                    };
                    receiver.Start(0);

                    var outbox2 = new Outbox(Path.Combine(dir, "erp")) { MaxAttempts = 5, BackoffBaseSeconds = 0.001 };
                    var publisher = new ErpPublisher(outbox2);
                    publisher.Endpoints.Add(new ErpEndpoint
                    {
                        Name = "erp",
                        Url = $"http://127.0.0.1:{receiver.Port}/jobs",
                        SigningSecret = "shhh",
                        TimeoutSeconds = 5
                    });

                    publisher.Publish("quote.created",
                        JsonValue.Obj().Set("partNumber", "SF-2050").Set("price", 3.42), realNow);

                    string lastFailure = null;
                    publisher.AttemptCompleted = (e, ep, r) =>
                    {
                        if (!r.Success) lastFailure = $"{ep.Name}: status={r.StatusCode} error={r.Error}";
                    };

                    int delivered = publisher.Drain(DateTime.UtcNow);
                    Check.Equal(1, delivered, "the event is delivered over a real socket" +
                        (lastFailure != null ? " -- failure was: " + lastFailure : ""));
                    if (received.Count == 0)
                    {
                        Check.True(false, "no event reached the receiver; remaining ERP checks skipped");
                        return;
                    }
                    Check.Equal(0, outbox2.PendingCount, "a delivered event leaves the queue");
                    Check.Equal(1, received.Count, "the receiver got exactly one event");
                    Check.Equal("quote.created", received[0]["eventType"].AsString(), "the envelope carries the type");
                    Check.Equal("SF-2050", received[0]["data"]["partNumber"].AsString(), "the payload arrives intact");
                    Check.Equal("SwissForge", received[0]["source"].AsString(), "the envelope identifies the source");
                    Check.True(headersSeen.Any(h => h.StartsWith("sha256=")), "the payload is signed");
                    Check.True(headersSeen.Any(h => h.Length > 20 && !h.StartsWith("sha256=")),
                        "an idempotency key is sent so the receiver can drop duplicates");

                    // --- a 503 is retried
                    received.Clear();
                    var flaky = new ErpPublisher(outbox2);
                    flaky.Endpoints.Add(new ErpEndpoint
                    {
                        Name = "flaky", Url = $"http://127.0.0.1:{receiver.Port}/flaky", TimeoutSeconds = 5
                    });
                    flaky.Publish("job.planned", JsonValue.Obj().Set("n", 1), realNow);

                    Check.Equal(0, flaky.Drain(DateTime.UtcNow), "the first attempt fails on a 503");
                    Check.Equal(1, outbox2.PendingCount, "the event is retained for retry");
                    System.Threading.Thread.Sleep(20);
                    flaky.Drain(DateTime.UtcNow);
                    System.Threading.Thread.Sleep(20);
                    Check.Equal(1, flaky.Drain(DateTime.UtcNow), "it succeeds once the far end recovers");
                    Check.Equal(1, received.Count, "the receiver eventually got it, exactly once");

                    // --- a 400 is not retried forever
                    var rejecting = new ErpPublisher(outbox2);
                    rejecting.Endpoints.Add(new ErpEndpoint
                    {
                        Name = "reject", Url = $"http://127.0.0.1:{receiver.Port}/reject", TimeoutSeconds = 5
                    });
                    rejecting.Publish("job.planned", JsonValue.Obj().Set("n", 2), realNow);
                    rejecting.Drain(DateTime.UtcNow);
                    Check.Equal(0, outbox2.PendingCount, "a payload the far end rejects stops being retried");
                    Check.Greater(outbox2.DeadLetterCount, 0, "it is parked in dead-letter for a human to look at");
                }

                // --- an unreachable endpoint is retryable, not fatal
                var offlineOutbox = new Outbox(Path.Combine(dir, "offline"));
                var offline = new ErpPublisher(offlineOutbox);
                offline.Endpoints.Add(new ErpEndpoint
                {
                    Name = "gone", Url = "http://127.0.0.1:9/nowhere", TimeoutSeconds = 2
                });
                offline.Publish("quote.created", JsonValue.Obj(), DateTime.UtcNow);
                Check.Equal(0, offline.Drain(DateTime.UtcNow), "an unreachable endpoint delivers nothing");
                Check.Equal(1, offlineOutbox.PendingCount, "but the event is still safely on disk");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
            }
        }
    }
}
