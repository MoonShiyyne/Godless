# Godless build order

Derived from the design doc (rev 7) and the build-order registry (v1, rev 5).
Full stage plan, with the reasoning and the gate failure actions:
https://claude.ai/code/artifact/0bcf773e-22cb-4314-b8a1-8e290edfe63b

The design doc is the source of truth. This file is derived. When they
disagree, the doc wins and this file gets re-synced.

## Current position

Stage 0 (repo setup) is **done**. Stratum 0 has not started.

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

## Next action

Build **S01** — the fixed tick loop and the seeded RNG stream registry, in
`Assets/Sim`. Streams resolve by hashed string id, never by index. Write the
determinism test first in `Sim.Tests`: same seed, 300 ticks, identical output
sequence. Then make it pass.

An open stratum-0 decision blocks part of this: **fixed-point or strict float
discipline** in the sim core. Part 23 requires one or the other and the doc
never calls it. G0's byte-identical-annals requirement cannot hold across
machines until it is decided. Decide before S01 hardens the tick.
