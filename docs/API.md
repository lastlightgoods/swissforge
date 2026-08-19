# SwissForge REST API

Hosted on loopback by the ESPRIT add-in, or by `swissforge serve` from the command line.
The machine-readable spec is served at `/openapi.json`.

## Authentication

Every endpoint except `/health` needs a bearer token:

```
Authorization: Bearer <token>
```

The add-in generates one on first run and writes it to
`%APPDATA%\SwissForge\swissforge.config.json`. The status window has a **Copy API token**
button. `swissforge serve --token my-secret` sets it explicitly.

Comparison is fixed-time. Missing or wrong token returns `401`.

## Errors

Every error is the same shape:

```json
{ "error": "material_not_found", "message": "Material 'MITHRIL' is not loaded.", "status": 400 }
```

| Status | Meaning |
|---|---|
| 400 | Bad input. `bad_json`, `no_part`, `empty_body`, `material_not_found`, `tool_not_found` |
| 401 | Missing or wrong token |
| 404 | Unknown route or unknown resource |
| 405 | Wrong method |
| 413 | Body over the 8 MB cap |
| 503 | `esprit_unavailable` — this endpoint needs a live ESPRIT document |

`503` from an `/esprit/*` endpoint is normal when running the CLI. The message names which
endpoints still work.

---

## Reference

### `GET /health`

The only endpoint that needs no token.

```json
{ "status": "ok", "version": "1.0.0" }
```

### `GET /api/v1/status`

```json
{
  "version": "1.0.0",
  "engineAvailable": true,
  "esprit": { "connected": true, "productName": "ESPRIT", "version": "20.5.2.1",
              "documentPath": "C:\\jobs\\SF-2050.esp", "message": "Attached." },
  "materialCount": 26,
  "toolCount": 22,
  "machine": "Citizen L20-VIII"
}
```

### `GET /api/v1/materials` · `GET /api/v1/materials/{id}`

Optional `?group=StainlessAustenitic`. The `{id}` form accepts a fuzzy name, so `303` and
`SS303` both resolve.

Every material carries `costIsIndicative`. **When true, that price is a built-in placeholder,
not your purchase cost**, and quoting will refuse to price off it.

### `GET /api/v1/tools` · `GET /api/v1/machine`

The loaded tool library and machine profile.

---

### `POST /api/v1/feeds-speeds`

```json
{
  "material": "SS316",
  "tool": "T01",
  "operation": "TurnRough",
  "workDiameterMm": 12,
  "radialStockMm": 1.5,
  "cutLengthMm": 20,
  "toleranceMm": 0.02,
  "targetRaMicron": 1.6,
  "unsupportedLengthMm": 25,
  "guideBushingEngaged": true
}
```

`tool` takes an id from the library, **or a full tool object** for one that is not loaded.
`machine` may be supplied inline to override the configured profile.

```json
{
  "surfaceSpeedMPerMin": 121.5, "surfaceSpeedSfm": 399,
  "rpm": 3223,
  "feedMmPerRev": 0.164, "feedInchPerRev": 0.006457, "feedMmPerMin": 528.6,
  "depthOfCutMm": 0.75, "passes": 2,
  "predictedRaMicron": 2.101,
  "spindlePowerKw": 0.923, "cuttingForceN": 455.7, "mrrCm3PerMin": 14.94,
  "advisories": []
}
```

`advisories` is the interesting field. An empty array means nothing was clamped or derated —
the machine can run this as printed. Otherwise each entry has `severity`, `code`, `message`.
Codes include `RPM_CLAMPED`, `SLENDER_DERATE`, `STICKOUT_DERATE`, `POWER_LIMIT`,
`FINISH_UNREACHABLE`, `PITCH_LOCKED`, `FEED_FOR_FINISH`.

A `Critical` advisory means no usable numbers were produced.

### `POST /api/v1/cycle-time`

