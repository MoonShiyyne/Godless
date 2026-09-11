# Godless build order

Derived from the design doc (rev 7) and the build-order registry (v1, rev 5).
Full stage plan, with the reasoning and the gate failure actions:
https://claude.ai/code/artifact/0bcf773e-22cb-4314-b8a1-8e290edfe63b

The design doc is the source of truth. This file is derived. When they
disagree, the doc wins and this file gets re-synced.

## Current position

**G0 passed on 2026-09-11. Stratum 0 is complete**, S0B included — it was
skipped first time round and not noticed until S14 (none of G0's conditions
reads it), then built after S14. Stratum 1 ends at **G1 — the go/no-go gate
that can cancel the project.**

| G0 condition | Evidence |
|---|---|
| Guard catches L1 / L2 violations | `Tools/check-laws.sh`; asmdef refusal of `using UnityEngine;` verified in the Editor (runbook B4) |
| Same seed, 300 years, byte-identical | `sim verify --seeds 0..20 --years 300` |
| Harness runs unattended | `sim run` — 60,000 sim-years in 0.28 s |
| Deleting base content still boots | `WithoutTheBaseMod_ItStillBoots` |
| M0: raise a hill at 60 fps, scrub back | In-Editor: 1.39 ms CPU / 0.67 ms GPU per frame; a scrub to tick 0 reproduces the untouched island's digest exactly |

**Stratum 1, in dependency order** (the critical path to G1 runs down the
middle column):

| Parallel | Critical path | Parallel |
|---|---|---|
| S17 minimal genome (6 genes) | S10 parcel grid + influence maps | S11 material stock |
| S1D base tileset + silhouette rules | S15 site scoring | S12 drives, S1C task allocation |
| S1F constraint fields | S18 split grammar | S1E subsistence |
| S1G separation metric | S19 WFC against stock | S13 simple pather |
| S29 screenshot harness | S1A physical construction | S1B footprint claim |

**Done in stratum 1: S10, S17, S12, S14, S11, S1C.**

Next, in dependency order toward G1: **S1D** the base tileset (S19 needs it),
**S1F** constraint fields (S15 needs them), **S18** split grammar (S17 and
S14 are done — the first system that turns a gene into a shape). S18 split grammar is the first system that can turn a gene into a
visible shape — S17's tell only becomes testable there, and it needs only
S17 and S14, both done.

`sim content` lists every gene and need with its tell. `sim parcels --field
water-distance|slope|height` draws the planning fields. `sim settle` founds a
settlement and prints its days — S12's tell, readable without a renderer.

### What S12 shipped, and what it deliberately did not

- Needs and activities are content (`Assets/Content/base/drives`). Stratum 1
  ships shelter, warmth, hunger and safety, per the stage plan; grief and
  reverence have no producer yet and status has no consumer.
- **Daily weather ships inside S12** (`World/Weather.cs`), because the tell is
  "slept in the rain" and nothing else produces rain. Stateless per day, from
  the biome's rainfall and winter severity.
- A `Settlement` type exists (`Assets/Sim/Settlements`) holding the people,
  the hearth and the roof count. Founding is a stand-in in `sim settle` —
  the flattest dry parcel near water — until S15 and S30.
- Nights in the open are recorded **by spell**, one record per run of nights
  with the same count and weather, and every unit of shelter pressure names
  one. Hunger rises with time alone, so it names nothing, correctly.
- All agents are identical apart from where they slept, so they move in
  step. Individual thresholds are S1C's job, not a tuning bug.
### What S1C shipped

- `Collective/`: task kinds are content; stratum 1 has one, `gather`, which
  expands to a task per material the catchment offers. Each person gets a
  threshold per task, drawn from the pair of stable ids; a free worker takes a
  task up with probability s²/(s²+θ²); doing it lowers θ, not doing it raises
  it. Stimulus grows with demand (commissioned budget plus a reserve, split by
  yield, less stock) and falls with work.
- Measured as "each gatherer's share of their gathering on their own main
  material": 0.93 with learning, against 0.58–0.74 for the same flat
  population without it. The busiest-few share is the wrong measure — a task
  that needs six hands at once cannot be done mostly by four.
