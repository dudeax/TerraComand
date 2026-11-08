# Cortex Command actors in Terraria — design

Goal: the bodies players control play **1:1 like Cortex Command actors** (AHuman); only the world (terrain, progression, enemies)
is Terraria's. Playtest feedback (2026-11-08): movement/jumping, look/animation and item use all still felt like Terraria.

Source of truth for behaviour: the CC Community Project (AGPL-3.0, same as this mod), cloned for reference at
`/mnt/media/cc-reference/src` (not part of this repo). Files cited below are relative to that.

## Units

CC simulates at 60 Hz (`c_DefaultDeltaTimeS = 0.0166666`, `Source/System/Constants.h`) with 20 px per metre (`c_PPM`).
Terraria also ticks at 60 Hz and its player (42 px) is about as tall as a CC human, so CC pixels map 1:1 onto Terraria pixels.

| CC | Terraria (per tick) |
|---|---|
| 1 m/s | 1/3 px/tick |
| 1 m/s² | 1/180 px/tick² |
| scene gravity 20 m/s² (`Scene.cpp`, default `GlobalAcc = (0, 20)`) | **0.111 px/tick²** (Terraria: 0.4) |

## Soldier Light (Coalition) reference actor

`Data/Coalition.rte/Actors/Infantry/CoalitionLight/CoalitionLight.ini`, paths and jetpack in `Data/Base.rte/Actors/Shared.ini`.

- Masses (kg): torso 48, head 32, helmet 4, arm 8 ×2, leg 12 ×2, foot 4 ×2 → ~140 kg plus jetpack and gun (assault rifle 11 kg).
- Health 100. `ImpulseDamageThreshold` 2400. Head `GibImpulseLimit` 640 / `GibWoundLimit` 9; torso wound limit in its own block.
- `StableVelocityThreshold` default (15, 25) m/s = (5, 8.3) px/tick: above this the actor goes UNSTABLE (ragdoll) and only
  recovers after `StableRecoveryDelay` 1000 ms below it (`Actor.cpp` ~1185).
- Leg: max reach 15 px extended, 7.5 contracted, hip offset ±(5, 2)-ish; arm max length 14, `MoveSpeed` 0.2.
- `AimDistance` 30, `SharpAimDelay` 250 ms, `AimRange` π/2 (±90° from horizontal).
- Lean targets (rad): Walk −0.1, Run −0.12, Crouch −0.8 (default `π/4·0.85`), body lean is reduced as the aim rises.

### Limb paths (foot travel speed, m/s → px/tick; push force N)

| Path | TravelSpeed | px/tick | PushForce |
|---|---|---|---|
| Stand | 0.7 | 0.23 | 4200 |
| Walk | 2.5 | 0.83 | 4400 |
| Run (MOVE_FAST) | 4.7 | 1.57 | 4900 |
| Crouch | 0.5 | 0.17 | 4000 |
| Crawl | 1.8 | 0.6 | 4200 |
| Arm crawl | 1.5 | 0.5 | 3400 |
| Climb | 1.5 | 0.5 | 3400 |
| Jump | 8 | 2.67 | 10000 |

Walking (`AHuman::PreControllerUpdate`, `AtomGroup::PushAsLimb`): each foot follows its path (a list of segment vectors relative to
the hip, rotated by the terrain slope found by two down-rays ±10 px from the hip, clamped to ±40°). The two legs alternate: one
leg's path may only restart when the other is past half-way. A foot that can't move into terrain pushes the body instead, with
force capped at `PushForce`; pushes against the travel direction are cut to 30 %. Travel speed is scaled down when the body is
slow (`UpdateLimbPathSpeed`: lerp from `2 m/s ÷ max` at standstill to 1 at full speed; stride length ×0.8→1.2). Crouching while
walking scales speed ×0.7 (`CrouchWalkSpeedMultiplier`). Auto-crouch: a ray above the head lowers the hips under low ceilings.

### Jetpack (`Source/Entities/AEJetpack.cpp`, "Jetpack Heavy" as fitted to Soldier Light)

- Thrust comes from emitted particles: 10 800 / min × mass 1.25 × avg 18 m/s ≈ **4 050 N**, scaled by a throttle that follows
  the fuel ratio: ×1.3 full → ×0.7 empty (`SetThrottle(2·ratio − 1)`).
  Against ~150 kg × 20 m/s² ≈ 3 000 N weight, that's +7 m/s² full, roughly hover when low.
- **No hop**: pressing jump fires a *burst* (BurstSize 8, BurstScale 2 → a kick of ~2.5 m/s ≈ 0.8 px/tick) and switches to
  JUMP movement state; holding keeps thrusting.
- Fuel: `JumpTime` 1.5 s of thrust; recharge ×1.5 real time when not thrusting (`JumpReplenishRate`); the burst costs
  `max(burst,2)` frames of fuel; won't *start* below 250 ms of fuel unless it was already firing.
- Nozzle angle: up to `π/2 × JumpAngleRange` (0.16 → ~14°) off vertical, steered by aim (aiming up tilts less, aiming down half)
  and flipped when moving against the facing.

### Body rotation and stability (`AHuman::Update` ~2540–2630)

