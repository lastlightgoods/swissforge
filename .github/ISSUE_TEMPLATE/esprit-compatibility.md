---
name: ESPRIT compatibility report
about: The add-in cannot see something in your ESPRIT build
title: '[compat] '
labels: esprit-compatibility
---

## Which member is missing?

Paste the error, or the `MISSING` lines from the probe. They look like:

```
MISSING document.operations: none of [Operations, OperationList, Processes] resolved
```

## Your ESPRIT

- Version (Help > About):
- Edition (EDGE / TNG / classic 20xx):
- Windows version:

## Probe output

Run this with ESPRIT open, then attach the JSON:

```
swissforge-probe.exe --full --out swissforge-probe.json
```

**Before attaching:** the report includes your machine name and the names of tools,
operations and post processors in the open document. If any of that is sensitive, run it
against a scratch document, or delete the `tools` and `operations` sections. The
`typeLibrary` section is the part that matters and contains nothing about your parts.

## The real member name, if you found it

From the `typeLibrary` section of the report. Something like:

```json
{ "name": "MachiningOperations", "kind": "get", "returns": "IDispatch" }
```

The fix is one line in `EspritGateway.MemberMap` — the real name goes first, the existing
candidates stay as fallbacks so other seats keep working.
