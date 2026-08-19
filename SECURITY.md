# Security

## Reporting

Please report vulnerabilities privately through GitHub's **Report a vulnerability** button on
the Security tab, rather than opening a public issue.

## What SwissForge exposes

The add-in runs an HTTP server inside ESPRIT's process. Worth knowing:

- **It binds to 127.0.0.1 only** unless you explicitly set `apiAllowRemote: true`. Turning that
  on exposes the API to your network, and the add-in logs a warning when you do.
- **A bearer token is required** on every endpoint except `/health`. It is generated on first
  run and written to `%APPDATA%\SwissForge\swissforge.config.json`.
- **Token comparison is fixed-time**, so it does not leak one character at a time.
- **Request bodies are capped** at 8 MB by default.
- **The API can modify your CAM document.** `POST /api/v1/esprit/cutting-data` writes speeds and
  feeds into open operations. Anyone holding the token can change what your machine will cut.
  Treat the token accordingly.

## What it does not do

- It does not phone home. No telemetry, no analytics, no update checks.
- It has no package dependencies, so it has no transitive supply chain.
- The probe is read-only: it never modifies the document, saves, or posts.

## Outbound integration

If you configure ERP endpoints, payloads leave your network. Those requests carry:

- an **HMAC-SHA256 signature** (`X-SwissForge-Signature`) when you set a signing secret, so the
  receiver can verify origin;
- an **idempotency key**, so the receiver can drop duplicates — a timeout after the far end
  committed looks identical to a failure;
- whatever bearer token you configured for that endpoint.

Queued events are stored **unencrypted** on disk in the outbox directory until delivered. If
your payloads contain customer part numbers or pricing, that directory deserves the same
protection as the rest of your job data.
