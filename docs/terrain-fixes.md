# Terrain regression fixes after v0.8.0

These are local follow-up fixes for grass snagging and sustained fire stopping its excavation.

## Causes and changes

- Grass spreads or strips by changing a tile's type. Previously, a carved dirt mask stopped matching a grass tile, making its missing soil solid again. Mask lookup now preserves damage across compatible dirt/grass, mud/jungle/mushroom-grass and ash/ash-grass conversions. Replacing dirt with a different substrate, such as stone, still invalidates the old mask. Settled jungle-grass debris also retains the correct mud substrate.
- Settling debris previously checked body occupancy for new loose tiles, but could refill holes in existing tiles inside a body. Occupancy now covers both paths, uses the physical actor silhouette and limb tips, and includes idle clones that bypass vanilla tile collision.
- Projectile chips previously targeted a fixed point ahead of the projectile's center. Once a bullet clipped the edge of a notch, that point could be empty, so subsequent bullets carved nothing. Impacts now search the projectile's swept footprint for remaining material.
- Carved-tile collision previously swept only when the destination overlapped material. Fast bullets could skip a thin remnant when their destination was beyond it. Axis sweeps now test the complete path.

Movement tuning, world-save representation and packet layouts are unchanged.

## Verification

Before the fixes, the new grass-conversion fixture restored an open cell to solid, and 40 repeated bullets failed both depth checks.

Release compilation succeeds with zero errors and the four existing warnings. The run at `.testenv/runs/20261109-055611-suite` passed all 113 assertions across grass contact, bullet carving, terrain, collision, general features, mining hardness, CC devices and remains. Coverage includes soil-family conversions, real substrate replacement, decorative plants, walking past a grass bump, debris occupancy for controlled/idle bodies, continued excavation over two volleys, and a fast bullet crossing a four-pixel remnant.

The dedicated-server/two-client run at `.testenv/runs/20261109-055836-mp-terrain` passed all 10 assertions: both clients retain converted-grass masks and see continued bullet excavation. One headless client segfaulted after completing its assertions during teardown, as seen in earlier release testing. No packet underflow, end-of-stream or fatal error appeared in the stdout logs.

Reproduce with:

```sh
tools/testenv/run.sh tools/testenv/scenarios/grass-contact.txt tools/testenv/scenarios/bullet-carving.txt --build
tools/testenv/run-mp.sh tools/testenv/mp/terrain
```
