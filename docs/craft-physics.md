# Character and craft physics — 0.9.0 working build

For the 0.10.0 additions, see [detachable parts and material contacts](attachments-materials.md). The notes below describe the 0.9.0 stage.

Reference: Cortex Command Community Project checkout `cca55f154ba49a17436cf91d91e1302fe45395db`, especially `AtomGroup.cpp`, `LimbPath.h`, `ACRocket.cpp`, `ACDropShip.cpp`, and the Base.rte craft and atom group presets.

Actors and craft now share swept rigid body terrain travel. Translation and rotation advance together in a local coordinate frame; simultaneous contacts distribute momentum using mass and moment of inertia. The terrain adapter reads the same remaining 2px cells as excavation, including slopes and platforms. Craft no longer use Terraria rectangle collision, velocity caps, air drag, scripted landing snaps, or frozen waiting states.

Character hands and feet use the source human foot, hand, and crab foot atom offsets, mirrored with facing. Rubber friction (0.9) and restitution (0.2) multiply terrain material properties. Contact normals and material responses are averaged over each group’s simultaneous hits. Force limits grow with elapsed segment time. Return strokes remain collision-disabled. Idle clones now update the same pixel-aware ground slope as controlled bodies. Settling terrain protects limb group atoms and craft hulls.

Craft presets use source attachment masses and emission rates:

| Craft | Empty mass | Main force | Activation burst | Capacity reference |
| --- | ---: | ---: | ---: | ---: |
| Rocket MK2 | 948 kg | 40,500 N | 4,320 N·s | 600 kg |
| Dropship MK1 | 3,422 kg | 43,750 N per engine | none in flight controller | 800 kg |
| Drop crate | 300.2 kg | none | none | 250 kg |

The rocket uses main-engine gimbaling and side/upper thrusters with lever-arm torque. Gear extends and retracts along its source paths. The dropship uses source throttle interpolation, cargo weight compensation, vertical velocity feedback, differential engine balancing, engine slew, and retro balance jets. Neutral piloting holds altitude; left/right changes engine direction, and jump/down requests vertical movement. Crates fall under the same gravity as characters.

Cargo includes packed body mass, built-in devices, equipment, and the pilot. Blast impulses affect craft mass and angular momentum. A pilot leaving a moving craft inherits the velocity at the exit point, including rotation. Ship simulation and state changes run on the authority; clients receive angular motion and engine/gear state along with the NPC's position and velocity. All peers need the same mod version.

The delivery controller requests engine input rather than prescribing movement. The entry/return plane is 900px above the landing zone, adapted to Terraria world scale. Native craft images and collision masks retain source pixel dimensions and pivots. Capacity is a reference, not a hard loading limit; an overloaded craft may fail to lift.

This is not a claim of full numerical or gameplay parity. Terrain is Terraria's 2px material grid, with adapted friction values. Character proportions and some body variants remain adapted. Craft are single rigid assemblies: detachable engines/hatches/legs, individual particle emission randomness, actor-to-craft collisions, the source circular/linear limb pressure distribution and terrain penetration by limb force, and full CC material interaction are not implemented. Rocket gear uses sampled support atoms and interpolated extension, not separate constrained bodies. Headless tests validate resolved equations and fixtures, not recorded frame-for-frame CC trajectories.

Validation commands:

```
dotnet run --project tools/physics-tests -c Release
dotnet build TerraCommand -c Release
tools/testenv/run.sh tools/testenv/scenarios --timeout 300
tools/testenv/run-mp.sh tools/testenv/mp/craft --timeout 240
```

Regression fixtures were adjusted where behavior intentionally changed: dropship neutral input now checks hovering before handing landing back to the autopilot; the mining hardness target sits within the physical hand's 56px digger reach; the idle-body fall fixture consumes its only packed body instead of carrying an additional 133kg body into the fall. Wall-following allows 960 ticks for contact-driven clearance and landing. Ground probes examine all limb atoms rather than only each group's anchor.

Validation results: the Release build succeeds with the four pre-existing warnings, and 86 standalone physics checks pass. The 29-scenario run `.testenv/runs/20261109-070202-suite` passed 352 assertions and exposed five failures in the rocket return and mining fixture. The final four-scenario rerun (`features`, `hardness`, `ships`, `ship-physics`) passed all 62 assertions after correcting rocket gimbal sign/attitude control and isolating the dirt fixture from earlier digger sweeps. All 29 scenarios therefore have passing coverage across the full run and focused rerun; this is not one clean full-suite run. Test process shutdown can still emit the existing headless native/file-log teardown errors after scenario completion.

The final dedicated-server/two-client craft run (`.testenv/runs/20261109-070743-mp-craft`) completed all 18 assertions successfully: boarding/control authority, pilot mass, thrust response, observer velocity/rotation, pilot exit and autonomous landing. The earlier longer flight test timed out during the return phase; the final test stays within prepared terrain and uses shorter input/observation intervals.
