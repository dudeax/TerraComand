# Terra Command

A tModLoader overhaul that plays Terraria with mechanics inspired by
[Cortex Command](https://github.com/cortex-command-community/Cortex-Command-Community-Project).
Vanilla progression, blocks and enemies stay; the rules change around them:

| Pillar | What it does now |
|---|---|
| **Pixel terrain** | Every solid block is 8×8 "art pixels" (one 64-bit mask per tile). Pickaxes carve the pixels nearest the cursor at vanilla's pace (each hit removes pick power % of the block, doubled on soft ground), explosions blow round craters, bullets/arrows/bolts chip where they hit. Dirt and sand erode fast, stone slower, bricks slowest. Vanilla pickaxe-power requirements and boss gating still apply. When enough of a tile is gone (80% by default), what's left crumbles. |
| **Multiple bodies** | Use a **Clone Body** to deploy an idle body. Press **Q** to take control of the body under the cursor, or cycle to the next one. Your current body, with its items and health, stays behind as an idle unit. Idle bodies hold position or follow you (**G**) and fight with whatever they're holding. If your body dies, it drops its gear and control jumps to your nearest idle body. You only truly die when you run out of bodies. |
| **Brain** | Your team's brain is a slow walking robot with a brain in a jar. It's a body like the others: you start in it, and you can leave it idle somewhere safe while you fight in a soldier. If it's destroyed, it's game over and the world is lost for your team. |
| **Ships & orbit** | Everything moves between the ground and orbit by **cargo ship**. In the orbital menu (**K**), pick cargo from the stockpile, or nothing for a pickup, then **Order ship** and click a landing zone. The ship flies down, lands and unloads; ordered Clone Bodies step out as soldiers. Throw items into a landed ship, or walk idle bodies into it, then **Launch ship**. When it leaves the atmosphere its cargo joins the stockpile shared by every player in the world. Enemies can shoot ships down, and the cargo is lost. From the menu you can also **craft** in orbit (a station counts as available when its item is in orbit, and buckets count as water/lava/honey). With the **Orbital Constructor**, blocks and walls are placed straight from orbit: right-click a block in the menu to select it. |

| **Cortex Command bodies** | The body you control is a CC actor, built from CC's own numbers (see `docs/cc-actor-design.md`). It has CC gravity (about a quarter of Terraria's). It walks on its legs slowly and steadily: 0.8 px/tick, or 1.6 running. Jumping is the **jetpack**: a burst on press, then thrust that weakens as the 1.5 s tank drains and refills when you stop. The torso leans while walking and bends forward when crouched. Going prone dives you onto your belly to crawl. Moving too fast (thrown by a blast, falling about 20 tiles) throws the body off its feet. It then ragdolls, takes impact damage when it hits something, and gets back up after a second. A fall of about 32 tiles is lethal. Wings, rocket boots and extra jumps add jetpack fuel and thrust. Hooks still work; mounts and dashes are disabled. Collision is per pixel. |
| **Aiming & damage** | Guns, bows and magic have spread that depends on stance: steady when crouched, wild when moving, airborne or using the jetpack. Recoil climbs under sustained fire, and the reticle shows current accuracy. Headshots do ×1.75. Hits leave visible wounds, which are cosmetic (no bleeding). Violent deaths gib. Bodies don't regenerate health naturally (there's a config option), but potions, buffs and accessories still heal. |

A new team starts as its brain. The first ship lands next to it with two soldier bodies, and new characters also get an Orbital Constructor.

### Controls
| Key | Action |
|---|---|
| A / D | walk |
| Shift (hold) | run |
| W or Space | jetpack: burst on press, thrust while held |
| Ctrl (hold) | crouch: lower, steadier aim; walking crouched is slower |
| S (hold) | prone: dive flat; A/D to crawl. Also drops through platforms |
| mouse | aim (the body turns to face it); reticle shows current spread |
| Q | switch to the body under the cursor, or cycle to the next one |
| G | toggle idle bodies between hold position and follow |
| K | orbital menu (stockpile, fabricate, order and launch ships) |

Body constants are in `TerraCommand/Common/Actors/CCActor.cs` (class `CC`), ported from the CC Community Project's data
files, with sources noted in `docs/cc-actor-design.md`. Spread, recoil and blast strength are still in
`TerraCommand/Common/Physics/Tuning.cs`. Crouch (Ctrl) and Run (Shift) can be rebound under Controls → Mod Controls.
Unbind Terraria's own Smart Cursor (Ctrl) and Auto Select (Shift) if they get in the way.

