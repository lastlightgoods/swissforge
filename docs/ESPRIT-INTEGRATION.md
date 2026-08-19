# ESPRIT integration

This is the part of SwissForge that is not yet verified, and this document is the honest
account of why, what was done about it, and what you need to do to close the gap.

---

## The problem

To write a plugin that drives ESPRIT you need to know the exact names of the properties and
methods on its automation object model. Two ways to get them:

1. **Hexagon's API reference.** It exists — a Doxygen-generated site with interfaces like
   `IChannels`, `IMachineItem`, `IOutputWindow`, `EspritUtils::IUtility`. It blocks automated
   retrieval.
2. **The type libraries on a licensed installation.** Which live on your machine, not here.

So the member names in `EspritGateway.cs` are the conventional ESPRIT automation names.
They are a well-founded starting point. **They are not verified against your build.**

The engineering question was: given that, what do you ship?

---

## What was done about it

### 1. The COM surface is contained in one file

`SwissForge.Esprit` is the only assembly in the solution that talks to ESPRIT. Everything
above it depends on `IEspritGateway`, an interface with eleven members. That means:

- The engine layer is developed and unit-tested with no ESPRIT installed — 324 checks run on
  Linux, macOS, Windows, or a CI container.
- A version difference in the host changes exactly one file.
- A failure in the CAM system degrades into a handled error rather than taking the add-in
  down with it.

### 2. Late binding, not a generated interop assembly

The conventional route is `tlbimp` on the ESPRIT type library, producing a strongly-typed
interop assembly. That would not have compiled here, and it would pin the build to one ESPRIT
version's exact type names.

SwissForge uses `IDispatch` reflection instead:

```csharp
var tools = document.GetAny("Tools", "ToolList", "CuttingTools");
```

`GetAny` returns the first candidate that resolves. Small naming differences between ESPRIT
generations are absorbed without a code change. A member that genuinely does not exist
produces:

```
ESPRIT has no member 'Tools' on ESPRIT.DocumentClass.
Members it does expose include: ActiveDocument, Application, CuttingTools, Documents, ...
```

That message tells you the fix. A `TypeLoadException` at assembly load does not.

The cost is no compile-time checking and slower calls. At the volume of calls this add-in
makes, neither matters, and version tolerance is worth considerably more.

### 3. The member map is one editable table

Every name lives in `EspritGateway.MemberMap`:

```csharp
public static string[] Tools      = { "Tools", "ToolList", "CuttingTools" };
public static string[] Operations = { "Operations", "OperationList", "Processes" };
public static string[] SpeedRpm   = { "SpindleSpeed", "Speed", "RPM", "SpeedValue" };
```

To correct one, **put the real name first and leave the others as fallbacks** — that way the
same binary keeps working on the other seats in the shop, which may be a different version.

### 4. The probe reads the truth off your machine

`swissforge-probe.exe` attaches to a running ESPRIT and walks the actual COM type library via
`ITypeInfo`/`ITypeLib`. Every interface, method, property, parameter name, and parameter type.
It is read-only: it never modifies the document, never saves, never posts.

---

## The probe workflow

### Run it

Start ESPRIT, open a document that represents typical work, then:

```powershell
.\dist\probe\swissforge-probe.exe --full --out swissforge-probe.json
```

Output:

```
SwissForge probe
Reads ESPRIT's COM object model. Read-only: nothing is modified or saved.

  Trying ProgID 'Esprit.Application' ... attached.
  Product : ESPRIT
  Version : 20.5.2.1

  Checking the members SwissForge expects...
    ok      application.document -> Document
    ok      document.tools -> Tools
    MISSING document.operations: none of [Operations, OperationList, Processes] resolved
    ...
```

Two useful things immediately: a pass/fail list of exactly which expected members resolved,
and a complete dump of what does exist.

### If it cannot attach

The four causes, in order of likelihood:

1. **ESPRIT is not running**, or has no document open.
2. **Elevation mismatch.** If ESPRIT runs elevated and the probe does not (or vice versa), the
   Running Object Table is not shared and the attach *always* fails. Run both the same way.
3. **Bitness mismatch.** The probe reports its own bitness on startup.
4. **A different ProgID.** Pass `--progid Your.ProgID`.

### Send it back

The report contains your machine name, ESPRIT version, and the names of tools, operations,
and post processors in the open document. If any of that is sensitive, open a scratch document
before running it, or delete the `tools` and `operations` sections before sending. The
`typeLibrary` section is what matters and contains nothing about your parts.

