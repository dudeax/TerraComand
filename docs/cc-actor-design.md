# Cortex Command actors in Terraria — design

Current source audit and verification: [physics parity work](physics-parity.md). The historical phase notes below include superseded estimates and implementation plans.

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

## Round 2 (after playtesting v0.2.0): "literally play like Cortex Command"

Decisions (user, 2026-11-08):
- **Characters:** original CC-style actors made of separate parts. The art is ours (`tools/gen_art.py` →
  `Content/Actors/<Kind>/*.png`), not CC's; replace the PNGs to restyle. The Terraria character isn't drawn. Armor only gives
  stats.
- **Brain:** a walking brain robot, controllable like any body. If it's destroyed it's game over, and the world is lost for that
  team.
- **Ships:** ordered from the buy menu. A ship flies down, unloads, is loaded and sent back up to the stockpile, and can be
  shot down. Ships replace the launch pad and drop pods.

Status:
1. **Actor rig (done)** — `ActorRig.cs`:
   - `RigRenderer` poses parts with two-bone IK. Feet follow the CC walk/run/crawl limb paths (advanced by ground covered) and
     plant on the actual ground pixels. Arms reach the weapon's grip and fore-grip.
   - The torso leans and tumbles about the hip, the head looks toward the aim, and the jetpack sits on the back.
   - Player bodies (`RigPlayer.BodyLayer`, all vanilla layers hidden) and idle clone bodies (`CloneUnit.PreDraw`) share the
     renderer.
2. **Brain (done)** — `Units/BrainSystem.cs`:
   - A body's kind (`ActorKind`) travels with its loadout, so the brain can be controlled, or left idle as a unit.
   - The first player of a team to enter a world becomes its brain. The brain walks at 60 %, can't run, and has no jetpack.
   - A dead brain (controlled or idle) marks the team Lost (saved with the world). That team gets the game-over screen and is
     sent back to the menu, and re-entering shows it again.
3. **Ships (done)** — `Content/NPCs/CargoShip.cs`, `Common/Orbital/ShipSystem.cs`. Planned as:
   - A rocket ordered from the buy menu flies to a chosen landing spot, lands and opens.
   - It unloads ordered bodies and items. Cargo or bodies inside the bay go up when it's sent back to orbit, into the stockpile.
   - It has health and can crash. It replaces `LaunchPad`, `LaunchRocket` and `DropPod`.

## Controls and inventory (done, after v0.3.0)

The keys follow CC's mouse+WASD preset (`Source/System/InputScheme.cs`), plus Terraria's own keys where CC has no equivalent:

| Input | CC | Here |
|---|---|---|
| Fire | LMB | LMB (use the held item) |
| Pie menu | RMB (hold) | RMB (hold, point, release) |
| Previous / next item | 1 / 2, mouse wheel | 1 / 2, mouse wheel |
| Pick up | F | F (nothing in reach: interact with the tile under the cursor) |
| Drop | G | G |
| Reload | R | R (registered; firearms arrive with CC devices) |
| Actor switch | Q / E | Q (E stays Terraria's grapple) |

How the pieces map:
- **Inventory:** CC's inventory deque maps onto Terraria's slots. Slot 0 is the hands; slots 1–49 hold the queue of devices
  (weapons, tools, gadgets). Blocks, potions and materials are pack stock, used from the inventory cursor.
- **Pick-up rules:** equipment needs a deliberate pick-up. Loose stock is still grabbed by walking over it, because Terraria
  mining needs it.
- **Mass:** carried mass adds to the actor's mass (140 kg base), so legs and jetpack push against all of it, as in CC. Weapons
  weigh about 2–8 kg, armor 2 kg plus 0.6 × defense, materials 0.05 kg each.
- **Code:** `Common/Actors/ActorInventory.cs` and `Common/UI/ActorControlsUI.cs` (pie menu + HUD).

## Devices (done)

`Common/Combat/Devices.cs` replaces the old stance-based aiming. Terraria weapons become CC devices:

- **Firearms** (ammo-fed ranged weapons except bows): a magazine counted per item (`MagazineGlobalItem`, saved with the item).
  - Stats derived from use time: magazine 100 / 40 / 15 / 8 / 5 (shotguns 6, rocket launchers 1); reload 1.2–3 s.
  - Shake 5° / 3° / 1.6° / 0.8° (fast → slow), sharp shake ≈ 55 % of that; sharp-aim reach 140–420 px.
  - Spread per round = `(shake − (shake − sharpShake) · sharpAim) · (supported ? 1 : 2) · gaussian`, halved (CC halves
    `ShakeRange` on load). Supported means stable and on the ground or prone, not jetting.
  - An empty gun reloads by itself; R reloads early; reloading drops sharp aim and can't fire.
  - Rounds leave the muzzle along the aim. The aim is measured from the shoulder, so the muzzle line passes through the cursor.
  - Recoil pushes the body (scaled by its total mass) and jolts the gun back in the hands.
- **Camera:** it looks ahead along the aim by `30 + reach · sharpAim` px (CC `AimDistance` + `SharpLength`). A dotted sight line
  fans out with the spread.
- **Diggers** (pickaxes): they dig the first solid pixel along the aim within 56 px of the hands (the tile containing it), not
  the cursor tile.
- **Throwables** (consumable thrown items: grenades, bombs, dynamite): held to wind up over 0.75 s, thrown on release at
  2.5–9 px/tick (CC MinThrowVel..MaxThrowVel).
- Swords keep Terraria's swing; CC has no melee to port.

## Idle bodies and remains (done)

- **Idle bodies:** `CloneUnit` hosts the same `CCActor` as a player's body, server-authoritative. The NPC's velocity is the
  actor's, vanilla NPC collision plus step-up moves it, and impacts feed CC travel-impulse damage.
  - A simple AI fills an `ActorInput`. In Follow it walks after the nearest team player, runs when far, and jets when blocked for
    20 ticks or when the leader is 5+ tiles above. It aims at the nearest enemy in sight and fires once the aim is on target,
    from the muzzle with magazine, reload and shake (×2 airborne).
  - Clients receive status, prone, flip, rotation, aim and crouch in `SendExtraAI` whenever they change, for drawing.
  - Blasts throw units through `CCActor.Shove` and also damage them: a player's explosives can't hit "friendly" NPCs in vanilla.
- **Remains:** `GibSystem` (cosmetic, every client).
  - `Decide`: a blast gibs, a headshot kill decapitates, overkill (half max life in one hit) gibs, anything else leaves a
    corpse.
  - CC bodies gib into their actual rig pieces (`RigRenderer.Build` → tumbling, bleeding chunks).
  - Corpses are posed rigs that fall with CC gravity, topple flat, and fade after 40 s. Decapitated ones are headless, and the
    head flies off as a chunk.
  - Terraria enemies are cut from their sprite frame (vanilla gore is suppressed when they are).
  - Blood droplets splat on terrain; a splat drops off when its ground is destroyed.
  - A control transfer isn't a vanilla death, so its remains are broadcast (`PacketType.DeathFx`).

## Overnight round (v0.6.0): the rest of CC's systems

**Terrain.** Blast debris is simulated as grains and settles back into the terrain as real pixels: loose, partly filled
tiles that drop a proportional share of their item when mined. Mining debris is cosmetic, so it can't refill a hole or
duplicate material. Blood is drawn exactly where a drop meets solid pixels.

**Actor types** (`ActorStats` in `CCActor.cs`). The table holds mass, a health scale, jetpack tank and force, a speed
scale, whether the type can run, jet or crouch, whether it moves at all, and its stand height. The same CC actor runs them
all. The crab drone is drawn with four IK legs on the walk path, a quarter of a cycle apart. The turret is a base and a head
that swivels to the aim. Both carry their gun on a mount (`RigRenderer.GunAt`).

| Type | Mass | Health | Jet tank / force | Speed | Notes |
|---|---|---|---|---|---|
| Soldier | 140 | ×1 | 90 / 4050 | ×1 | |
| Light | 110 | ×0.8 | 110 / 2900 | ×1.15 | |
| Heavy | 200 | ×1.6 | 60 / 5800 | ×0.85 | |
| Crab drone | 120 | ×1.2 | none | ×0.9 | can't run or crouch, 22 px tall, mounted pistol |
| Turret | 300 | ×2 | none | none | 26 px tall, mounted rifle |
| Brain | 140 | ×1 | none | ×0.6 | can't run |

**Device systems** (`FirearmSpec`, `CCFirearm`, `CCThrowable`, `CCGrenade`). The spec covers RPM, full auto, bursts,
activation delay (spin-up), particle count and spread (shotguns, diggers), magazine, reload, shake, sharp shake, sharp length,
recoil, and the round. Grenades cover fuses, impact, sticky, remote detonation, shrapnel and bomblets. Each system has one
original example item (see the README).

**Economy.** Funds are the stockpile's coins. Spending makes change across platinum, gold, silver and copper. Catalogue
prices are the items' Terraria value, and selling pays the vanilla sell price (a fifth of value). Ship costs depend on the
craft. All of it is server-authoritative (Buy and Sell packets).

**Unit AI modes.** Each order computes a goal, and the shared walk and jet controller moves the body to it. Gold dig carves
along the aim at the digger's strength and rate; when the ore is out of reach it digs a shaft, alternating sides each bite,
and drops into it. The ore and gems it digs up go to the stockpile. Brain hunt targets enemy brains: other teams' brain
bodies, players' brains in PvP, and every player brain for the AI faction.

**Craft.** `ShipStyle` is one of Rocket, Dropship or Crate, set on the cargo ship NPC with its size, health, bay and art.
The dropship steers to its landing X as it descends. A crate falls under gravity, bursts open and is despawned after ten
seconds. A piloted ship (`ShipState.Piloted`) flies on server physics from the pilot's controls, which are captured from
the body's input. The body rides hidden inside the ship. A ship whose pilot leaves in the air lands itself.

**Enemy faction** (`EnemyFaction`). Team 6. Its bodies are hostile NPCs, so players' fire hits them. Its rounds are marked
hostile through a GlobalProjectile flag that is synced in the projectile's extra AI, so they hurt players and players'
bodies but pass through its own. It earns 1 gold a minute. Each wave buys a dropship plus 2–4 soldiers (light or standard
bodies, and heavies from wave 3), each armed from a widening gun list. The squad lands 30–45 tiles from the target, on the
bunker's side, and brain-hunts; the empty dropship goes back up. Hits from rounds now push a body by a small momentum
impulse instead of vanilla's fixed knockback, which the actor physics would have carried for tens of tiles.
