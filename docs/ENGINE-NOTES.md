# Engine notes

What the numbers mean, where they come from, and what to calibrate before you trust them.

---

## Cutting data

### The model

Surface speed starts from the material's baseline for coated carbide at its nominal hardness,
then gets three corrections:

```
Vc = base × hardnessFactor × substrateFactor × operationFactor
```

- **Hardness** — `(nominal / actual) ^ 0.8`. The pure inverse over-corrects on the soft end and
  produces speeds nobody would run; the exponent is softened deliberately.
- **Substrate** — HSS 0.35, cobalt 0.45, carbide 1.00, coated carbide 1.35, cermet 1.50,
  CBN 2.50, PCD 3.00.
- **Operation** — turning 1.00, finishing 1.20, boring 0.80, grooving 0.60, cutoff 0.55,
  threading 0.65, drilling 0.55, reaming 0.40, tapping 0.25, knurling 0.15.

Feed starts from tool geometry — nose radius for turning, diameter for drilling, pitch for
threading — then gets multiplied by the material's feed factor and derated for stickout and
unsupported length.

Force and power use the standard Kienzle treatment: `kc = kc1.1 × h^(-mc)`, with tabulated
`kc1.1` and `mc` per material family.

### The three derates worth understanding

**Tool stickout.** Deflection scales with `(L/D)³`. Past 4 diameters of stickout, feed is
pulled back hard — reaching 30% at 12 diameters. This is what stops the generated program
chattering a 1 mm drill 12 mm deep at catalogue feed.

**Workpiece slenderness.** The Swiss-specific one. A Swiss lathe supports the bar right at the
cut, so what matters is not part length but **how far the tool is working ahead of the guide
bushing**. Past ~3 diameters the part starts pushing away and the finish goes with it. Past 8,
you are in chatter territory and the advisory escalates to an error.

This applies **only to single-point work on the outside of the part** — turning, grooving,
threading, knurling. For drilling, tapping, milling and cross-work the flexible member is the
tool, not the part, and applying both derates would cut a 2 mm cross drill to a third of its
feed because the part happens to be long. That is a bug that was caught in testing and is now
a regression test.

**Chucker mode.** With the guide bushing removed the whole protrusion is a cantilever and the
limits arrive far sooner — derating from 3 diameters at 20% per diameter instead of 10%.

### What to calibrate first, in order

1. **`costPerKg` for every material you buy.** Not cutting data, but it is what unblocks
   quoting, and a wrong metal price moves the quote more than a wrong feed.
2. **`baseSurfaceSpeedMPerMin` for your top three materials**, from jobs you have actually run.
   The built-ins are conservative-to-middling; a shop with high-pressure coolant and a modern
   coated grade will beat them comfortably.
3. **`partsPerEdge` and `costPerEdge` per tool.** This is the whole tooling line in the quote,
   and in a hard material it is where the margin goes.
4. **`toolLifeFactor` per material.** Multiplies `partsPerEdge`. If 316 eats twice the inserts
   that 303 does on your machines, that ratio belongs here.
5. **`hourlyRate` and `utilizationFactor` per machine.**

Put all of it in a materials override file and point `materialsPath` at it. Anything you set
replaces the default; anything you leave out is inherited, so you only write down what you know.

### What the model does not know

Coolant pressure and delivery. Insert geometry beyond nose radius. Machine rigidity beyond a
power rating. Bar straightness. Whether the guide bushing is properly adjusted. Any of these
moves the real answer more than the differences between the correction factors above.

**Treat the output as a defensible starting point, not an answer.** That is what the advisories
are for — they tell you which assumption is doing the work in any given number.

---

## Cycle time

### Why summing operations is wrong

On a Swiss machine, two to four channels cut simultaneously. The cycle is the **critical path
through the waitcode graph**, not the sum of the work. A job with 40 seconds of operations can
run in 18 — or in 40, if the waitcodes are in the wrong places.

The scheduler models:

- **Channels** running independently in programmed sequence.
- **Waitcodes** as rendezvous. All channels using a label wait for the last to arrive. A label
  reused later fires again, because that is normal practice, not an error.
- **Exclusive operations** — cutoff, transfer, bar feed — as global barriers. Everything stops.
- **Deadlock**, where each channel is parked on a label the other only reaches later. Reported
  as `Critical`, because on the machine that hangs the cycle.