- Nobody takes up or stays on a task nothing wants; without that the stock
  overshot its target twofold.
- Gather rates rose about fourfold (oak 1.5 voxels a labour tick at full
  yield): a voxel is an eighth of a cubic metre, a tick a quarter day.
- With no roofs, people spend half their daylight at the fire, so labour is
  thin until S1A builds. That is the pressure working, not a bug.

### What S11 shipped

- The biomes had promised oak, slate, reed and thatch since S09 and no voxel
  declared them. They exist now, placed in the palette so the contrast rule
  still holds (values 12, 22, 70, 86 around the existing 34-78).
- A material is a voxel with a `gather` rate. A settlement's `Catchment` is
  what the land within `haulRangeVoxels` (40) offers, per material, and so how
  fast a tick of labour gathers it. The range is short on purpose: at 64 a
  highland hearth gathered timber at full speed and every biome had every
  lowland material.
- `MaterialStock` holds whole voxels. Takes are all or nothing; the first
  failed take in a run is a `stock.short` record, caused by whatever wanted
  the material. That is the moment S19's substitution will read.
- Nothing gathers yet. Labour becomes stock through S1C's tasks.

### What S0B shipped

- Priority-flood drainage over every column (`World/Drainage.cs`): each
  column's way to the sea, rain gathered from upstream (biome rainfall), and
  pits filled to their spill height. A bucket queue with FIFO inside a level
  and a fixed neighbour order, so flats drain identically everywhere. ~8 ms.
- Worldgen now ends by carving it (`World/Hydrology.cs`): rivers where flow
  passes `RiverFlow`, broad where it passes six times that; lakes in basins,
  **capped at three voxels over the floor** so a highland caldera holds a
  tarn instead of drowning the biome G1 needs. Every caller of
  `IslandGenerator.Generate` — saves, the Editor, the harness — gets it.
- `IslandMap` gains `WaterLevelAt`, `IsRiver`, `IsLake` and
  `HeightAboveWaterAt` (height above nearest drainage — the water table, and
  how high a flood must rise to reach a column). Site scoring and floods read
  that. `sim island` draws rivers `=` and lakes `o`.
- Three S0B invariants over the voxels themselves: a river reaches the sea,
  water rests on ground under open air, no lake exceeds its cap.
- Static after worldgen. A god who parts a river needs drainage recomputed
  from the live terrain; the pass is cheap, and S2J is where it is called.

### What S14 shipped

- Intent kinds are content (`Assets/Content/base/intents`); stratum 1 has one,
  shelter. One kind per need, refused by name otherwise.
- Pressure piles up per kind in a sparse demand field and a table of the
  records behind it, decays with a half-life, and is weighed once a day. Past
  threshold it becomes a `BuildIntent` leaning toward the heaviest parcel,
  carrying the four strongest contributing records, and the pile is spent.
  Beyond `maxOpen`, pressure makes the newest intent heavier instead.
- **Annal records gained `Contributors`**: the causes after the strongest.
  `Consequences` follows them, the digest covers them, and the save is v2
  (v1 still reads).
- Lifecycle on record: raised, claimed, resolved, abandoned, each caused by
  the raising, so a structure's chronicle walks back through its intent.
- Nothing claims or resolves an intent yet. That is S15 and S1A. Part 27's
  "no build intent unresolved for > 3 years" cannot hold until they exist,
  so it is not in the harness yet — `OldestOutstandingAge` is there for it.

- **Debt: settlement state is not in the save.** S0C saves seed, deltas and
  annals; agents' needs are sim state that replay does not rebuild. Extend
  the save before G1's scenario, not after.

## Registry health warning

The registry is v1, derived from design doc **rev 5**, `sourceParts "01-23"`.
The doc is now **rev 7 with 27 parts** and the part numbering shifted.
**45 of 68 rows carry at least one wrong part number.** Match systems by their
`name` and `tell`, never by the `part` field, until the re-sync lands.

Three known defects to respect before the re-sync:

- **S63 is superseded.** Part 16 replaced grievance-threshold schism with
  schism as a failure mode of the quorum decision system (S3A). Building S63
  as registered produces exactly what rev 7 rejects.
