# Godless build order

Derived from the design doc (rev 7) and the build-order registry (v1, rev 5).
Full stage plan, with the reasoning and the gate failure actions:
https://claude.ai/code/artifact/0bcf773e-22cb-4314-b8a1-8e290edfe63b

The design doc is the source of truth. This file is derived. When they
disagree, the doc wins and this file gets re-synced.

## Current position

Stage 0 (repo setup): **done**. S00, S01, S02: **done**.

Next: **S05** (annalist schema) or **S03** (chunk store). S05 is pure schema
work and S04 writes into it; S03 is the longer pole and the critical path to
G1 runs through it. S06/S07 need a reachable Editor and are the only
stratum-0 systems that do.

Nothing in stratum 1 may start until S00–S09 and S0A–S0C are green. Each is a
decision the rest of the codebase encodes rather than calls.

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
Remaining for G0: the harness running unattended (S08).

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
