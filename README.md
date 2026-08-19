# SwissForge

[![CI](https://github.com/lastlightgoods/swissforge/actions/workflows/ci.yml/badge.svg)](https://github.com/lastlightgoods/swissforge/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%20Framework%204.8-512BD4.svg)](https://dotnet.microsoft.com/)
[![Dependencies](https://img.shields.io/badge/dependencies-none-brightgreen.svg)](#architecture)

An ESPRIT EDGE plugin for Swiss-type screw machining — cutting data, multi-channel cycle
time, quoting, NC program auditing, and a local REST API that exposes all of it to the rest
of your shop.

---

## Read this first

**Two thirds of this is verified. One third is not, and I can tell you exactly which third.**

Hexagon's ESPRIT EDGE API reference blocks automated retrieval, and the type libraries only
exist on a licensed installation. So the code that talks to ESPRIT was written against the
conventional ESPRIT automation object model, and **the member names in it have not been
checked against your build.**

Rather than guess and hand you something that fails at runtime, the design contains that
uncertainty:

| Layer | Status |
|---|---|
| Engines — cutting data, cycle time, quoting, G-code analysis, integration | **324 automated checks passing.** No ESPRIT dependency. Runs on any OS today. |
| REST API | **Verified end-to-end over real sockets**, including auth, error handling, and malformed input. |
| CLI | **Working now.** No ESPRIT, no licence, no Windows required. |
| ESPRIT adapter, add-in, probe | **Compiles clean, unverified against a live ESPRIT.** Every COM call goes through one member map, and `swissforge-probe` dumps your real object model so that map can be corrected from fact. |

The intended first move is: build it, run the probe, send back the JSON. Correcting the
adapter after that is a single-file change.

---

## What it does

### Cutting data that explains itself

Feeds and speeds from material, tool substrate, and operation — then clamped by spindle
limits, spindle power, tool stickout, and the part's own overhang past the guide bushing.
Every clamp and derate comes back as an advisory saying what was reduced and why:

```
$ swissforge feeds --material SS316 --op TurnRough --dia 12 --stock 1.5 --length 20

  316 austenitic stainless   |   OD rough, CNMG 0.4R   |   TurnRough
------------------------------------------------------------------
  Spindle speed           3223 rpm
  Surface speed          122 m/min
  Feed               0.1640 mm/rev
  Depth of cut            0.750 mm
  Passes                     2
  Predicted finish        2.10 um Ra
  Spindle power           0.92 kW
```

A recommendation with no advisories is one the machine can run as printed.

### Cycle time that understands channels

Adding up operations gives the wrong answer on a Swiss machine. Two, three, or four channels
cut simultaneously, and the cycle is the critical path through the waitcode graph. SwissForge
schedules the operations against their waitcodes and reports what actually matters:

- the real cycle time, not the sum
- **how long each channel spent idle** — which is where the recoverable seconds live
- which channel is the bottleneck
- **deadlocked and orphaned waitcodes**, before the machine finds them

### Quoting that refuses to lie

Two things spreadsheet quoting usually gets wrong, and this does not:

- **The remnant is not free.** A 12 ft bar with a 300 mm unpushable tail yields fewer parts
  than the arithmetic suggests, and you paid for the whole bar. Material cost per part is bar
  cost ÷ parts actually produced.
- **Scrap costs finished parts, not raw stock.** A part scrapped at final inspection has
  consumed its material, its machine time, and its tooling.

And it will **refuse to produce a sendable quote** off a built-in placeholder metal price.
Load your real purchase cost, or pass `--allow-indicative` deliberately.

### An NC linter that catches the expensive things

```
$ swissforge lint main.nc --dialect CitizenCincom

  [STOP]  CSS_NO_CLAMP   line 10, channel 0
      Constant surface speed (G96) is active with no G50 spindle clamp.
      > G1 X-0.2
      As X approaches centre the commanded rpm approaches infinity. On a bar machine
      this is the single most expensive missing line in the program.
      Add G50 S<max> before the G96.

  [STOP]  UNPAIRED_WAITCODE   line 13, channel 0
      Waitcode 'L30' appears only in channel 0.
      A rendezvous needs at least two channels. Either the matching code in the partner
      channel was deleted, or the label is mistyped. On the machine this is the line the
      cycle hangs on.
```

Sixteen rules, covering unclamped G96, feed moves with no F, cutting with no spindle,
unpaired and count-mismatched waitcodes, arcs with no geometry, mixed G20/G21, tool changes
straight out of a cut, programs left in G91, and more. Each finding names a line and suggests
a fix. State defects are reported **once per channel**, not once per line — a linter that
repeats itself forty times gets switched off, and then it catches nothing.

Exit codes drop straight into a pre-post hook or CI.

### A REST API, because the value should not be trapped in a CAM dialog

The add-in hosts the engines on loopback inside ESPRIT's process. Your ERP can pull a cycle
time. A quoting spreadsheet can post a part and get a price. A tablet on the floor can lint a
program before it goes to the machine. A scripting agent can drive the lot.

```
GET  /api/v1/status            POST /api/v1/feeds-speeds
GET  /api/v1/materials         POST /api/v1/cycle-time
GET  /api/v1/tools             POST /api/v1/plan
GET  /api/v1/machine           POST /api/v1/quote
GET  /openapi.json             POST /api/v1/gcode/lint
                               POST /api/v1/gcode/analyze

GET  /api/v1/esprit/probe      POST /api/v1/esprit/cutting-data
GET  /api/v1/esprit/tools      POST /api/v1/esprit/post
GET  /api/v1/esprit/operations
GET  /api/v1/esprit/stock
```

Bearer token, loopback-only by default, full OpenAPI 3 spec served at `/openapi.json`. The
`/esprit/*` endpoints return a clear 503 when running outside ESPRIT; everything else works
anywhere. Full reference in [docs/API.md](docs/API.md).

### Integration that survives a bad network

Shop PCs lose their connection and get rebooted mid-shift. Events are written to disk **first**
and delivered on a timer with exponential backoff, HMAC signatures, and idempotency keys.
A network outage becomes a delay instead of data loss. Payloads the far end rejects on their
merits stop being retried and land in a dead-letter folder where a human can see them.

---

## Quick start

### Right now, with no ESPRIT and no Windows

```bash
./verify.sh                                  # 324 checks, ~0.5s

cd src/SwissForge.Cli
dotnet build
alias swissforge='dotnet bin/Debug/net8.0/swissforge.dll'

swissforge materials
swissforge sample > part.json
swissforge plan part.json
swissforge quote part.json --breaks 100,1000,25000
swissforge lint ../../samples/program-broken.nc --dialect CitizenCincom
swissforge serve --port 8731
```

Requires the .NET 8 SDK. Nothing else — **the entire solution has zero NuGet dependencies**,
by design (see below).

### On the ESPRIT machine

```powershell
.\build.ps1                                  # tests, then builds everything

# ESPRIT running with a document open:
.\dist\probe\swissforge-probe.exe --full     # -> swissforge-probe.json

# elevated:
cd .\dist\addin
.\Install-SwissForge.ps1
```

Then restart ESPRIT. **Send back `swissforge-probe.json`** — that is what turns the adapter
from careful guesswork into verified code.

Prerequisites, configuration, uninstalling, and what to do when the add-in does not load are
all in [Installation](#installation) below.

---

## Installation

The Quick start above is the short version; this is the complete one.

There are three ways to run SwissForge. The engine layer is identical in all of them — only
the surface differs.

| Surface | What it gives you | Requires |
| --- | --- | --- |
| **CLI** | `plan`, `quote`, `lint`, `materials` from a shell or a build script | .NET 8 SDK, any OS |
| **REST API** | The same engines over HTTP, for ERP, MES, and shop dashboards | .NET 8 SDK, any OS |
| **ESPRIT add-in** | Both of the above, plus the live document — it reads the part and the tooling out of ESPRIT itself | Windows, .NET Framework 4.8, a licensed ESPRIT seat |

Start with the CLI even when the add-in is the goal. It needs no ESPRIT and no Windows, so
it will tell you whether the engines agree with your shop's proven numbers before you spend
anything on registry keys.

### Prerequisites

- **[.NET SDK 8 or later](https://dotnet.microsoft.com/download).** That is the whole list.
  The solution has zero third-party dependencies by design, so there is nothing else to
  acquire and no dependency tree to audit. The only package that restores is Microsoft's
  `NETStandard.Library` targeting shim, and `nuget.config` constrains the feed to that one
  package — an accidental `PackageReference` fails the restore rather than quietly landing
  a dependency in your shop.
- **For the add-in only:** Windows, plus the .NET Framework 4.8 targeting pack and the 4.x
  developer tools. The installer needs `regasm.exe`, which ships with those. Visual Studio
  installs both, as do the standalone Build Tools. ESPRIT must already be installed and
  licensed.

### The CLI and the API — any OS

```bash
git clone https://github.com/lastlightgoods/swissforge.git
cd swissforge

./verify.sh                                  # 324 checks, ~0.5s. Do this first.
```

`verify.sh` builds and runs the engine tests with `SwissForgeVerifyOnly=true`, which drops
Core to `net8.0` alone. That is what lets it pass on a Linux CI box or a locked-down shop PC
with no .NET Framework reference assemblies. If it fails, stop — nothing downstream is worth
installing.

Then build the CLI:

```bash
dotnet publish src/SwissForge.Cli -c Release -o ~/.local/swissforge

# Either alias it:
alias swissforge='dotnet ~/.local/swissforge/swissforge.dll'

# ...or, for a machine with no .NET runtime installed, produce a single self-contained
# executable and copy that anywhere:
dotnet publish src/SwissForge.Cli -c Release -r win-x64 \
  -p:PublishSingleFile=true --self-contained true -o dist/cli-standalone
```

Confirm it works, and that it disagrees with nothing you already trust:

```bash
swissforge version
swissforge materials                          # the built-in cutting data
swissforge sample > part.json
swissforge plan part.json
swissforge quote part.json --breaks 100,1000,25000
swissforge lint samples/program-broken.nc --dialect CitizenCincom   # must exit non-zero
```

To run the REST API on its own, with no ESPRIT anywhere:

```bash
swissforge serve --port 8731
```

It binds to `127.0.0.1` only. See [docs/API.md](docs/API.md) for the endpoints and the
authentication model.

### The ESPRIT add-in — Windows

Run all of this on the machine that has ESPRIT.

**1. Build and stage.**

```powershell
.\build.ps1                                  # engine tests, then the full solution
```

`build.ps1` runs the engine tests before it builds anything, and refuses to continue if they
fail. It stages to `.\dist`:

```
dist\addin      SwissForge.AddIn.dll + the install scripts
dist\probe      swissforge-probe.exe
dist\cli        swissforge.dll
dist\samples    the sample parts, tools, and NC programs
```

**2. Probe your seat first.** With ESPRIT running and a document open:

```powershell
.\dist\probe\swissforge-probe.exe --full     # -> swissforge-probe.json
```

This dumps the COM object model your ESPRIT version actually exposes. It changes nothing and
needs no elevation. **Please send `swissforge-probe.json` back** — see
[docs/ESPRIT-INTEGRATION.md](docs/ESPRIT-INTEGRATION.md). It is what turns the adapter from
careful guesswork into verified code.

**3. Install.** From an **elevated** PowerShell prompt:

```powershell
cd .\dist\addin
.\Install-SwissForge.ps1 -WhatIf              # see exactly what it would write
.\Install-SwissForge.ps1
```

Elevation is required because COM registration writes to `HKLM`. The script does two things,
and both must happen for ESPRIT to load anything:

- Runs `regasm /codebase` with the **64-bit** regasm, because ESPRIT is a 64-bit host.
  Registering with the 32-bit one puts the CLSID somewhere ESPRIT will never look, and the
  failure surfaces later as an unhelpful "class not registered".
- Writes a `SwissForge.AddIn` key with `LoadBehavior = 1` under every known ESPRIT `AddIns`
  hive. The path moved when DP Technology became part of Hexagon and still varies by version,
  so the script writes all seven candidates rather than guessing. Extra keys under a hive your
  version does not read are inert — a much cheaper failure than the add-in silently never
  loading.

**4. Restart ESPRIT** and look for SwissForge in the add-in manager.

### Configuring the add-in

Copy [`swissforge.config.example.json`](src/SwissForge.AddIn/install/swissforge.config.example.json)
to one of these. The first one found wins:

```
%APPDATA%\SwissForge\swissforge.config.json         per user
%PROGRAMDATA%\SwissForge\swissforge.config.json     whole machine
next to SwissForge.AddIn.dll                        per installation
```

Comments and trailing commas are allowed — these files get hand-edited on a shop floor.

The settings that matter on day one:

- **`materialsPath`, `toolsPath`, `machinePath`** — your real purchase prices, proven cutting
  data, and machine configuration. Anything you set overrides the built-in defaults; anything
  you leave out is inherited. Quoting **refuses to price a job off a built-in placeholder
  price**, so pointing `materialsPath` at your real numbers is what unblocks it. Start from
  [`samples/materials-override.example.json`](samples/materials-override.example.json).
- **`apiEnabled`, `apiPort`** — the local REST API. Default port 8731.
- **`apiToken`** — leave empty and one is generated on first run and written back to the
  per-user file. Set it explicitly if you want the same token across every seat.
- **`apiAllowRemote`** — off by default, and the API binds to `127.0.0.1`. Turning it on
  exposes the API to your network: anyone who can reach the machine and holds the token can
  drive it. Read [SECURITY.md](SECURITY.md) before you do.
- **`outboxDirectory`, `endpoints`** — outbound ERP integration. Events are written to disk
  first and delivered on a timer, so a network outage delays them rather than losing them.

### Uninstalling

```powershell
# elevated:
cd .\dist\addin
.\Uninstall-SwissForge.ps1
```

This unregisters the COM class and removes the add-in keys from every hive the installer
wrote to. Your configuration and outbox under `%APPDATA%\SwissForge` are left alone — those
are yours, not the installer's.

### When it does not load

- **SwissForge is missing from the add-in manager.** Confirm the registry key exists under a
  hive your version reads. Some versions honour only `LoadBehavior = 3` ("load at startup")
  rather than `1` ("load on demand"); change the value and restart. The add-in is cheap to
  start either way.
- **"Class not registered".** Almost always a bitness mismatch — re-run the installer from an
  elevated prompt and check it picked `Framework64\v4.0.30319\regasm.exe`.
- **`regasm.exe not found`.** The .NET Framework 4.x developer tools are not installed. See
  Prerequisites.
- **It loads, but quoting refuses to price anything.** Working as intended: no real material
  price is configured. See `materialsPath` above.
- **Anything else.** Run `swissforge-probe.exe --full` and include the JSON in the issue —
  it identifies exactly what your seat exposes.

---

## Architecture

```
SwissForge.Core        netstandard2.0 + net8.0   Every engine. Zero ESPRIT dependency.
  Json/                  self-contained JSON
  Units/                 mm, m/min, mm/rev, and the conversions
  Model/                 materials, tools, machines, parts, operations
  Feeds/                 material library + cutting-data engine
  CycleTime/             multi-channel waitcode scheduler
  Quoting/               cost and price
  GCode/                 parser, dialects, channel splitter, linter
  Templates/             setup templates + job planner
  Integration/           durable outbox + ERP publisher
  Api/                   HTTP server + REST surface + OpenAPI
  Esprit/                IEspritGateway  <-- the only seam

SwissForge.Esprit      net48    The ONLY assembly that talks COM.
SwissForge.AddIn       net48    IDTExtensibility2 entry point, UI, hosting.
SwissForge.Probe       net48    Type-library dumper.
SwissForge.Cli         net8.0   Everything, headless, any OS.
SwissForge.Core.Tests  net8.0   324 checks.
```

Three decisions worth understanding before changing them:

**No NuGet packages anywhere, including tests.** The add-in is loaded by COM into ESPRIT's
process, sharing an AppDomain with the host and every other add-in. Dragging in
`System.Text.Json` or Newtonsoft is the most common cause of "works standalone, throws
`FileLoadException` inside the CAM host". It also means the whole thing builds and verifies on
a locked-down shop PC with no package feed.

**Late-bound COM, not a generated interop assembly.** A generated interop assembly pins the
build to one ESPRIT version's exact type names. Late binding compiles against nothing, tries
several candidate names per member, and fails with *"ESPRIT has no member 'Foo' on type X; it
does have: ..."* instead of a `TypeLoadException` at load. Given that the API reference is not
publicly retrievable, this is the difference between shippable and not.

**The HTTP server is raw `TcpListener`.** `HttpListener` needs an elevated
`netsh http add urlacl` before it will bind — a non-starter on a locked-down shop PC. ASP.NET
Core will not load into a .NET Framework host.

---

## What this does not do

- **No collision checking.** The planner assigns work to channels assuming the tool positions
  allow concurrency. Whether a given gang slide and endworking sleeve can be in the cut
  simultaneously is a question for ESPRIT's kinematic model. The planner says so in its own
  output rather than leaving you to find out.
- **Cutting data is a starting point, not physics.** Feeds and speeds are a negotiated truce
  between your tooling vendor, your coolant, and your machine's rigidity. The built-ins are a
  first pass; override them from jobs you have actually run. See
  [docs/ENGINE-NOTES.md](docs/ENGINE-NOTES.md).
- **Material prices are placeholders** until you load your own. The quoting engine enforces
  this rather than trusting you to remember.
- **The first part off a bar takes longer** than the steady-state cycle, because back-working
  overlaps the *next* part. The planner reports the steady-state figure and says so.

---

## Documentation

- [docs/API.md](docs/API.md) — REST reference with worked examples
- [docs/ESPRIT-INTEGRATION.md](docs/ESPRIT-INTEGRATION.md) — how the adapter works, and how to
  correct it from probe output
- [docs/ENGINE-NOTES.md](docs/ENGINE-NOTES.md) — the cutting model, its assumptions, and what
  to calibrate first
- [CONTRIBUTING.md](CONTRIBUTING.md) — the two contributions worth more than code
- [SECURITY.md](SECURITY.md) — what the API exposes, and to whom

## Samples

- `samples/part-SF-2050.json` — a realistic stepped pin
- `samples/tools.json`, `samples/machine-citizen-l20.json`
- `samples/materials-override.example.json` — how to load your real numbers
- `samples/program-clean.nc` — lints clean
- `samples/program-broken.nc` — every defect in it is one that happens in real shops

---

## Contributing

The most useful contribution is not code. It is **probe output from your ESPRIT build** —
which makes the adapter work on one more version — and **cutting data from jobs you have
actually run**. Both have issue templates. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[Apache License 2.0](LICENSE). Permissive, with an explicit patent grant.

ESPRIT and ESPRIT EDGE are products of Hexagon AB / DP Technology Corp. SwissForge is an
independent, unaffiliated add-in — not endorsed by, sponsored by, or supported by either.