### Correcting the map

Find the real member in the dump:

```json
{
  "name": "IPartDocument",
  "kind": "TKIND_DISPATCH",
  "members": [
    { "name": "CuttingTools", "kind": "get", "returns": "IDispatch",
      "signature": "IDispatch CuttingTools { get; }" },
    { "name": "MachiningOperations", "kind": "get", "returns": "IDispatch",
      "signature": "IDispatch MachiningOperations { get; }" }
  ]
}
```

Then:

```csharp
public static string[] Tools      = { "CuttingTools", "Tools", "ToolList" };
public static string[] Operations = { "MachiningOperations", "Operations", "OperationList" };
```

Rebuild, reinstall, done. Nothing else in the codebase changes.

---

## Installing the add-in

Two things have to happen:

1. **COM registration** — `regasm /codebase`, which needs administrator rights.
2. **ESPRIT must be told the add-in exists** — a registry key under the vendor's `AddIns`
   hive with `LoadBehavior` set.

`Install-SwissForge.ps1` does both. The `AddIns` key path moved when DP Technology became part
of Hexagon and varies by version, so the script writes **every known candidate**:

```
HKLM\SOFTWARE\D.P.Technology\ESPRIT\AddIns\SwissForge.AddIn
HKLM\SOFTWARE\WOW6432Node\D.P.Technology\ESPRIT\AddIns\SwissForge.AddIn
HKLM\SOFTWARE\Hexagon\ESPRIT\AddIns\SwissForge.AddIn
HKLM\SOFTWARE\Hexagon\ESPRIT EDGE\AddIns\SwissForge.AddIn
HKCU\... (the same four)
```

Extra keys under a hive your version does not read are inert. That is a far cheaper failure
than the add-in silently never loading.

### If it does not appear in ESPRIT

- **`LoadBehavior`.** The script writes `1` (load on demand). If your version only honours `3`
  (load at startup), change it and restart. The add-in is cheap to start either way.
- **Bitness.** ESPRIT is a 64-bit host. Registering with the 32-bit `regasm` puts the CLSID in
  a hive it will never look in. The script uses `Framework64` deliberately.
- **`/codebase`.** Without it the CLR looks in the GAC and will not find the DLL.
- **The registry path.** Look under `HKLM\SOFTWARE` for a vendor key with an `AddIns` subkey
  and see what other add-ins on that machine did. If it is a path the script does not write,
  send it back — it belongs in the list.

---

## What the add-in does once loaded

`OnConnection` is deliberately cheap: attach, read a config file, start the API on a background
thread. ESPRIT is waiting on that call.

**Nothing in the add-in may throw.** An unhandled exception inside a COM callback propagates
into the host, and taking down a CAM system along with a programmer's unsaved work is not a
forgivable way for a convenience add-in to fail. Every entry point is wrapped; failures are
logged and swallowed. If SwissForge cannot start, ESPRIT keeps running with the add-in inert.

The status window shows the API URL and token, the connection state, and a log. Four buttons:
probe, read the document, post-and-lint, copy the log.

---

## `IDTExtensibility2` is declared, not referenced

The conventional route is a COM reference to `Extensibility.dll` (the Microsoft Add-In Designer
type library). SwissForge declares the interface directly with its published IID
(`B65AD801-ABAF-11D0-BB8B-00A0C90F2744`).

COM binds by IID, not by which assembly declared the interface, so identity is identical. The
benefit is that this project has **no external references at all** — and a missing or
version-mismatched `Extensibility.dll` on one shop machine is a common and deeply unhelpful way
for an add-in to fail to load.

The method order in that declaration is the published vtable order and must not be changed.

---

## Security

- The API binds to **127.0.0.1 only** unless you explicitly set `apiAllowRemote`. Turning that
  on exposes it to your network; the add-in logs a warning when you do.
- A **bearer token** is required on everything except `/health`. Generated on first run and
  written to `%APPDATA%\SwissForge\swissforge.config.json`.
- Token comparison is **fixed-time**, so it does not leak one character at a time.
- Request bodies are **capped** (8 MB default).
- Outbound integration payloads carry an **HMAC-SHA256 signature** and an **idempotency key**,
  so the receiver can verify origin and drop duplicates — a timeout after the far end committed
  looks identical to a failure.