- **Orphaned waitcodes**, used by only one channel. Almost always a deleted partner or a typo.

### The output that matters

`channelIdleSeconds`. Cycle time tells you where you are; idle time tells you what to do about
it. A channel idle 80% of the cycle is a channel that could be carrying work off the bottleneck,
and that is nearly always the cheapest cycle-time reduction available.

`overlapRatio` — sum of operations ÷ cycle time. 1.0 means fully serial. On a multi-channel
machine, under about 1.2 means you are leaving most of the machine on the table.

### The overlap assumption

Back-working on the finished part overlaps main-spindle work on the *next* one. That is the
whole point of a Swiss machine, and the planner schedules it that way by default.

Consequence: **the first part off a bar takes longer than the steady-state cycle**, because
there is no previous part to work on. The planner reports steady state and says so in an
advisory. For a 5-piece prototype run, that matters.

### What it does not do

**No collision checking.** Work is assigned to channels assuming those tool positions can be in
the cut simultaneously. Whether a given gang slide and endworking sleeve actually can is a
question for the kinematic model in ESPRIT — a planner working from a feature list cannot
answer it. The plan says so in its own output rather than leaving you to find out.

---

## Quoting

### The two things spreadsheets get wrong

**The remnant is not free.** A 3660 mm bar with a 300 mm unpushable tail and 2 mm of face stock
gives 3358 mm of usable length. At 26.8 mm per part that is 125 parts — and you paid for the
whole bar. Material cost per part is bar cost ÷ **parts actually produced**, not ÷ parts
theoretically contained.

**Scrap costs finished parts.** A part scrapped at final inspection has consumed its material,
its machine time, *and* its tooling. Costing scrap as lost bar stock systematically under-quotes
tight-tolerance work, which is exactly the work where scrap rates are highest.

### Bar yield

`barUtilizationFraction` is **finished part length ÷ bar length** — the fraction of metal you
bought that leaves as product. Kerf, back-face stock, and the remnant are all metal bought and
thrown away.

This is deliberately not "bar length consumed", which is ~100% by definition and tells you
nothing. A 3 mm cutoff blade on a 5 mm part yields 55%; the advisory names the blade as the
dominant loss, because a thinner blade pays for itself in a week on a job like that.

### The refusal

The quoting engine will **not** produce a sendable price off a built-in placeholder metal price.
`isQuotable` comes back false with `INDICATIVE_MATERIAL_COST` naming the material.

This is deliberate friction. A quote built on an invented metal price is the kind of mistake
that only surfaces on the invoice. Load your real cost, set `costPerBar` on the stock, or pass
`allowIndicativeMaterialCost` to say you know it is budgetary.

### Amortisation

- **Setup** spreads across the lot quantity.
- **Programming** spreads across `annualUsage` when the customer has told you it, otherwise
  across the lot. Punishing the first release for the entire programming cost loses work you
  would have been glad to have.
- **Bar changes** are real machine minutes, spread across the parts that bar produced.
- **Tooling** is `costPerEdge ÷ (partsPerEdge × toolLifeFactor)`, plus the machine minutes spent
  indexing a worn edge — those are machine minutes too.

---

## G-code analysis

### Parsing

Deliberately permissive. Real post-processor output is full of vendor extensions, and a parser
that throws on the first unfamiliar address is useless for auditing programs a shop actually
runs. Anything it cannot classify is preserved as raw text and **reported** rather than silently
dropped.

Handles: multi-channel `$n` sections, Citizen/Star `!nLxx` waitcodes, Fanuc M-code waits in the
reserved range, block delete, `( )` and `;` comments, `%` tape markers.

### Why the linter is conservative

A tool that cries wolf gets switched off, and then it catches nothing at all. So:

- `Critical` is reserved for things that stop the job or crash the machine.
- State defects are reported **once per channel**, at the first line where they bite — not once
  per line for the rest of the program.
- Findings that depend on an assumption say so. `CUT_WITHOUT_SPINDLE` notes that the spindle may
  legitimately be started in another channel or a sub-program.

Every finding carries a line number, the offending code, and a suggested fix. A finding you
cannot act on is noise.