- **S28 (harness) must move to stratum 0 as S08.** L7 requires an assertion in
  the harness from stratum 2 on, and the harness is *in* stratum 2 — so the
  first ten systems L7 governs cannot comply.
- **No worldgen row exists.** "biome" appears nowhere in the registry, yet
  G1's first test is "change the biome." S10, S11, S15 and S16 all depend on
  it. Registered in the stage plan as S09.

## Stratum 0 — Substrate (M0, ~4 units)

| ID | System | Deps |
|----|--------|------|
| S00 | Sim / Unity assembly split, plus CI guard | — |
| S01 | Fixed tick + seeded RNG streams | S00 |
| S02 | Content pipeline, base game as mods | S00 |
| S03 | Voxel chunk store, 512x512x160 | S00 |
| S04 | Voxel delta log + snapshots | S03, S01 |
| S05 | Annalist record schema | S01 |
| S06 | Binary greedy meshing + baked AO | S03 |
| S07 | Camera, input, terrain editing | S06 |
| S08 | Headless runner + assertion framework | S00, S01 |
| S09 | Island generator, biomes, material deposits | S03, S02 |
| S0A | Palette, module grid, material cap, contrast rule | S02 |
| S0B | Water table + flow accumulation | S03, S09 |
| S0C | Save / load serialization contract | S03, S04, S05 |

S00 is complete: the asmdef sets `noEngineReferences: true`, `Tools/check-laws.sh`
guards L1 and L2, and CI runs it.

**Exit (G0):** determinism test green (same seed, 300 years, byte-identical
annals); the guard fails a planted `using UnityEngine;`; deleting
`Assets/Content/base` still boots; the harness runs unattended.
**On failure: do not start stratum 1.**

S01's half of G0 is green: 300 simulated years digest-identical across runs,
and identical under a different stream registration order.

S02's half is green too: `WithoutTheBaseMod_ItStillBoots` loads a content
root with no base mod and gets zero mods, zero documents and no exception.

S05 gives "byte-identical annals" something to actually mean: `Annalist`
digests its whole record, so the G0 assertion becomes a digest comparison
once there are systems writing into it.

S03's tell is measured, not asserted: a full island surface costs **2 MB**
across 512 of 1280 chunks, against 160 MB for a naive int per voxel — 78x.
The budget test is set at 8 MB so that losing uniform-chunk elision fails
in CI rather than in a profiler.

S04 answers the simulation half of M0's exit condition already:
`RaiseAHill_ThenScrubBackToBeforeYouRaisedIt` raises a hill in year 12,
scrubs back to tick 0, finds the flat ground, and confirms the reconstructed
world digests identically to the original. What is missing from that scene is
the renderer, not the history.

S08 closes the harness half of G0: `sim verify` runs each seed twice and
compares byte for byte, and `sim run` checks every registered invariant
across 200 seeds and exits non-zero when one fails.

Remaining for G0: the hill-raise scene rendered at 60fps (S06, S07 — both
need an Editor).

## History: rules learned in S07

- **Call `SimWorld.BeginHistory()` once worldgen finishes.** It snapshots the
  baseline every reconstruction replays from. Without it a world plays but
  cannot be scrubbed back.
- **Advance the clock before writing voxels.** Snapshots are end-of-tick, so a
  write on an already-snapshotted tick is refused; `VoxelWorld.Set` validates
  before it touches the store, so a refused write leaves no trace.
- **World digests compare contents, not allocation.** A chunk emptied back to
  air is freed. Two worlds with the same voxels digest the same.

## Numerics: decided

**`double` under guard-enforced discipline**, not fixed-point. Recorded in
CLAUDE.md under L2 and enforced by `Tools/check-laws.sh`.

The four basic operations and `Sqrt` are IEEE-754 correctly rounded and give
bit-identical results in CoreCLR, Mono and IL2CPP. Only the transcendentals
vary, because `System.Math` forwards those to the platform's libm — so those
are banned inside `Assets/Sim` and `Core.SimMath` reimplements them from
`+ - * /` and `Sqrt` alone, verified against `System.Math` to 1e-12.

Fixed-point was rejected as a tax on every gene value, utility score,
influence map and site weight in the game, for certainty the narrow ban
already buys.
