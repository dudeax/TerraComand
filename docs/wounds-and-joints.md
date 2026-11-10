# Persistent wounds and material contacts — working 0.12.0

The subsequent [0.13.0 native-scale pass](native-scale.md) maps one CC pixel to one 2px Terraria art cell; the terrain limitation below describes the earlier scale.

This is a further implementation slice toward CC physics and damage parity, not a completed exact-engine port.
Reference: local CC Community checkout `cca55f154ba49a17436cf91d91e1302fe45395db`.

## Implemented

Flesh actors now collect ongoing wound-emitter damage. The standalone `ActorWounds` model resolves the inherited emission streams from `Data/Base.rte/Effects/Wounds.ini` and the counting rules in `Source/Entities/AEmitter.cpp`:

- Flesh entry: two 30/minute streams, six burst particles, 40-emission limit, 0.05 damage per continuous emission.
- Flesh exit: two 60/minute streams, ten resolved burst particles, 80-emission limit.
- Deadly torso exit: inherited exit streams plus a 60/minute blood stream.
- Parent break wound: inactive inherited blood stream plus 60/minute spray and 120/minute viscera; nine burst particles, 30 burst damage, 0.2 ongoing damage per emission, 480-emission limit.
- Strong head entry: the Heavy's source override uses a 50-emission limit and two burst damage.

Burst particles count toward the limit. Burst ticks deliver burst damage instead of emission damage. Emitters stop only after the count exceeds the limit, as in the source. Fractional health damage accumulates rather than rounding every blood particle up to a whole hit point.

Actual projectile hits create entry emitters. Heavy limb entries use the source metal-dent behavior and do not bleed. Mechanical actors do not create flesh wounds. Severing a flesh limb removes that limb's wound emitters and creates a parent stump emitter. The Medikit stops the body's active bleeding, clears fractional damage and resets limb wound counts without restoring missing limbs. Merely reaching full health does not erase physical wounds.

Player bodies collect their damage on the owning client; idle bodies collect it on the server. Internal wound damage bypasses projectile defence and immunity processing and does not create another wound. Idle bodies now also suppress Terraria's separate friendly-NPC regeneration counter when natural healing is disabled; it previously healed a friendly body by one health every 181 ticks, offsetting blood loss. Death still follows ordinary brain-loss and spare-body transfer hooks. Both actor movement snapshots and character saves preserve the emitters, their phase, burst state and fractional damage. Older body saves load without active emitters. NPC extra AI and player stance packets carry the same state. Every multiplayer peer needs 0.12.0 because the packet layout changed.

Living joints use `Attachable::TransferJointImpulses` stiffness, vector summation, strict threshold comparisons and capped transfer results. Light/canonical soldier arm strength is 320 N·s, leg strength 480 N·s, both at stiffness 0.5; source gib limits are 480/720 N·s. Heavy arms use 360/540 N·s. Supported bullet hits accumulate an estimate of relative projectile momentum for the limb during the current game tick and can sever it below its wound limit. The source collision-response effective mass/reflection and full parent/child momentum transfer are not yet applied on this path. Heavy arm/leg wound limits are now 10/11, versus the light soldier's 8/10. Mechanical joint presets have not been inferred from human values.

Rigid-body contacts now accept per-atom material responses. Human torso/head contacts use Flesh/Kevlared Flesh friction and bounce (0.6/0.6); the attached jetpack retains Military Stuff (0.4/0.75). Mechanical bodies retain the civilian/military response. Each atom's body material multiplies the contacted terrain's coefficients. Detached Heavy limbs use Armoured Flesh rather than a generic metal response. Geometry sampling remains unchanged in this pass.

The previously present additional Coalition weapon classes referenced four missing textures, preventing client startup despite successful compilation. Their native palette sprites have been imported, recorded in the art manifest, and added to the reproducible importer. Existing weapon definitions and Blast Runner work are preserved.

## Exact parity still outstanding

