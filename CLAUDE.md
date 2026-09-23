# Godless (v2)

WorldBox in 3D. A voxel god sandbox where civilisations rise and fall, and
where the land records them: dug, terraced, walled, paved and abandoned by the
people who lived on it. Unity 6, solo build.

Three pillars. **Rise and fall**: kingdoms have life cycles — founding,
expansion, golden age, strain, crisis, fall — and ruins are reoccupied.
**Emergent terraforming**: only the god paints terrain; people dig, clear,
terrace, irrigate, pave and wall because they need to, and every voxel they
move says why. **Emergent construction**: buildings grow from need, culture,
material and technology age. The god touches everything — land and creatures.

**Design source of truth:** the Godless v2 Plan artifact
(https://claude.ai/artifact/3Mi5ZTo4XNKAFj8nNAdkrt). Milestones, gates and
the current queue: `claude/build-order.md`. v1 (the "act only on the world"
culture-genome game, with the Silence) is archived whole on tag `v1-archive`
and branch `archive/v1`; its design lives in the rev 7 design artifact.

## Laws

Violating one is grounds for rejecting a diff. `Tools/check-laws.sh` enforces
the mechanical half of L1 and L2; the rest is on you.

L1 LAYERING. `Assets/Unity` may reference `Assets/Sim`. `Assets/Sim` may
    never reference Unity. The asmdef sets `noEngineReferences: true`, so a
    violation is a compile error. Never remove that flag.
L2 DETERMINISM. All randomness through seeded per-system streams keyed by
    hashed STRING ids, never load-order indices. No `DateTime.Now`. No
    `GetHashCode` — it is randomised per process; use `Core.StableHash`. No
    unordered dictionary iteration in the sim. Same seed must give
    byte-identical annals.
    NUMERICS, decided: the sim core uses `double` under discipline, not
    fixed-point. The four basic operations and `Sqrt` are IEEE-754 correctly
    rounded and agree across CoreCLR, Mono and IL2CPP; the transcendentals
    do not, because `System.Math` forwards those to the platform libm. So
    `Math.Sin/Cos/Tan/Asin/Acos/Atan/Atan2/Sinh/Cosh/Tanh/Pow/Exp/Log/`
    `Log2/Log10/Cbrt` are banned inside `Assets/Sim` and the guard enforces
    it. Use `Core.SimMath`, which builds all of them from `+ - * /` and
    `Sqrt` alone. `Math.Sqrt/Abs/Floor/Ceiling/Round/Min/Max/Truncate/Sign`
    stay legal.
L3 PROVENANCE. Every voxel change, intent and gene mutation carries the
    event that caused it, from the first line that writes one. The chronicle,
    the terraforming record, kingdom histories, the feed and the timeline all read
    this. Retrofitting it across twenty systems is the single most expensive
    mistake available in this project.
L4 VISIBLE TELL. No system merges without one sentence answering "what does
    a stranger see?" If there is no answer, do not build it — cut it rather
    than tune it.
L5 CONTENT IS DATA. Tilesets, biomes, events, genes, grammar rules and
    scenarios load from `Assets/Content` through the mod pipeline. No
    privileged base-game code path, ever. Deleting `Assets/Content/base` must
    leave a game that boots with nothing to build.
L6 DEPTH ORDER. Build milestones in order (`claude/build-order.md`). A
    milestone is done when its gate passes in `sim eval`, not when its code
    is written.
L7 ONE ASSERTION. Every system ships with at least one invariant in the
    batch harness, over 200 seeds, not one.
L8 TIME TO CONSEQUENCE. What a system does must be visible to a watcher
    within minutes at 1x, and a god power within a second. v1's first farm
    took 68 real hours at 1x; nobody saw it happen.
L9 LIVENESS. Nothing waits for ever. Every job, project and plan is
    re-evaluated as the world changes, and the harness asserts progress —
    no project stuck past a year, no roofless town that never builds. v1's
    job board was built once at founding and a town stopped growing in year 8.
L10 BUDGET. Every per-unit cost is counted against 2,000+ units at 60 fps.
    Units are data, not objects; distant cities may run in aggregate. v1's
    350 people cost 60 s per 20 years.

## Rules the laws do not cover

- **The gene rule.** Every gene must change the silhouette at normal camera
  distance. If you cannot name what a stranger would see, cut the gene.
- **The modding rule.** All RNG streams seed from hashed stable string IDs,
  never load-order indices. No content outside `Assets/Content`.
- **One door for the god.** Every god power is an `IGodCommand` through
  `SimWorld.Commands`: it lands at the start of the next step, writes its own
  god.* record, and everything that reads the ground or the creatures hears of
  it that step. Nothing edits the world from the Unity side directly.
- **Tell the player.** A system that makes something happen worth seeing
  gives it a feed line (`Assets/Content/base/feed`) with a place to go.
- **No model in the tick.** Use a model at design time to author rule sets,
  and optionally out-of-band for chronicle prose. Nothing in the simulation
  tick calls one. Nondeterminism there breaks saves, replays and repro.

## Layout

    Assets/Sim/         plain C#, no UnityEngine, the whole game
      Core/             clock, seeded streams, stable hash, SimMath, digests
      Content/          the mod pipeline: JSON with comments, patches, load order
      Voxels/           chunk store, detail cells, raycast
      Deltas/           voxel delta log, snapshots, seek and replay
      Annals/           deterministic event record with causes
      Chronicle/        the player's side of the record: the event feed
      Harness/          SimWorld, time rules, the command queue, batch runner, invariants, pacer
      World/            island, biomes, rivers, parcel grid, influence maps, the god's terrain hand
      Economy/          materials, what the land gives, stock
      Build/            building grammar, realizer, tiles, silhouettes, terrain negotiation
      Culture/          genome and gene tells (cultures, religions and languages from M6)
      Life/             species, creatures as data, bands and camps, grazing, the god's powers over life
    Assets/Unity/       rendering, meshing jobs, input, UI
    Assets/Content/     base game shipped AS MODS
    Sim/                library csproj — compiles Assets/Sim for tooling
    Sim.Headless/       headless CLI runner, including `sim eval`
    Sim.Tests/          xUnit + the batch harness
    Tools/              law guard, verify

New folders arrive with their milestone: settlements (M2),
earthworks (M3), polities (M4), and so on; see `claude/build-order.md`.

## Unity rules

- When an Editor is connected, drive it with `unity command`. Never hand-edit
  `.unity`, `.prefab` or `.asset` files.
- `.meta` files always travel with their asset in the same commit.
- Never commit `Library/`, `Temp/`, `Logs/`, or any `bin/` or `obj/`.
- **C# 9 only in `Assets/Sim`.** The Editor rejects anything newer even when
  `dotnet` accepts it. `Sim/Godless.Sim.csproj` pins `LangVersion 9.0` and
  targets `netstandard2.1` to match. `Sim.Tests` and `Sim.Headless` target
  `net10.0` and are free to use modern C#.
- Do not retarget any project to `net8.0`. This machine has SDK 10 and no
  .NET 8 runtime, so a `net8.0` project builds clean and then dies at
  `dotnet run` — the failure surfaces a step later wearing a different mask.

## Maps are content

A world is a document in `Assets/Content/base/worlds`: falloff centres, the
elevation curve, terrace step, moisture bias, river flow, how much of the map
should be land, and which biomes it admits. `WorldChoice.Pick(content, name)`
is the only way to start one — it returns the preset *and* the biome subset
together, because a map's biome list decides the biome indices the island
stores, so taking one without the other generates a world that loads back as a
different one. `sim maps` lists them; `--map <id>` picks one on run, island,
parcels, separate and eval; the Editor picks one in the inspector; a save
records its name.

Two rules that keep falling out of this:

- **Sea level is per island** (`IslandMap.SeaLevel`), not a constant. Anything
  reading `IslandMap.DefaultSeaLevel` outside a preset is a bug waiting for
  someone to play the delta.
- **Numbers that describe a place belong to the place.** "An island covers
  8% to 75% of the map" was a generator constant and became wrong the day a
  delta existed; it is now `land.least`/`land.most` in each world document and
  the S09 invariant reads it. Prefer moving a threshold into content over
  widening it until every map fits. The same went for the biome-dominance
  cap (`dominantBiome.most`, default 0.9) and the lake cap the S0B check
  reads (`water.maxLakeDepth`). Fit a map's numbers to `sim run --island
  --map M` at 200 seeds, not 10: at 10, three of five maps looked clean.

## Speed is a display decision

A tick is a step, and at v2's default a step is a day (`time.json`): people
act a step at a time, the calendar is months and years of steps, and politics
is weighed once a year. At 1x ten steps run a real second, so a year takes
36 s. The player can pause and speed up (`Assets/Sim/Harness/TickPacer.cs`,
driven by `Assets/Unity/Input/SimSpeed.cs`). None of it reaches the
simulation: a tick is a tick, and the pacer only decides how many whole ticks
are due this frame. Same seed plus same tick count gives the same world,
however those ticks were spread — `HowTheTicksAreSpreadCannotChangeTheWorld`
holds that line, and the L1 guard makes it hard to break, since `Assets/Sim`
cannot see `Time.deltaTime` at all.

So: never pass a speed, a frame time or a real duration into a system. A
faster speed is added by extending `TickPacer.Multipliers` and nothing else.
Anything that needs to *happen* faster or slower belongs in the clock's ticks,
not in the pacer.

## The loop

    Tools/verify.sh         # guard + full suite, one exit code: commit only on this
    Tools/check-laws.sh     # L1 + L2 guard on its own
    dotnet test             # sim logic, no Editor needed
    unity command recompile # only when touching Assets/Unity
    unity status            # "ready" means the Editor is reachable

Commit as `Tools/verify.sh && git commit ...` so a failing run cannot land.
No wall-clock assertions in Sim.Tests: the suite runs in parallel and the
same run has measured 15 s and 95 s, so a time limit measures machine load.
Report timings through the test output or a `sim` command instead.

The headless harness (S08) is the thing that lets you iterate without a
human looking at a screen. Build once, then:

    dotnet build Sim.Headless -c Release
    Sim.Headless/bin/Release/net10.0/sim verify   # same seed twice, byte-identical
    Sim.Headless/bin/Release/net10.0/sim run      # 200 seeds x 300 years, all invariants
    Sim.Headless/bin/Release/net10.0/sim content  # what Assets/Content actually loads
    Sim.Headless/bin/Release/net10.0/sim eval     # the game against each milestone's goals

`sim eval` is how a milestone is judged (L6): each milestone adds goals — a
measurable sentence and a threshold — and it exits 1 on any miss. It exists
because v1's behaviour check found a town that stopped building in year 8
that 400 passing tests had not.

Do not use `dotnet run -v q -- <cmd>`; the -v flag eats the app's arguments.
Every system you add owes an entry in StandardInvariants, owned by its
milestone. That is law L7 and `sim run` prints what it checked.

If `unity status` or `unity command` will not connect, the Editor is almost
certainly in Safe Mode from a compile error. Run `unity pipeline list` to
confirm, fix the errors, restart Unity. Do not fall back to editing scene
files by hand.

## Working agreement

- One commit per system, message naming the milestone (`M1: ...`).
  When something breaks later, the bisect is free.
- Never start a session on a dirty tree.
- Name the milestone and system when starting work, so a session does not
  drift into whatever seems interesting.
