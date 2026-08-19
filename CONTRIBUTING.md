# Contributing to SwissForge

The most useful contribution is not code. It is **numbers from jobs you have actually run**,
and **the truth about what your ESPRIT build exposes**.

---

## The two highest-value contributions

### 1. Probe output

The ESPRIT adapter was written without access to the type libraries — Hexagon's API reference
blocks automated retrieval, and the libraries only exist on a licensed install. So the member
names in `EspritGateway.MemberMap` are the conventional ESPRIT automation names, verified
against as few real builds as have been reported.

If you have ESPRIT, run this and open an issue with the result:

```
swissforge-probe.exe --full --out swissforge-probe.json
```

It is read-only — it never modifies the document, saves, or posts. Every report makes the
adapter work on one more version. Use the **ESPRIT compatibility report** issue template.

### 2. Cutting data you can stand behind

The built-in material library is a defensible starting point, not physics. If you run 316 at
numbers the engine disagrees with, and you have the tool life to back it up, that is worth more
than any published table. Use the **Cutting data disagreement** issue template.

---

## Building

```bash
./verify.sh          # engines only. Any OS, no ESPRIT, no packages needed.
```

```powershell
.\build.ps1          # everything, including the net48 projects. Windows only.
```

You need the .NET 8 SDK. Nothing else — **there are no package dependencies anywhere in this
solution, including the tests.** Please keep it that way; the reasons are in the Architecture
section of the README, and they are load-bearing.

Building the add-in does **not** require an elevated prompt. COM registration is the
installer's job, not the compiler's.

---

## Where things live

```
SwissForge.Core        Every engine. No ESPRIT dependency. This is where most work happens.
SwissForge.Esprit      The ONLY assembly that talks COM. Windows only.
SwissForge.AddIn       IDTExtensibility2 entry point, UI, hosting. Windows only.
SwissForge.Probe       Type-library dumper. Windows only.
SwissForge.Cli         Headless CLI. Any OS.
SwissForge.Core.Tests  Custom harness, no test framework dependency.
```

If a change makes `SwissForge.Core` depend on ESPRIT, it is the wrong change. That seam is what
lets the engines be tested on any machine, and it is worth defending.

---

## Tests

Every behavioural change needs a check. The harness is deliberately tiny — no xunit, no NUnit:

```csharp
Check.Suite("Multi-channel scheduling");
Check.Near(15, result.CycleSeconds, 1e-9,
    "the rendezvous holds the fast channel until the slow one arrives");
```

**Write the assertion message as a claim about the world, not a restatement of the code.**
"the rendezvous holds the fast channel until the slow one arrives" tells a reader what should
be true. `Assert.Equal(15, cycle)` does not.

When a test fails, work out which of the two is wrong before changing either. Several of the
existing regression tests exist because a test caught a real modelling error — a cross drill
being derated for the *part's* overhang when the flexible member is the tool, bar utilisation
counting kerf as "used" — and the fix belonged in the engine, not the expectation.

---

## Style

- Comments explain **why**, not what. If a constant is 0.8 rather than 1.0, say what goes wrong
  at 1.0.
- Advisories are user-facing text. Write them for a machinist reading them at 6am, and always
  say what to *do*: "Add G50 S&lt;max&gt; before the G96", not "invalid G96 usage".
- Prefer a legible failure to a plausible fabrication. An engine that returns zero and says why
  beats one that invents a number.
- No package references. See above.

---

## Reporting a security issue

See [SECURITY.md](SECURITY.md). Please do not open a public issue for one.
