# Conversion rescue: first playable slice

The historical notes below describe the first data port. The subsequent [CC art import](cc-art.md) replaces its sprite placeholders, and the [physics pass](physics-parity.md) replaces normal rectangle terrain movement.

The goal is a CC battlefield in a Terraria landscape. The previous default preserved Terraria progression and gave a new brain copper tools and two unarmed soldiers. The conversion now starts with a constructor and delivers two armed Coalition riflemen, a light digger and three frag grenades. Soldiers can fight immediately after switching into them. Buy replacement riflemen and weapons in orbit; all four Coalition guns refill their own magazines.

Full conversion is enabled by default in the server configuration. It suppresses ambient NPC spawning and removes vanilla starting items from newly created characters. Turning it off restores those two vanilla behaviours. Existing characters, NPCs and terrain are not erased. The enemy faction remains configurable separately; its bunker appears after 20 seconds and its first attack uses the existing configured timer (default ten minutes). You can attack the bunker before its first wave.

## Ported content

Source: local Cortex Command Community reference, commit `cca55f154ba49a17436cf91d91e1302fe45395db`, under `Data/Coalition.rte/`. Content and art are imported under the reference license; see the root license and `docs/cc-art-manifest.json` for asset provenance. Stats and visuals are adapted for Terraria.

| Item | Source INI | Rounds | RPM | Reload | Loaded mass | Sharp reach |
|---|---|---:|---:|---:|---:|---:|
| Coalition Pistol | `Pistol/Pistol.ini` | 10 | 300 | 1000 ms | 4 kg | 125 px |
| Coalition Assault Rifle | `AssaultRifle/AssaultRifle.ini` | 35 | 800 | 1800 ms | 13 kg | 250 px |
| Coalition Compact Assault Rifle | `CompactAssaultRifle/CompactAssaultRifle.ini` | 24 | 1000 | 1600 ms | 9 kg | 150 px |
| Coalition Sniper Rifle | `SniperRifle/SniperRifle.ini` | 8 | 150 | 2200 ms | 15 kg | 420 px |
| Coalition Auto Pistol | `AutoPistol/AutoPistol.ini` | 18 | 1200 | 1600 ms | compatible firearm |
| Coalition Heavy Pistol | `HeavyPistol/HeavyPistol.ini` | 8 | 300 | 1000 ms | compatible firearm |
| Coalition Shotgun | `Shotgun/Shotgun.ini` | 6 | 120 | 280 ms | compatible firearm |
| Coalition Auto Shotgun | `AutoShotgun/AutoShotgun.ini` | 10 | 280 | 2900 ms | compatible firearm |

Existing generic devices cover additional Coalition gear with matching mechanics: frag, incendiary-style impact and cluster grenades; timed and remote charges; combat shield; medikit; diggers; rotary gun, launcher and sawthrower. Coalition-specific actor art now includes light and heavy infantry, brain, Gatling drone, Blast Runner and turret assets where the importer can map them to a current rig.

## Deferred Coalition content and mechanics

The reference lists 12 infantry/weapon INIs plus explosive, shield and utility devices, and six actor presets (light/heavy infantry, Gatling drone, Blast Runner, Gatling turret and bunker cannon). Remaining firearm presets are Auto Cannon, Grenade Launcher, Missile Launcher and Uber Cannon. Their explosive, missile-guidance, sustained beam/energy, or programmable ammunition behavior needs dedicated projectile mechanics before these are represented as normal bullets. Matchshot-style launcher scripting and smart targeting are not faked as ordinary rounds.

Actors are not yet a complete port. Light and heavy infantry use existing human rigs with imported art; their CC armor-specific damage regions and loadout AI are unsupported. The Gatling Drone can use the existing crab body rig but still lacks its autonomous targeting and native turret weapon mount. Blast Runner depends on its scripted terrain-digging and jumping behavior and the current crab rig cannot represent its turret/two-leg assembly. Gatling turret and bunker cannon require mounted aiming, emplacement deployment and their specialized shells. Infantry detachments/wounds, armor penetration, autonomous crab AI, deployable emplacements, dual wield/reload, functioning shields, remote charge inventory links, timed explosive placement and distinct medikit behavior are also absent or only approximated by generic devices.