The torso is a rigid body with an angle and angular velocity. While STABLE a spring pulls it to the lean target:
`angVel = angVel·(0.98 − 0.06·health/maxHealth) − 0.5·(rot − target)`; past 0.75π it snaps (a flip). Prone: spring to ±π/2
(`24·dt·diff` for 333 ms, then holds flat with `39·dt·diff`). UNSTABLE: falls over to ±π/2 (forward if moving) with
`3·dt·diff`. Dying: topples within 125 ms then DEAD. Upside-down (|rot| > π) forces UNSTABLE.

### Aim and sharp aim

Mouse aim sets the aim angle directly (clamped ±π/2 relative to the facing); aiming behind flips the body. Holding sharp aim
(RMB / Shift) while STABLE, slowish (< 5 m/s) and in Stand/Crouch/Prone/Walk ramps `SharpAimProgress` toward 1 (×0.3 while
walking) after `SharpAimDelay`; released, it decays (×0.95 − 0.1 per tick). The view point extends along the aim by the device's
`SharpLength` × progress (assault rifle 250 px). Firearm spread per round:
`(ShakeRange − (ShakeRange − SharpShakeRange)·sharpAim) · (supported ? 1 : NoSupportFactor) · gaussian` degrees, `ShakeRange`
and `SharpShakeRange` halved on load (assault rifle: 5 → 2.5°, 3 → 1.5°, no support ×2). Rounds fly from the muzzle.

### Default controls (keyboard + mouse player)

A/D move, W (or Space) jump/jetpack, S crouch, S while moving/holding → prone, Shift run (MOVE_FAST), mouse aim, LMB fire,
RMB sharp aim, R reload, Q/E previous/next inventory item, F pick up, G drop, hold a key for the pie menu, Ctrl switch actor.

## Architecture in tModLoader

- `Common/Actors/CCActor` — the actor simulation (state + one `Update(controller)` per tick), shared by the player's body and
  idle clone bodies (NPCs). State: velocity (px/tick), rotation + angular velocity, status (Stable/Unstable/Dying/Dead),
  movement state, prone state, crouch amount, flip, aim angle, sharp-aim progress, jetpack fuel, two leg gaits (CC limb paths),
  hand targets. Numbers ported from the tables above.
- Player integration — `SetControls` reads raw input into a `CCController` and **clears the vanilla controls**, so vanilla only
  runs collision (gravity 0, run accel/slowdown 0, no jump). Our physics sets `velocity`; vanilla + `PixelCollision` move the
  hitbox; impacts are read back afterwards. The hitbox follows the posture (standing 42, crouched, prone 20).
- Rendering — every vanilla player draw layer is hidden; one layer draws the rig: torso, head (+hair/helmet), two arms, two legs,
  the held item, cut from the character's own Terraria skin/armor textures so equipment still shows.
- Items — hotbar replaced by a small carried inventory (CC: whatever you can carry; we start with ~4 slots). Ranged weapons
  become CC firearms (magazine = ammo per reload, reload time, rate of fire from use time, shake/sharp shake, rounds from the
  muzzle); pickaxes/drills become diggers; melee items swing in a short arc; consumables (potions) are used from the pie menu.
- Clone bodies (NPC) run the same `CCActor`, with AI filling the controller.

## Phases

1. **Movement core**: `CCActor` physics (gravity, gait-driven walking/running/crouch/crawl, burst + thrust jetpack with
   CC fuel, rotation spring, stable/unstable ragdoll, impacts), wired into the player with vanilla movement disabled. Scenario
   tests for speeds, jet height, fuel, falling over. A debug stick-figure rig draw so motion is visible.
2. **Rig rendering** from Terraria textures (legs on the gait, arm + item on the aim, head looking at aim, lean, prone, ragdoll).
   *Done* (`Common/Actors/ActorRig.cs`). Vanilla still builds the player's draw data, so skin, armor, vanity and dyes all show. Each
   piece is then classified by texture (head / legs / upper body) and re-posed:
   - **Torso:** rotates about the hip (frame row 42) with the actor's rotation (lean, crouch bend, prone, tumble).
   - **Head:** turns toward the aim about the neck.
   - **Arms:** both composite arms hold the aim; they hang limp while thrown.
   - **Held item:** drawn in the front hand along the aim at all times.
   - **Legs:** pivot on the hip, squash when crouched, and lie along the body when prone or thrown.
   - **Jetpack:** a sprite on the back; the exhaust comes from its nozzle.
   Deviation from CC: Terraria's legs are one 4-art-pixel-wide sprite holding both legs, too small to split into two IK limbs
   that read well. Instead, Terraria's 14-frame walk cycle is advanced by the ground actually covered (3.6 px per frame, about
   one CC stride cycle), so the feet don't slide.
3. **Controls + inventory**: CC key scheme, carried items instead of the hotbar, Q/E switch, F/G pick up and drop, pie menu.
4. **Devices**: firearms (magazines, reload, sharp aim + look-ahead camera, recoil), diggers, melee, throwables.
5. **Clone bodies** on the same actor + AI; gibbing into limbs; multiplayer sync of actor state.