## Design decisions (easy to change)
- **Progression stays with the player, not the body.** Max life and mana from crystals and other permanent upgrades belong to the player. Each body only carries its items, its equipment and its current health.
- **Items must physically come down to be used.** Crafting and building happen in orbit, but weapons, armour and tools have to be delivered by ship. Ships are free for now. Charging coins from the stockpile (Cortex Command's "gold") is an obvious next step.
- **Units belong to a team.** Players on the same team (or everyone, with no team) share bodies.
- No Cortex Command assets, names or art are used. Its mechanics and numbers are ported (see `docs/cc-actor-design.md`), and all art is original, generated by `tools/gen_art.py`. Actors are drawn from per-part PNGs in `TerraCommand/Content/Actors/`.

## Building
```bash
tools/setup-toolchain.sh                    # .NET 8 SDK + tModLoader build in ./.tml (first time only)
cd TerraCommand && ~/.dotnet/dotnet build -c Release
```
The build compiles the mod and packages `TerraCommand.tmod` into `~/.local/share/Terraria/tModLoader/Mods/`.
That's the folder Steam tModLoader reads on Linux, so the mod appears under **Workshop → Manage Mods**.
For in-game rebuilds, symlink `TerraCommand/` into `~/.local/share/Terraria/tModLoader/ModSources/` and use **Develop Mods → Build**.

### Automated in-game testing
`tools/testenv/run.sh` runs the **real tModLoader client** on a virtual X display (Xvfb, software OpenGL, no audio
device) and drives it with a scenario script. The script runs through the mod's built-in test driver
(`Common/Testing/AutoTest.cs`), which only switches on when the `TC_AUTOTEST` environment variable is set:
```bash
tools/testenv/run.sh tools/testenv/scenarios --build             # whole suite, one game session (~40s)
tools/testenv/run.sh tools/testenv/scenarios/hardness.txt        # one scenario (~25s)
# summary:      .testenv/runs/latest/summary.txt        (exit code 0 = all passed)
# per scenario: .testenv/runs/latest/NN-name/results.txt + screenshots (captured in-process from the back buffer)
# logs:         .testenv/runs/latest/client.log
```
Every scenario gets a fresh test character and a fresh copy of a fixed-seed baseline world, which the dedicated
server generates once. Everything lives in a throwaway save directory (`.testenv/`), so real characters and worlds
are never touched. All scenarios share one game session: after each one the driver quits to the menu without
saving and loads the next.

Speed: about 16s of startup per session (Terraria loading its assets), then a few seconds per scenario. While a
scenario runs the driver fast-forwards: `TC_SPEED` game ticks per rendered frame (default 10), and it only draws one
frame in `TC_DRAW_EVERY` (default 30). The game logic still runs tick by tick, so results are unchanged. Before a
screenshot it renders `TC_SHOT_FRAMES` frames at normal speed (default 24) so tiles and lighting are current.

Requirements (paths overridable by env vars): Terraria + tModLoader installed side by side (`TML_DIR`, default
`/mnt/media/SteamLibrary/tModLoader`), plus `Xvfb` (`XVFB_ROOT`), `xdotool` (`XDOTOOL_ROOT`) and `ffmpeg`.
If `~/.steam/sdk64/steamclient.so` exists (link it to the one steamcmd ships), tModLoader starts its Steam API
without Steam running. Otherwise it shows a blocking "Steam Game Server failed" notice, and the script presses
"Continue" with `xdotool`.

Scenario commands, one per tick (tile offsets are relative to the tile under the player's feet):

| Command | Effect |
|---|---|
| `nospawn`, `goto spawn\|x y`, `flatten w h`, `wait n` | setup / timing |
| `give item [n]`, `clearinv`, `hold item`, `pickup off\|on` | inventory (items by vanilla name, ID, or mod item name) |
| `aim dx dy`, `aimtile dx dy`, `use ticks` | aim the cursor and hold the use button: really swings, places, mines |
| `drop item n dx dy`, `droptile item n dx dy`, `interact dx dy [item n]` | world items, right-clicking tiles |
| `stock item n`, `craft item [n]`, `buildkey item`, `menu` | orbital stockpile |
| `ship dx [item n]...`, `shiplaunch`, `shiphit dmg` | cargo ships (`expect ships\|shipcargo\|delivered op n`, `expect shipstate descending\|unloading\|waiting\|ascending`) |
| `claimbrain [starter]`, `kind brain\|soldier`, `killunit` | brain (`expect kind\|unitkind brain\|soldier`, `expect brainstate none\|alive\|lost`, `expect gameover yes\|no`) |
| `carve dx dy r [power]`, `settile dx dy TileName`, `projectile name dx dy vx vy`, `spawnnpc name dx dy` | terrain / combat |
| `switch`, `orders`, `hurt dmg` | bodies |
| `screenshot name`, `probe n`, `log text` | output (`probe` logs aim and item-use state for n ticks) |
| `expect stock\|inv item op n`, `expect units\|damaged\|ground\|life op n`, `expect projectiles name op n`, `expect npcs name op n`, `expect tile dx dy empty\|solid\|partial`, `expect wall dx dy`, `expect alive`, `expect menu open\|closed` | assertions (`op` is one of `>= <= > < == !=`) |

### Multiplayer testing
```bash
tools/testenv/run-mp.sh tools/testenv/mp/basic --build    # dedicated server + 2 clients (~3 min)
tools/testenv/run-mp.sh tools/testenv/mp/physics          # A's bomb throws B's body (~2 min)
```
An MP scenario directory holds `server.txt` and one script per client (`a.txt`, `b.txt`, ...):
- **Server:** the server loads a fresh copy of the baseline world and runs `server.txt` from its console with
  `tc script` (setup only, before anyone joins).
- **Clients:** each client gets its own save directory, character (`TCTestA`, ...) and virtual display, then joins
  over localhost.
- **Coordination:** clients wait for each other with `sync <label>` barriers. For example, A launches Wood, both
  sync, then B checks that it can see the Wood in the shared stockpile.
- **Speed:** multiplayer runs at normal game speed, without fast-forward.

### Testing without a game client
```bash
cd .tml && ~/.dotnet/dotnet tModLoader.dll -server -nosteam -config <serverconfig.txt>
# then in the server console:
tc selftest      # automated checks: carving, hardness, persistence, crafting, recipe groups, units, net serialization
```
In-game (singleplayer): `/tc give <itemId> <count>`, `/tc crater <radius>`, `/tc selftest`.

Known harness limitation: late in long single-session suites, screenshots are sometimes dark (stale
lighting after many world reloads). Results are unaffected; run that scenario on its own for a clean image.

## Code map
```
TerraCommand/
  Common/Actors/       CCActor: the Cortex Command body (states, gait/limb paths, jetpack, balance spring, stability, impacts)
                       ActorRig: draws the body as a CC rig (lean/tumble about the hip, head and arms on the aim, held item, jetpack)
  Common/Physics/      BodyPhysicsPlayer (hosts the CCActor on the player, disables vanilla movement), Explosions, Tuning
  Common/Destruction/  PixelTerrain (masks, carving, sync, save), TerrainRules (hardness/softness),
                       PixelTerrainGlobalTile (partial-tile drawing), DestructionGlobalProjectile, PixelMining
  Common/Orbital/      OrbitalStockpile (shared inventory), OrbitalCrafting, OrbitalNet (craft/build requests),
                       ShipSystem (ordering ships, cargo, unloading/loading, launch, delivery, landing-zone picking)
  Common/Units/BrainSystem  the team brain: claim on entering a world, game over and world lost when it dies
  Common/Units/        UnitLoadout (a body's items/health/looks), UnitSystem (spawn/swap/orders/save), UnitPlayer (controls, death transfer)
  Common/UI/           OrbitalUISystem (stockpile / fabricate / ship cargo menu)
  Common/Net/          PacketHandler; server is authoritative for terrain, stockpile and units
  Content/             CargoShip and CloneUnit NPCs, Clone Body, Orbital Constructor, actor part art (Content/Actors)
```

## Roadmap
1. **Play exactly like Cortex Command** (`docs/cc-actor-design.md`). Done: CC movement; original CC-style actors made of
   separate parts (IK legs on CC limb paths, arms on the weapon); the brain robot, whose loss ends the game; cargo ships to and
   from orbit. Next: CC controls and carried inventory (pie menu, pick up and drop),
   then CC devices (magazines, reload, sharp aim with camera look-ahead, diggers), then clone bodies on the same actor with
   limb gibbing.
2. Loose debris: carved pixels fall as particles and settle.
3. Smarter unit AI: dig, take cover, use ammo and mana, use any weapon properly. Direct per-unit orders via a selection box.
4. Coin-based ship and body costs (CC's gold), and buying from NPC shops through orbit.
5. Bosses and large enemies that tunnel or blast terrain. Gibs.
6. Per-corner lighting on carved tiles. Proper gib chunks (art) instead of particles.
7. Keep per-item mod data (`ModItem` fields) in the stockpile.

## License
AGPL-3.0-or-later. See [LICENSE](LICENSE).
