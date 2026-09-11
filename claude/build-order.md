# Godless build order

Derived from the design doc (rev 7) and the build-order registry (v1, rev 5).
Full stage plan, with the reasoning and the gate failure actions:
https://claude.ai/code/artifact/0bcf773e-22cb-4314-b8a1-8e290edfe63b

The design doc is the source of truth. This file is derived. When they
disagree, the doc wins and this file gets re-synced.

## Current position

**Stratum 0 is complete. G0 passed on 2026-09-11.** Next is stratum 1, which
ends at **G1 — the go/no-go gate that can cancel the project.**

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

Start with **S17** and **S10**: both depend only on finished work, and S17 is
the smallest thing that can make a silhouette respond to a number.

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

## The open stratum-0 decision

**Fixed-point, or float under strict discipline, in the sim core.** Part 23
requires one or the other and the doc never calls it. G0's byte-identical
annals cannot hold across machines until it is decided.

It did **not** block S01 — the tick is a counter and the streams produce
integers, so `Assets/Sim/Core` is representation-agnostic and `RngStream`
deliberately exposes no `NextDouble`. It binds at **S03** (chunk and column
data), **S10** (influence maps) and **S17** (gene values) — the first systems
that store a continuous quantity. Decide before S03.

Recommendation: **double, under discipline enforced by the guard.** In .NET
the four basic operations and `Sqrt` are IEEE-754 correctly rounded and agree
across CoreCLR, Mono and IL2CPP; what varies is the transcendentals, because
those come from the platform's libm. So the discipline is narrow and
greppable — ban `Math.Sin/Cos/Tan/Pow/Exp/Log` in `Assets/Sim`, provide
deterministic replacements in `Core/SimMath`, and add the ban to
`Tools/check-laws.sh` in the same commit. Fixed-point buys a little more
certainty for a tax on every gene, score and field in the game, which is the
wrong trade for a solo build. Not yet decided — this is a recommendation.
