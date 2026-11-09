# Actor physics parity work (v0.8.0 alpha)

The reference is the local CC Community source at commit `cca55f154ba49a17436cf91d91e1302fe45395db`. The canonical soldier is Coalition's `Soldier Light`. This pass targets character simulation and posing, not weapons or campaign content.

## Corrected against the reference

| System | Previous behaviour | Current behaviour / source |
|---|---|---|
| Unarmed soldier mass | Estimated 140 kg | 133 kg: torso 48, head 32, helmet 4, arms 2×8, legs 2×12, feet 2×4, jetpack 1. `CoalitionLight.ini`, `Shared.ini`; `MOSRotating::GetMass` includes attached parts. |
| Soldier jetpack | Generic Heavy preset, 4050 N | Resolve the soldier override of 12000 particles/min across three emissions: mean force 4208.33 N. |
| Burst | Flat 360 Ns kick every press | Nine emitted particles (ceil(8/3) in each emission), mean extra impulse 378.75 Ns, 160 ms spacing. Burst scale also applies to that tick's continuous emission. |
| Burst fuel | Eight ticks, same on every press | Nine ticks plus one emission tick; rejected bursts use half the burst fuel. Throttle is sampled before fuel is consumed. |
| Sharp aim | Immediate convergence | 250 ms settling delay and source convergence/revert rules. Reloading holds progress and the settling timer at zero. |
| Balance spring | Always full-health damping | Damping follows current health/max health, for player and NPC actors. |
| Humanoid strides | Physics and rendering used different clocks | Render the simulated limb-path phase and the source speed-dependent X/Y stride scale. Remote actors receive gait and contact snapshots. |
| Crawling | Leg propulsion only; arms hung at the sides | Add `Human Arm Crawl Path` (1.5 m/s, 3400 N per supporting arm). Rear arm participates; front arm participates when not holding a device. Render those strokes. |
| Ground angle | Legs always posed vertically | Two terrain probes either side of the body orient the leg frame, with CC's ±40° clamp. |
| Posture collision | Player narrowed when prone; NPC stayed standing | Shared posture metadata for combat/UI; normal terrain movement uses rotated body atoms. Auto-crouch probes head clearance. |
| Control handoff | Limb phases and some timers reset | Preserve gait, prone transition timer and burst cooldown with the body. v0.7.0's saved physical snapshots still load. |

Jetpack inheritance matters. `Data/Base.rte/Actors/Shared.ini` defines three emissions: Heavy changes the first to 10–26 m/s, while the inherited second remains 12–23 and the smoke remains 5–25. Each has mass 1.25 kg. `CoalitionLight.ini` overrides the total rate to 12000/min. `AEmitter::ReadProperty` divides that rate among emissions and rounds each emission's burst count upward. `AEJetpack::Burst` uses the resulting total burst count for fuel. The old constants skipped those inheritance steps.

Source files:

- `Data/Coalition.rte/Actors/Infantry/CoalitionLight/CoalitionLight.ini`
- `Data/Base.rte/Actors/Shared.ini`, `Data/Base.rte/Effects.ini`
- `Source/Entities/AEmitter.cpp`: property resolution, emission and burst scaling
- `Source/Entities/AEJetpack.cpp`: fuel, restart threshold, burst spacing integration
- `Source/Entities/AHuman.cpp`: limb speeds/scales, crawl arms, sharp aim, balance
- `Source/Entities/Actor.cpp`: stability and travel-impulse damage

## Contact solver

Controlled players and idle clone bodies now use `ActorContacts` for ordinary terrain movement. Terraria's rectangle stepping and slope correction are bypassed for these actors; the rectangle remains combat and UI metadata. Grappling and ship piloting retain the compatibility path.

- Generated 2 px alpha samples follow the existing torso, head and pack sprites. Attached limb artwork does not collide with terrain, matching `Arm::Clear` and `Leg::Clear`. Translation and rotation are swept together against remaining terrain pixels, slopes, half blocks and one-way platforms.
- Simultaneous contacts distribute normal and friction impulses using mass, rotational inertia and contact leverage. The solver advances to the free side of a collision and resolves the remaining time rather than discarding a whole movement tick.
- Terrain-constrained hand and foot tips drive force-limited reactions at their joints. Motor contact follows `PushTravel`'s force-capped velocity and proportional material friction; static feet use its 20 ms correction rule and source travel-speed cap. Upright locomotion disables rotational leverage, as `AHuman` does; crawling enables it. Stride progress follows actual tip position and segment targets, including the source's collision-disabled return segments and vertical-speed adjustment.
- Collision sampling and rendering share the same limb endpoints. Physical hands also anchor held devices and pickup interactions.
- Integration uses a local coordinate frame to preserve small movements far from the world origin. Bounded recovery releases grips or bodies enclosed by newly placed terrain.
- Gait, rotation, velocity and contact anchors survive network updates and control handoff. New snapshot fields are decoded before ignored actor updates return. Legacy saved body snapshots remain readable.