| Area | Current boundary / required work |
| --- | --- |
| Flesh and armour penetration | Projectile hit detection still uses Terraria broad hitboxes and nearby-bone routing. The source sprite raster traversal, layered helmet/armour resistance, bullet continuation and entry/exit wound placement need implementation. Exit emitter profiles are implemented but are not automatically created by penetration yet. |
| Initial impact damage | Existing Terraria projectile damage, defence and headshot scaling still handle the initial hit. Entry emitters therefore skip their burst damage on this compatibility path to prevent double damage. Full source damage requires replacing that path, not adding burst damage on top. |
| Living attachments | Source transfer decisions exist; only supported projectile hits use them. General collision and blast loads, independent living head/helmet/jetpack attachments, exact parent/child momentum transfer and component gib subdivision remain unfinished. A severed limb remains an intact temporary wreck even above its source gib threshold. |
| Body simulation | Native source masses, proportions, atom generation, limb pressure distribution, dynamic-body collisions and full attachment constraints still need parity work. Some actor kinds remain adapted variants. |
| Terrain | Material storage and excavation still use 2px cells, not CC's 1px bitmap. Existing Terraria protection/progression rules remain. |
| Emitters and explosions | Wound emission clocks are deterministic. Blood particles remain visual; jet/craft thrust uses mean particle momentum, and explosions use the existing radial impulse model rather than source particle interactions. |
| Health and treatment | Terraria player health/progression remains. The Medikit clears wounds; it does not regrow missing anatomy. |
| Remains | Detached pieces are temporary NPCs; full source articulated corpse/gib behavior and world persistence remain unfinished. |
| Validation | Regression checks validate formulas and gameplay behavior. Recorded CC-versus-mod trajectories, penetration traces and interactive feel comparisons are still needed before an exact-parity claim. |

## Verification

Run the standalone production-code suite and package the mod:

```sh
PATH="$HOME/.dotnet:$PATH" dotnet run --project tools/physics-tests -c Release
PATH="$HOME/.dotnet:$PATH" dotnet build TerraCommand -c Release
```

The new `wounds.txt` gameplay scenario exercises actual ticking blood loss, wound ownership through control handoff, Medikit treatment and stump bleeding. The `mp/wounds` scenario verifies synchronization and handoff with a dedicated server and two clients.

```sh
tools/testenv/run.sh tools/testenv/scenarios --timeout 300
tools/testenv/run-mp.sh tools/testenv/mp/wounds --timeout 240
```

The actor regression's post-tunnel assertion permits a one-pixel auto-crouch remainder while a physical stride settles, and checks that the actual sprite contact atoms remain outside terrain. The normal upright/crouch checks still require full 42px height. This replaces an exact metadata-height assumption exposed by the corrected material response.

The combined amputation/hatch fixture now starts with 400 health so that cumulative stump bleeding does not kill the test body and drop its 13kg rifle into the ship during a later empty-mass check. Lethal bleeding and spare-body transfer have their own wound scenario. Head-region and decapitation fixtures use a settled precision rifle to isolate region/death routing from Gaussian spread, which has separate coverage; the deployment step waits for that rifle's longer item animation to finish.

### Results

- Release packaging succeeds as 0.12.0 with zero errors and the four existing warnings.
- All 177 standalone production-code checks pass, including emitter inheritance/count limits, fractional damage, legacy saves, wound handoff, joint threshold/vector accumulation, treatment without regrowth, and per-atom material response.
- All 35 gameplay scenarios have passing coverage across a full run and focused reruns. The full `.testenv/runs/20261109-235512-suite` run had 432 passes and three failures in the headshot and combined amputation/hatch fixtures. Subsequent `combat` and `limbs-hatches` runs pass 21/23 checks; the precision-rifle `remains` rerun at `20261110-000006-remains` passes all 15 after allowing its longer firing animation to finish. This is not one clean full-suite run.
- The final `20261110-000928-wounds` scenario passes all 24 checks. It verifies controlled and idle-body bleeding, medical treatment, body ownership, player bleed-out control transfer, and an idle body bleeding to death. The idle-body death case exposed and verifies the fix for Terraria's hidden friendly-NPC regeneration counter.
- Dedicated server plus two clients: `20261109-235214-mp-wounds` passes 11 checks for wound replication, swaps and treatment; `20261110-001053-mp-limbs` passes 21 checks for living limb detachment, observer state, actual projectile-driven severing and anatomy-preserving handoff. These do not include a dedicated observer test of an NPC's final bleeding death.
- The existing headless teardown issues remain: both clients segfaulted after their DONE markers in the wound run; the limb run completed with the existing native/file-log teardown diagnostics. Completed assertions do not establish clean native shutdown.