```json
{
  "operations": [
    { "id": "A1", "channel": 0, "sequence": 1, "cutSeconds": 10 },
    { "id": "A2", "channel": 0, "sequence": 2, "cutSeconds": 5, "waitBefore": "L20" },
    { "id": "B1", "channel": 1, "sequence": 1, "cutSeconds": 6 },
    { "id": "B2", "channel": 1, "sequence": 2, "cutSeconds": 5, "waitBefore": "L20" }
  ],
  "recomputeTimes": false
}
```

Set `recomputeTimes: true` to derive durations from `rpm`/`feedMmPerRev`/`cutLengthMm` instead
of supplying them.

```json
{
  "cycleSeconds": 15, "cycleFormatted": "15.0s",
  "sumOfOperationSeconds": 26, "overlapRatio": 1.733,
  "partsPerHour": 240, "partsPerDay": 4080,
  "bottleneckChannel": 0,
  "channelBusySeconds": { "0": 15, "1": 11 },
  "channelIdleSeconds": { "0": 0, "1": 4 },
  "schedule": [ { "operationId": "A1", "startSeconds": 0, "endSeconds": 10, "waitSeconds": 0 } ],
  "advisories": []
}
```

`channelIdleSeconds` is where the recoverable cycle time lives. Advisory codes:
`CHANNEL_IDLE`, `NO_OVERLAP`, `UNBALANCED`, `ORPHAN_WAIT`, `DEADLOCK`, `SCHEDULER_STALL`.

A `DEADLOCK` advisory names both channels and the label — that is a program that hangs the
machine with every channel lit and nothing moving.

### `POST /api/v1/plan`

```json
{ "part": { ... }, "machine": { ... }, "tools": { ... }, "template": { ... } }
```

Only `part` is required. Returns the full operation list with cutting data, channel
assignments, waitcodes, a timed schedule, and advisories.

`isRunnable: false` means something is missing that makes the plan unusable — a tool that does
not exist, a material that is not loaded. Each is named.

### `POST /api/v1/quote`

Same body as `/plan`, plus optional `cycleSeconds`, `policy`, and `quantityBreaks`.
**If `cycleSeconds` is omitted the job is planned first to derive one** — a caller sending a
part expects a price, not a two-step dance.

```json
{
  "partNumber": "SF-2050", "currency": "USD",
  "isQuotable": true,
  "cycleSeconds": 27.6, "partsPerBar": 125, "barUtilizationFraction": 0.854,
  "partsPerHour": 130.6, "lotMachineHours": 38.3, "lotCalendarDays": 2.3,
  "primary": {
    "quantity": 5000,
    "materialPerPart": 0.144, "machinePerPart": 0.5807, "toolingPerPart": 0.0193,
    "setupPerPart": 0.068, "programmingPerPart": 0.00475, "scrapPerPart": 0.0149,
    "costPerPart": 0.8317, "pricePerPart": 1.2795, "marginFraction": 0.35,
    "lotPrice": 6397.50
  },
  "breaks": [ ... ],
  "advisories": []
}
```

**`isQuotable: false` means do not send this to a customer.** The reason is a `Critical`
advisory: `INDICATIVE_MATERIAL_COST`, `NO_CYCLE_TIME`, `BAR_TOO_BIG`, `PART_BIGGER_THAN_BAR`,
`NO_MATERIAL`, `NO_PARTS_PER_BAR`.

Set `policy.allowIndicativeMaterialCost: true` to price a budgetary quote deliberately.

Informational codes worth reading: `SETUP_DOMINATES`, `MATERIAL_DOMINATES`,
`LOW_BAR_UTILIZATION`, `LONG_RUN`.

---

### `POST /api/v1/gcode/lint`

```json
{ "gcode": "O1000\nG21 G90\n...", "dialect": "CitizenCincom", "name": "SF-2050" }
```

