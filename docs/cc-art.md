# CC art import

This pass replaces the procedural prototype art with palette artwork from Cortex Command Community Project commit `cca55f154ba49a17436cf91d91e1302fe45395db`. The upstream project is [Cortex Command Community Project](https://github.com/cortex-command-community/Cortex-Command-Community-Project); artwork is credited to Data Realms and the CC Community contributors, under the upstream repository's AGPL-3.0 license (also included in this repository's `LICENSE`).

[In-game preview](art-preview.png).

`cc-art-manifest.json` records every output, its source PNGs and any adaptation. Source magenta mask pixels become transparent RGBA. No AI-generated artwork is used.

- Soldier and Light use Coalition Light parts; Soldier composites the original helmet over the head.
- Heavy uses Coalition Heavy parts. Brain uses Brainbot parts, with its head fitted and neck adjusted for the existing mod proportions.
- Complete front/back arm and leg frames are selected by extension, following CC's frame-selection rule. Their joint pivots replace the temporary split-bone drawing. Source hands and feet complete the pose. Collision remains driven by the shared physical contact endpoints.
- Crab uses Gatling Drone parts; Turret uses the Base small turret. These remain adapted mod actors, not complete ports of those source actors.
- Weapons, diggers, explosives, shield, medikit and constructor use source sprites. The mod's Sawthrower uses Rocket Propelled Chainsaw launcher/projectile art; this is a cosmetic adaptation, not a port of that weapon's behavior.
- Rocket and storage crate retain native dimensions; the dropship renders separate hull and steering engine sprites. Native landing gear frames replace the rocket placeholder legs. The composite remains a fallback asset and collision reference. Body purchase icons and the mod icon use source icons.
- Existing non-colliding split-bone PNGs are compatibility exports from source art. Normal actors render the complete limb frames.

Rebuild the imports using a local checkout of the reference:

```sh
python3 -m venv /tmp/tc-art-tools
/tmp/tc-art-tools/bin/pip install -r tools/art-requirements.txt
/tmp/tc-art-tools/bin/python tools/import_cc_art.py /path/to/CC/Data
python3 tools/gen_collision_masks.py
python3 tools/gen_ship_masks.py
```

Pillow is an import-time tool only; the mod has no new runtime dependency. The old `gen_art.py` requires `--placeholders` explicitly, to prevent accidentally restoring prototypes. Importing is deterministic and preserves proportions with nearest-neighbour fitting where the manifest calls for it. Source sprite dimensions, equipment composition, attachment dynamics and all actor stats are not yet a complete 1:1 port.

Validation: Release build succeeds with the four existing warnings. All 57 production physics checks pass with regenerated masks. The focused actor-type, rig, contact, CC-device and ship run at `.testenv/runs/20261109-000300-suite` passed all 67 assertions. Screenshots were reviewed for both facings and standing, walking and prone poses. The full 26-scenario run at `.testenv/runs/20261109-000426-suite` passed 309 assertions and exposed one wall-follow failure. After correcting obstacle detection to measure net position progress, upward jet aim and full-body clearance, the follow/order rerun at `.testenv/runs/20261109-001734-suite` passed all 16 assertions. All 26 scenarios therefore have passing coverage across the full run and focused reruns, rather than one clean full-suite run. Every previously tracked PNG is covered by the 171-entry import manifest (including the working 0.9.0 craft additions).

The distributed `.tmod` includes `LICENSE` and `ART-CREDITS.txt`. Fresh release multiplayer handoff verification passed all 11 assertions; the headless teardown caveat is recorded in the physics notes and release notes.