Relevant additional source: `Source/Entities/AtomGroup.cpp` (`Travel`, `PushAsLimb`), `Atom.cpp`, and `LimbPath.cpp` (`GetCurrentVel`, `ReportProgress`, `RestartFree`). The generated masks can be reproduced with `python3 tools/gen_collision_masks.py`.

## Remaining differences

This is **not yet 1:1 with the CC engine**:

- Terrain contacts use sampled imported CC sprites with adapted mod proportions and joint offsets, rather than exact source atom groups (see [art import](cc-art.md)). Motor tips are swept points; full foot atom groups remain outstanding.
- The body is a compound rigid simulation with posed limbs. Independent attachment masses, breakable living joints, wounds, material penetration and dislodging/climbing behavior are not fully ported. Corpse and gib physics still use the existing separate simulation.
- Surface friction/restitution map Terraria materials to mod coefficients. They are not the complete CC material database.
- Jetpack thrust uses deterministic mean particle momentum, rather than discrete particles with random spread and velocities.
- Light, Heavy, Crab, Turret and Brain remain the mod's existing variants. The resolved Coalition Soldier Light mass and jetpack apply to the canonical soldier.

## Verification

The standalone harness compiles production simulation and snapshot code directly against FNA:

```sh
PATH="$HOME/.dotnet:$PATH" dotnet run --project tools/physics-tests -c Release
```

It covers source constants, gravity, burst timing/fuel, aim/reload, health damping, gait and legacy snapshots, contact effective mass/torque, swept thin walls, angular collisions, one-way platforms, moving terrain, crawling, four-legged walking, backward bump climbing and recovery. All 57 standalone checks pass. These checks do not establish identical recorded trajectories in the two engines.

Gameplay verification uses `tools/testenv/run.sh`; multiplayer handoff uses `tools/testenv/run-mp.sh tools/testenv/mp/handoff`.

- Release compilation succeeds with zero errors and the four existing warnings.
- All 57 standalone checks pass, including resuming the next physical stride after control handoff.
- The full 26-scenario run at `.testenv/runs/20261108-234625-suite` produced 307 passing assertions and three failures. Focused reruns at `.testenv/runs/20261108-235003-suite` passed all 34 assertions in those three scenarios after correcting their assumptions. Together these cover 310 passing gameplay assertions across all 26 scenarios; this is not a single clean full-suite run.
- Actor movement (38 assertions), real contacts (16), terrain support (11), actor types, brain movement, unit orders and loaded-unit combat all have passing runs.

The scenario corrections matter: limb-path speed is not a prescribed body velocity; auto-crouching slows doorway traversal; the combat rectangle does not define tunnel clearance; physical strides can briefly lose support and incur CC's airborne spread multiplier. The enemy victory flag updates after the NPC death phase. A loaded 150.6 kg clone can exceed the 4800 Ns gib limit in a 25-tile fall, so the nonlethal loaded-body fixture uses 20 tiles. These bounds are regression checks, not recorded CC trajectory equivalence.

All multiplayer peers need the same build because actor packet layouts changed. The final dedicated server/two-client handoff run at `.testenv/runs/20261108-235106-mp-handoff` passed all 11 assertions in 92 seconds, including the restored-contact binding changes. No packet underflow, end-of-stream error, fatal error or segmentation fault appeared in this run. The headless environment still logged Steam initialization and `/proc` process-stat lookup warnings. An earlier run had a client segfault after its DONE marker; normal interactive shutdown remains unverified.

v0.8.0 is an alpha release of the physics and CC art passes. Manual comparison against recorded CC actor trajectories and interactive feel testing remain necessary before claiming 1:1 parity.

Release verification (2026-11-09): the packaged mod reports version 0.8.0 for tModLoader 2026.9.3.1. Release compilation and all 57 physics checks passed. The fresh two-client handoff run at `.testenv/runs/20261109-002153-mp-handoff` passed all 11 assertions with the imported art and AI changes. One client segfaulted after its DONE marker during teardown; no packet underflow, end-of-stream or fatal error appeared in the stdout logs.
