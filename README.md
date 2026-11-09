# Terra Command

Latest alpha: [v0.8.0](https://github.com/dudeax/TerraComand/releases/tag/v0.8.0) · [Download TerraCommand.tmod](https://github.com/dudeax/TerraComand/releases/download/v0.8.0/TerraCommand.tmod)

A tModLoader conversion toward [Cortex Command](https://github.com/cortex-command-community/Cortex-Command-Community-Project) in a Terraria landscape.
Full conversion is enabled by default: ambient Terraria spawns are suppressed and new characters start without copper tools.
The first ship brings two armed Coalition riflemen, a light digger and grenades. Buy replacements in orbit and destroy the enemy brain.
Existing characters and worlds are preserved. Disable **Full conversion** in the mod configuration to restore vanilla spawns and starting tools.

This is still a partial conversion. See [the rescue notes](docs/coalition-content.md) for the ported content, physical control handoff, and remaining engine limitations.

Character/physics work in progress: [source audit, verified changes and remaining solver gaps](docs/physics-parity.md).

Current systems:

| Pillar | What it does now |
|---|---|
| **Pixel terrain** | Every solid block is 8×8 "art pixels" (one 64-bit mask per tile). Pickaxes carve the pixels nearest the cursor at vanilla's pace (each hit removes pick power % of the block, doubled on soft ground), explosions blow round craters, bullets/arrows/bolts chip where they hit. Dirt and sand erode fast, stone slower, bricks slowest. Vanilla pickaxe-power requirements and boss gating still apply. When enough of a tile is gone (80% by default), what's left crumbles. |
| **Multiple bodies** | Bodies arrive by ship (or from a body item: **Clone Body**, **Light Body**, **Heavy Body**, **Crab Drone**, **Turret Kit**). The **actor types** differ the CC way, in mass, health, speed and jetpack: light soldiers are quick with a big tank, heavies are slow and tough with a strong jetpack that burns out fast, the crab drone is a low four-legged walker with a mounted gun and no jetpack, and the turret can't move but watches its arc with a mounted rifle. Press **Q**, or use the pie menu, to take control of the body under the cursor or cycle to the next one; your current body, with its items and health, stays behind. Idle bodies are the same CC actor as yours: same legs, jetpack, balance and ragdoll. An AI drives them through the same controls, in CC's **AI modes** (pie menu → Unit orders): **sentry**, **follow**, **patrol** (a beat around where they stand, turning at walls), **go to** (click a point), **gold dig** (find ore nearby, dig a shaft to it, and send what they dig up to orbit), and **brain hunt** (close on the nearest enemy brain). Orders go to the bodies you **box-select** (Select units..., then drag), or to all of them. They aim and fire at enemies in sight with magazines and reloads, and jet over obstacles. If your body dies with spares around, it leaves its remains and its gear, and control jumps to the nearest idle body. |
| **Brain** | Your team's brain is a slow walking robot with a brain in a jar. It's a body like the others: you start in it, and you can leave it idle somewhere safe while you fight in a soldier. If it's destroyed, it's game over and the world is lost for your team. |
| **Ships & orbit** | Everything moves between the ground and orbit by **cargo ship**. In the orbital menu (**K**), pick cargo from the stockpile, or nothing for a pickup, then **Order ship** and click a landing zone. The ship flies down, lands and unloads; ordered Clone Bodies step out as soldiers. Throw items into a landed ship, or walk idle bodies into it, then **Launch ship**. When it leaves the atmosphere its cargo joins the stockpile shared by every player in the world. Enemies can shoot ships down, and the cargo is lost. Choose the **craft** when ordering: a **rocket**; a **dropship**, which steers in at an angle and can be **flown** (open its hold with F and press Pilot ship; move to steer, jump to thrust, Q to get out); or a **drop crate**, which falls unpowered and bursts open, a cheap one-way delivery. Stand by a landed ship and press **F** to open its **hold** like a chest: click to take, drop or shift-click items to load. From the menu you can also **craft** in orbit (a station counts as available when its item is in orbit, and buckets count as water/lava/honey). With the **Orbital Constructor**, blocks and walls are placed straight from orbit: right-click a block in the menu to select it. |

| **Gold (economy)** | Coins in the orbital stockpile are the team's funds, CC's gold. The menu's **Buy** tab sells every body type and CC device into the stockpile; **Ctrl-click** a stockpile stack to sell it (mined ore and gems, loot) at Terraria's sell price. Calling a ship costs money (rocket 50s, dropship 1g, crate 20s). A new team gets its first ship free and 5 gold. |
| **Enemy faction** | CC's computer player (config option, on by default). A while after you arrive, an enemy brain digs in to a brick bunker about 220 tiles away. It earns money and spends it on dropships of soldiers (red-tinted) sent to hunt your brain, bigger and better armed with each wave. Its rounds hurt you and your bodies; you can shoot its ships down. Destroy its brain to win the world. |
| **Cortex Command bodies** | The body you control is a CC actor, built from CC's own numbers (see `docs/cc-actor-design.md`). It has CC gravity (about a quarter of Terraria's). It walks on its legs slowly and steadily: 0.8 px/tick, or 1.6 running. Jumping is the **jetpack**: a burst on press, then thrust that weakens as the 1.5 s tank drains and refills when you stop. The torso leans while walking and bends forward when crouched. Going prone dives you onto your belly to crawl. Moving too fast (thrown by a blast, falling about 20 tiles) throws the body off its feet. It then ragdolls, takes impact damage when it hits something, and gets back up after a second. A fall of about 32 tiles is lethal. Wings, rocket boots and extra jumps add jetpack fuel and thrust. Hooks still work; mounts and dashes are disabled. Collision is per pixel. |
| **Weapons (CC devices)** | **Guns** have magazines: size and reload time come from the fire rate (pistols 15, assault guns 40, machine guns 100, shotguns 6, snipers 5, launchers 1). An empty gun reloads by itself, and **R** reloads early. Each round leaves the muzzle with a random spread (CC's "shake"). **Sharp aim** narrows it: aim far out and hold steady, and the camera looks ahead along the aim by the gun's reach (a sniper sees further than a pistol). Firing unsupported (in the air, jetting, knocked over) doubles the spread. Recoil pushes the body, less so when it's heavily loaded. A dotted sight line shows the aim and current spread. **Pickaxes are diggers**: they dig the first solid ground along the aim, close in front of the hands, not under the cursor. **Grenades, bombs and dynamite** are thrown CC-style: hold to wind up, release to throw (a tap just lobs it at your feet). Every CC device system has an original example, all buyable and craftable: the **Service Pistol**, **Field Rifle**, **Tri-Burst Rifle** (bursts), **Scattergun** (pellets), **Longshot** (sniper), **Spinner** (spins up before it fires), **Rocket Tube**, **Sawthrower** (ricocheting saw blades), **Light/Heavy Digger** (digging particles), **Frag/Impact/Cluster grenades**, **Sticky Charge**, **Remote Charge** with its **Remote Detonator**, **Riot Shield** (blocks rounds from the front) and **Medikit**. |
| **Damage & remains** | Headshots do ×1.75. Hits leave visible wounds (cosmetic, no bleeding) and spray blood that splats on the terrain. A CC body caught in a blast **bursts into its parts** (helmet, torso, limbs, boots, jetpack), which tumble and bleed. A headshot kill knocks the head off and the body collapses. Any other death leaves a corpse that lies for a while. Terraria enemies burst into pieces of their own sprite when blasted, and lose their heads to a killing headshot. Explosions hurt everyone in range, idle bodies included. Bodies don't regenerate health naturally (config option), but potions, buffs and accessories still heal. |

A new team starts as its brain. The first ship lands next to it with two armed Coalition riflemen, a light digger and three frag grenades. New characters get an Orbital Constructor. The Buy tab also includes Coalition pistol, assault rifle, compact rifle and sniper rifle data ports.

### Controls
| Key | Action |
|---|---|
| A / D | walk |
| Shift (hold) | run |
| W or Space | jetpack: burst on press, thrust while held |
| Ctrl (hold) | crouch: lower, steadier aim; walking crouched is slower |
| S (hold) | prone: dive flat; A/D to crawl. Also drops through platforms |
| mouse | aim (the body turns to face it). Aiming far out builds sharp aim, and the camera looks ahead |
| left mouse | fire / use the held item (throwables: hold to wind up, release to throw) |
| R | reload |
| right mouse (hold) | **pie menu**: point at a command and release (orbital menu, launch ship, next item, drop, pick up, ship hold, unit orders, switch body). **Unit orders** opens a second page you click: sentry, follow, patrol, go to..., gold dig, brain hunt, select units..., select all |
| mouse wheel, 1 / 2 | next / previous carried item (the held item goes to the back of the queue) |
| F | pick up the weapon or gear nearest your hands; next to a landed ship, open its hold; otherwise use/open what's under the cursor (doors, chests) |
| G | drop the held item |
| Q | switch to the body under the cursor, or cycle to the next one; while flying a ship, get out |
| K | orbital menu (stockpile, fabricate, buy, sell, order and launch ships) |

Body constants are in `TerraCommand/Common/Actors/CCActor.cs` (class `CC`), ported from the CC Community Project's data
files, with sources noted in `docs/cc-actor-design.md`. Spread, recoil and blast strength are still in
`TerraCommand/Common/Physics/Tuning.cs`. All mod keys come bound by default (applied once, the first time the mod loads) and can be rebound under Controls → Mod Controls. Conflicting Terraria shortcuts are suppressed during actor play; saved keybindings are preserved.

**Carrying (CC style).** There's no hotbar.
- **Held and queued items:** a body holds one item in its hands. Its other weapons, tools and gadgets form a queue you rotate
  through.
- **Picking up:** equipment lying around is picked up deliberately (F), not by walking over it. Materials, ammo, coins and
  potions are still collected automatically, so mining works.
- **Blocks and potions:** these stay in the pack. Use them from the inventory (Esc) cursor, the Terraria way.
- **Mass:** everything carried has mass. A heavily loaded body walks sluggishly, and with enough stone in its pack the jetpack
  can't lift it. Send loads up by ship instead.
- **HUD:** shown next to the body: the held item with its rounds or reload progress, a throw wind-up bar, health, jetpack
  fuel, and the carried list after switching.

## Design decisions (easy to change)
- **Progression stays with the player, not the body.** Max life and mana from crystals and other permanent upgrades belong to the player. Each body only carries its items, its equipment and its current health.
- **Items must physically come down to be used.** Crafting and building happen in orbit, but weapons, armour and tools have to be delivered by ship, and ships cost coins from the stockpile.
- **Units belong to a team.** Players on the same team (or everyone, with no team) share bodies.
- **Armour gives stats only**: every body of a type looks the same.
- Coalition weapon names and data are ported from the local CC Community reference (see [provenance and adaptations](docs/coalition-content.md)). CC palette sprites now replace the prototype art for actors, gear and craft; actors use the original extension-based limb frames. See [art provenance and import instructions](docs/cc-art.md). Audio remains adapted from Terraria.

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
| `hurtunit dmg`, `npclife NPCName n` | remains (`expect deathfx gib\|decap\|corpse`, `expect chunks\|corpses\|splats op n`, `expect unitdist op tiles`) |
| `claimbrain [starter]`, `kind brain\|soldier`, `killunit` | brain (`expect kind\|unitkind brain\|soldier`, `expect brainstate none\|alive\|lost`, `expect gameover yes\|no`) |
| `carve dx dy r [power]`, `settile dx dy TileName`, `projectile name dx dy vx vy`, `spawnnpc name dx dy` | terrain / combat |
| `switch`, `orders`, `hurt dmg` | bodies |
| `next`, `prev`, `pickup`, `drop`, `spawnitem item dx dy`, `pie label`, `pieshow n` | CC carrying and pie menu (`expect holding item\|nothing`, `expect queue\|mass op n`, `expect worlditem item op n`) |
| `reload` | devices (`expect rounds\|magazine\|throwspeed\|sharpaim op n`, `expect reloading yes\|no`, `expect cone\|spread op n`) |
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
  Common/Units/EnemyFaction the AI faction: bunker, brain, budget, dropship waves, winning the world
  Common/Orbital/Economy   funds from stockpile coins, buy catalogue, selling, ship costs
  Common/Combat/CCDevices  FirearmSpec / CCFirearm / CCThrowable: the CC device model for original guns, grenades, diggers
  Common/Units/        UnitLoadout (a body's items/health/looks), UnitSystem (spawn/swap/orders/save), UnitPlayer (controls, death transfer)
  Common/UI/           OrbitalUISystem (stockpile / fabricate / buy / ship cargo menu), ShipHoldUI, ActorControlsUI (pie menu, HUD)
  Common/Net/          PacketHandler; server is authoritative for terrain, stockpile and units
  Content/             CargoShip (rocket / dropship / crate) and CloneUnit NPCs, body items, CC devices, Orbital Constructor,
                       actor part art (Content/Actors: Soldier, Light, Heavy, Crab, Turret, Brain)
```

## Roadmap
Done: CC movement and actors (soldier, light, heavy, crab drone, turret, brain robot); CC controls and inventory; CC devices
and an example of each device system; gibbing and corpses; settling debris and partly filled tiles; ships (rocket, dropship,
crate) with holds and piloting; gold economy with buying and selling; unit AI modes with box selection; the enemy AI faction.

Next:
1. Smarter unit AI: take cover, use ammo and mana, use any weapon properly, dig paths through terrain on go-to.
2. Enemy faction tactics: defend its bunker, dig in, adapt its purchases to what kills its soldiers.
3. Bosses and large enemies that tunnel or blast terrain.
4. Per-corner lighting on carved tiles. Proper gib chunks (art) instead of particles.
5. Keep per-item mod data (`ModItem` fields) in the stockpile.

## License
AGPL-3.0-or-later. See [LICENSE](LICENSE).
