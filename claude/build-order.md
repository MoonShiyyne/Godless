# Godless v2 build order

Derived from the Godless v2 Plan artifact
(https://claude.ai/artifact/3Mi5ZTo4XNKAFj8nNAdkrt), which is the source of
truth. When they disagree, the plan wins and this file is re-synced.

v1 — the culture-genome god game with the Silence, strata 0-2 — is archived
whole on tag `v1-archive` and branch `archive/v1` (e02c36b). Its build order,
with every "what shipped" section and the rules learned, is in that tag at
`claude/build-order.md`.

## The vision in one line

WorldBox in 3D: a god's sandbox where civilisations rise and fall, and where
the land itself records them — dug, terraced, walled, paved and abandoned by
the people who lived on it.

## How a milestone is judged

A milestone is done when its gate passes in `sim eval` (law L6), not when its
code is written. Each milestone adds its goals to `Sim.Headless/Eval.cs`: a
measurable sentence, a threshold, and the number it measured. `sim eval` exits
1 on any miss. Every system also ships an invariant (L7) and a feed line when
it makes something worth seeing (the "tell the player" rule).

## Milestones

| | Milestone | Gate |
|---|---|---|
| **M0** | Reset: v1 foundations kept, the three clocks, the command queue, the event feed, `sim eval` | An empty world at 60 fps; a god power lands in under a second; eval runs |
| M1 | Life: creatures and people wander, eat, breed, age, die, fight; first god powers (spawn, smite, bless, curse, raise, lower, water, fire, rain) | 1,000 units at 60 fps; a spawned band settles or dies out within 5 minutes |
| M2 | Villages: settle, gather, grow building by building from the grammar | A village of 100 within 10 minutes at 1x on every map; no project stuck past a year |
| M3 | Earthworks: quarries, mines, cleared forest, terraces, irrigation, roads worn then paved, bridges, harbours, dams, erosion | After 20 minutes a stranger can point to five kinds of human-made landform; every moved voxel has a cause |
| M4 | Kingdoms: cities, capitals, borders, roads, trade, diplomacy, war, sieges | Wars start and end without the player; borders move; walls rise where wars were fought |
| M5 | Rise and fall: cohesion, wealth and stress cycles; famine, plague, rebellion, succession; collapse, ruins, reoccupation | In an unattended hour at least one kingdom rises and falls on 80% of seeds, and ruins are reoccupied |
| M6 | Culture and ages: cultures, religions, languages as objects that spread and split; technology ages; architecture diverges | A stranger tells two cultures' cities apart 85%+ of the time |
| M7 | The god's toolbox and the game: disasters, powers, world laws, inspect-anything, saves, scenarios | The four-minute test: something worth watching or doing every minute |

## Current position

**M0 done** (see below). **Next: M1, Life.**

### What M0 shipped

- **Kept from v1:** the determinism core, the content pipeline, the voxel
  chunk store and detail cells, the delta log and timeline scrub, the annals,
  world generation with rivers and the five maps (plus the per-map island
  limits fix), the parcel grid and ground refresh, the building grammar,
  realizer, tiles, silhouettes and terrain negotiation, the genome, the
  harness, the mesher and camera.
- **Archived:** every settlement, needs, job, farm, store, hauling, town and
  commons system (about 8,000 lines), the building-project pipeline tied to
  them (intents, siting, construction, collapse, furnishing), and their
  content and tests. Materials moved to `Assets/Sim/Economy`.
- **Three clocks** (`Harness/TimeRules.cs`, `Content/base/time.json`): a step
  is a day; months of 30 and years of 360 steps are the calendar; politics is
  yearly. 10 steps a real second at 1x, so a year is 36 s.
- **One door for the god** (`Harness/Commands.cs`, `World/TerrainCommands.cs`):
  powers land at the start of the next step, before any system runs, or at
  once while paused; everything applied is kept in order for replay. The
  terrain brush goes through it.
- **The event feed** (`Chronicle/EventFeed.cs`, `Content/base/feed`): record
  kinds content names become lines for the player with a place to go; the
  Editor shows them bottom-left and flies the camera to one clicked.
- **`sim eval`**: the M0 gate — 6 goals, all met on green shore seed 7: a year
  in 36 s at 1x, an empty world stepping in well under 2 ms, a god power
  reaching the planning grid in one step (0.1 s at 1x), the feed telling it
  with its place, and the same acts giving the same world.

## Lessons carried from v1

Each became a law or a rule in CLAUDE.md.

- **Time to consequence (L8).** At two minutes a day the first farm took 68
  real hours; a single six-hour tick made watchable movement and a visible
  arc impossible together.
- **Liveness (L9).** A job board built once at founding stopped a town
  growing in year 8, and 400 passing tests did not notice.
- **Budget (L10).** Rich per-person routines cost 60 s per 20 years for 350
  people.
- **One door for the god.** The god's hills never reached the villagers'
  planning map, and each stroke skipped a tick nobody simulated.
- **Tell the player.** Deaths, feasts and departures were recorded and never
  shown.
- **Measure behaviour, not just code.** The behaviour check and gameplay
  review found more than the test suite; `sim eval` makes that permanent.
