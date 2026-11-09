# Detachable craft parts and material contacts — 0.10.0 working build

This builds on the 0.9.0 shared travel solver. Reference is CC Community commit `cca55f154ba49a17436cf91d91e1302fe45395db`: `Attachable::TransferJointImpulses`, `SceneMan::TryPenetrate`, `AtomGroup::Travel`, `Base.rte/Materials.ini`, dropship MK1 and shared rocket-leg presets.

## Implemented

Dropship left/right engines and rocket left/right landing gear are removable physical attachments. Collision impulses accumulate per attachment for one simulation tick, with joint stiffness applied before the source strength comparison. Engines use 4,000 N·s and stiffness 1; gear uses 25,000 N·s and stiffness 0.5. Projectile wounds use source limits of 35 for engines and 9 for gear. Player-owned projectile hits are forwarded to the server with identity and a component-local contact point; the server checks ownership, active projectile, target eligibility, part geometry and proximity, and deduplicates hits. Nearby explosions can break the impacted joint.

Removing a component removes its mass (300kg engine, 70kg gear), thrust/support, collision atoms and rendered sprite. Parent inertia is recalculated from the surviving atom group. The detached part becomes a server-authoritative `CraftWreck` NPC using the same swept translation/rotation solver. It inherits velocity at the component centre, including angular motion; the parent receives the corresponding linear momentum correction. Native sprite offsets and mirrored gear geometry determine its physical pivot. Blasts also accelerate and spin loose wrecks according to their mass. Wrecks last five minutes. Missing-part masks, wound counts, part identity, rotation and spin synchronize to clients. Destroying a hull releases its surviving components after NPC death processing finishes, so a new wreck cannot overwrite the dying ship’s slot.

Materials have source integrity, friction, bounce and density metadata. Terraria soil maps to Earth, sand/silt/slush to Sand, stone to Stone, bricks to Concrete, ores to Metal, ice to Ice, and wood to Wood. Unknown solids fall back to Stone. Grass continues to share its substrate. Rigid-body contacts multiply representative body and terrain friction/restitution; limbs retain Rubber properties. Individual attached meshes do not yet carry separate per-atom material IDs.

A contact's share of incoming momentum is tested against integrity. The 2px terrain cell covers four source pixels, so resistance is `integrity * 4`. When it yields, exactly that remaining cell is removed, material debris is emitted, and the resisting impulse consumes momentum using CC's `integrity / incoming momentum` retardation. Carved cells use the existing terrain network/save masks. Ordinary low-energy support does not erode terrain. World boundaries, protected tiles and existing boss/mining gates remain enforced. Physical terrain penetration has its own server configuration toggle.

## Limits of this pass

This is the first attachment/material slice, not a complete CC entity/material port. Main/side rocket thrusters, component gib subdivision, wound decals/bleeding effects, dynamic-body collisions and repair are still separate work. A joint failure currently leaves an intact wreck even when CC would subdivide that component at its gib threshold. Ship projectile damage still follows the existing hull health path alongside component wounds. Parent/component angular momentum is not yet solved as a general articulated constraint system.

Terrain yielding runs on the authority: ships, idle actors, wrecks and singleplayer controlled actors can displace it. Multiplayer player-owned movement currently collides with the resulting synchronized terrain but does not predict or request material penetration itself. The subsequent [0.11.0 pass](limbs-hatches-ballistics.md) adds living limbs, hatches and momentum-based terrain penetration for supported bullets. Granular debris retains its existing particle simulation, and density metadata is not yet used to derive grain mass. Terrain remains the adapted 2px grid rather than CC's 1px bitmap.

The hard-fall regression uses protected ground: a fall onto yielding soil can now excavate and cushion the impact. Crawling is measured on fresh flat ground after the fall fixture, rather than in its newly created crater.

Rocket autopilot steering accounts for the actual angular impulse of activating a side engine, with a smaller threshold for continuing emission. This avoids chattering opposite full activation bursts and creating artificial sustained lift. Manual controls retain source burst behavior. Rockets stop requesting engine thrust while unloaded/waiting, but their bodies remain physically simulated. Craters and failed assemblies can strand a craft; cargo/economy regression fixtures use a fresh, level landing zone.

An empty unit-selection rectangle now reliably clears selection, as its documented behavior requires. Material-driven changes in body position exposed that an empty XNA rectangle could previously overlap a nearby unit and accidentally retain one selection.

## Validation

- Release build: successful, with the four existing warnings.
- Standalone production-code suite: 114 checks pass, including material thresholds/retardation, resting-contact non-erosion, automatic joint failure, wound limits, momentum inheritance, missing-part mass/thrust/silhouette, snapshot round-trips, and burst-aware steering.
- The 31-scenario run `.testenv/runs/20261109-171437-suite` passed 365 assertions and exposed eight failures in cargo/rocket behavior and empty unit selection. Focused reruns after the controller/selection fixes and fixture isolation give passing coverage across all 31 scenarios; this is not one clean full-suite run.
- Final flight/cargo rerun (`ships`, `features`, `ship-physics`): 56 assertions pass. Detachment plus hull-death disassembly: 15 assertions pass. Material excavation plus loose-wreck blast response: five assertions pass; the existing explosion scenario also passes all four assertions.
- Dedicated server/two-client live detachment: 18 assertions pass. Both clients observe the intact assembly before the server removes a part, then observe its missing mask, new mass, and one physical wreck.
- Dedicated server/two-client player-projectile hit test: 10 assertions pass. Nine real client-owned bullet hits break the enemy rocket's gear on the server and synchronize the result to both clients.

The headless environment still sometimes emits the existing native/file-log errors during teardown after DONE; assertions above describe completed game scenarios, not a clean native shutdown. Wrecks are temporary NPCs and do not persist across world reloads.
