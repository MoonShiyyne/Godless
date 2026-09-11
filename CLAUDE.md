# Godless

Emergent voxel civilization godsim. Unity 6, solo build.

The player is a god who acts only on the world and never on the agents.
Settlements build from instinct — a numeric culture genome bent by scars,
salience and myth. Every session ends in the Silence: powers withdrawn,
200 years unattended, scored on what of you is still legible.

**Design source of truth:** the `Godless` artifact, design & technical plan
rev 7, 27 parts. The build order and system registry are *derived* from it.
Stage plan and current queue: `claude/build-order.md`. Read it before starting
any system. Do not open a system whose dependencies are not done.

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
    the Silence scoring, the stratigraphic probe and the timelapse all read
    this. Retrofitting it across twenty systems is the single most expensive
    mistake available in this project.
L4 VISIBLE TELL. No system merges without one sentence answering "what does
    a stranger see?" If there is no answer, do not build it — cut it rather
    than tune it.
L5 CONTENT IS DATA. Tilesets, biomes, events, genes, grammar rules and
    scenarios load from `Assets/Content` through the mod pipeline. No
    privileged base-game code path, ever. Deleting `Assets/Content/base` must
    leave a game that boots with nothing to build.
L6 DEPTH ORDER. Check `depends_on` in the registry before starting. Strata
    are not a schedule you can compress by working ahead.
L7 ONE ASSERTION. From stratum 2 on, every system ships with at least one
    invariant in the batch harness, over 200 seeds, not one.

## Four rules the laws do not cover

- **The gene rule.** Every gene must change the silhouette at normal camera
  distance. If you cannot name what a stranger would see, cut the gene.
- **The modding rule.** All RNG streams seed from hashed stable string IDs,
  never load-order indices. No content outside `Assets/Content`.
- **The memory rule.** Scar, salience and myth are three separate layers and
  no code path may collapse them. A scar is felt and individual. Salience is
  collective attention held up by monuments. A myth is a transmissible account
  that outlives both — a settlement with zero drowning scars and no monument
  still builds high, because that is what the story says people do. Collapsing
  these destroys the central mechanic.
- **No model in the tick.** Use a model at design time to author rule sets,
  and optionally out-of-band for chronicle prose. Nothing in the simulation
  tick calls one. Nondeterminism there breaks saves, replays and repro.

## Layout

    Assets/Sim/         plain C#, no UnityEngine, the whole game
      Drives/           utility AI, needs
      Culture/          genome, mutation, inheritance, motifs, diffusion
      Collective/       quorum decisions, response thresholds, grievance
      Transmit/         myth themes, attractors, conformity, the floating gap
      Literacy/         script invention, textualization, canon and heresy
      Memory/           scars, salience, decay, inheritance, TraumaProfile
      Annals/           deterministic event record, era naming, place queries
      Deltas/           voxel delta log, snapshots, seek and replay
      Build/            intents, site scoring, grammar, WFC
      Chronicle/        provenance presentation over the annals
    Assets/Unity/       rendering, meshing jobs, input, UI
    Assets/Content/     base game shipped AS MODS
    Sim/                library csproj — compiles Assets/Sim for tooling
    Sim.Headless/       headless CLI runner (S08)
    Sim.Tests/          xUnit + the batch harness
    Tools/              law guard, screenshot harness, gene inspector

`Collective/`, `Transmit/` and `Literacy/` exist because design doc rev 7
added Parts 16, 09 and 10. Do not file that material under `Culture/`.

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

Do not use `dotnet run -v q -- <cmd>`; the -v flag eats the app's arguments.
Every system you add from stratum 2 on owes an entry in StandardInvariants,
owned by its registry id. That is law L7 and `sim run` prints what it checked.

If `unity status` or `unity command` will not connect, the Editor is almost
certainly in Safe Mode from a compile error. Run `unity pipeline list` to
confirm, fix the errors, restart Unity. Do not fall back to editing scene
files by hand.

## Working agreement

- One commit per system, message naming the registry id (`S01: ...`).
  When something breaks in stratum 3, the bisect is free.
- A branch per stratum. Never start a session on a dirty tree.
- Name the system by its registry id when starting work. That is what the ids
  are for, and it stops a session drifting into whatever seems interesting.