Mass includes the magazine. Assault rifle ammo inherits from the compact rifle; the assault rifle overrides velocity to 100 m/s. Speeds use the existing conversion of 20 pixels/metre at 60 ticks/second. Fire rates round to Terraria item-use ticks. Damage, recoil, projectile simulation, art and sound are engine adaptations, not exact CC ports. Prices use one silver per CC gold unit (10, 50, 30 and 70 respectively). The rifleman is an equipped loadout of the existing soldier rig, not a port of CC's infantry artwork.

## Physical control handoff

Switching control now transfers velocity, jetpack fuel, rotation, angular velocity, stance, facing and recovery state with the body. It previously zeroed the incoming body's velocity and retained the outgoing actor's transient state. Idle-body world saves and loadout packets now carry the physical snapshot. Old world saves without the snapshot load with a fresh body state; multiplayer peers must run the same mod version.

Menus and text entry consume movement and weapon input while simulation continues. In conversion mode, a command panel shows brain state, reserve actors, orbital funds and the bunker objective in place of vanilla resource bars and the minimap; the fullscreen map remains available.

Thrown CC devices require deliberate pickup, and equipment cannot be picked up through solid terrain. Shots converge from the muzzle onto the cursor (including prone fire), and a muzzle sweep prevents shooting through a wall just because the end of the gun reaches beyond it.

Opening a menu cancels an unfinished throw without consuming a grenade. Ship piloting receives neutral input while menus are open. Vanilla smart-select, smart-cursor, quick mount, quick-use and grapple actions are suppressed during actor play without editing saved control profiles.

## Still unfinished

This is a playable foundation, not a complete conversion of every CC faction. CC's atom-group rigid body solver, detachable living limbs, wound/penetration model, material simulation, full asset library, faction-specific actor rigs, craft and campaign activities remain substantial work. Terrain still uses the existing 2-pixel cells and actors still collide through Terraria hitboxes. Terraria crafting/progression remains available for compatibility; the starting loop no longer requires it.

Regression scenarios: `conversion.txt` checks actual starting inventory, armed delivery, magazine operation and menu input; `body-handoff.txt` checks stable/fresh versus knocked/fuel-depleted bodies and momentum across swaps. Existing actor, inventory, brain, ship, enemy and weapon scenarios cover the surrounding systems.

## Validation

Release build: `PATH="$HOME/.dotnet:$PATH" dotnet build TerraCommand -c Release` (four existing compiler/analyzer warnings, no errors). The packaged mod is installed into the normal tModLoader Mods directory as version 0.7.0.

The dedicated-server / two-client handoff scenario also passed (11 assertions), covering loadout packet decoding, returning to the original body, and another client taking the equipped soldier. Both headless clients emitted native segmentation faults during harness teardown after their DONE markers; gameplay assertions passed, but clean client shutdown is not verified. Server simulation continues during client delays, so short-lived fuel and recovery states are checked deterministically in single player.

All 25 single-player gameplay scenarios passed across the complete suite and focused reruns (288 assertions). The new starter supplies required updating ship-test setup to retrieve a recovered grenade stack before checking an empty hold; the delivery test counts the number of grenades in the stack.

Reproduce the focused checks with:

```sh
tools/testenv/run.sh tools/testenv/scenarios/conversion.txt tools/testenv/scenarios/body-handoff.txt tools/testenv/scenarios/ships.txt --build
tools/testenv/run-mp.sh tools/testenv/mp/handoff
```

Use a fresh character and world to see the new starting expedition. Existing characters keep their equipment; previously claimed worlds do not receive a duplicate starter ship.