```json
{
  "programName": "SF-2050", "dialect": "CitizenCincom", "channelCount": 2,
  "critical": 3, "errors": 2, "warnings": 5, "info": 1, "isClean": false,
  "findings": [
    { "severity": "Critical", "code": "CSS_NO_CLAMP",
      "message": "Constant surface speed (G96) is active with no G50 spindle clamp.",
      "suggestion": "As X approaches centre the commanded rpm approaches infinity...",
      "line": 10, "channel": 0, "raw": "G1 X-0.2" }
  ]
}
```

Dialects: `CitizenCincom`, `StarSR`, `TsugamiFanuc`, `HanwhaXD`, `FanucGeneric`.

**Rules**

| Code | Severity | What it catches |
|---|---|---|
| `CSS_NO_CLAMP` | Critical | G96 with no G50 spindle clamp |
| `FEED_MOVE_NO_F` | Critical | A cutting move before any F word |
| `ZERO_FEED` | Critical | F0 or negative |
| `MIXED_UNITS` | Critical | G20 and G21 in one program |
| `UNPAIRED_WAITCODE` | Critical | A waitcode only one channel reaches |
| `WAITCODE_COUNT_MISMATCH` | Critical | A label used a different number of times per channel |
| `WAIT_PARTNER_MISSING` | Critical | A waitcode naming a channel that never reaches it |
| `CUT_WITHOUT_SPINDLE` | Error | A feed move with no spindle command active |
| `ARC_NO_GEOMETRY` | Error | G2/G3 with no R and no I/J/K |
| `SPEED_ABOVE_MACHINE` | Error | S beyond what the spindle can turn |
| `NO_END_OF_PROGRAM` | Error | A channel that never reaches M30/M2/M99 |
| `DUPLICATE_O_NUMBER` | Error | Two programs sharing a number |
| `UNTERMINATED_COMMENT` | Error | An unclosed `(` |
| `TOOLCHANGE_NO_RETRACT` | Warning | An index straight out of a cut |
| `FEED_ABOVE_RAPID` | Warning | Usually a decimal slip or a unit mix-up |
| `SPINDLE_LEFT_ON` | Warning | End of program with the spindle running |
| `INCREMENTAL_LEFT_ON` | Warning | Ends in G91; the next program inherits it |
| `Z_BEYOND_STROKE` | Warning | A Z well past the machine's stroke |
| `COOLANT_NEVER_ON` | Warning | Cutting with no coolant anywhere |

State defects are reported **once per channel**, at the first line where they bite.

### `POST /api/v1/gcode/analyze`

Structure rather than defects: the channel split, a waitcode pairing table, and per-channel
statistics. Set `includeBlocks: true` for every parsed block.

---

### ESPRIT endpoints

All return `503 esprit_unavailable` outside ESPRIT.

- **`GET /api/v1/esprit/probe`** — which expected members resolved on this build. Answers even
  offline.
- **`GET /api/v1/esprit/stock` · `/tools` · `/operations`** — read the active document.
- **`POST /api/v1/esprit/cutting-data`** — push changes back:
  ```json
  { "updates": [ { "operationId": "12", "speedRpm": 3223, "feedMmPerRev": 0.164,
                   "reason": "SwissForge recommendation for 316" } ] }
  ```
  Returns `requested` / `applied` / `skipped`.
- **`POST /api/v1/esprit/post`** — run a post processor and, unless `lint: false`, lint the
  output in the same call.

---

## Worked example

```bash
TOKEN=your-token-here
API=http://127.0.0.1:8731

curl -s $API/health

curl -s -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' \
     -d '{"material":"SS316","tool":"T01","operation":"TurnRough",
          "workDiameterMm":12,"radialStockMm":1.5,"cutLengthMm":20}' \
     $API/api/v1/feeds-speeds

curl -s -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' \
     --data-binary @- $API/api/v1/gcode/lint <<< \
     "{\"gcode\": $(python3 -c 'import json,sys; print(json.dumps(open("main.nc").read()))')}"

curl -s -H "Authorization: Bearer $TOKEN" \
     -H 'Content-Type: application/json' \
     -d "{\"part\": $(cat part.json), \"quantityBreaks\":[100,1000,25000]}" \
     $API/api/v1/quote
```
