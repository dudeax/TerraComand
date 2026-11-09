#!/usr/bin/env python3
"""Import CC palette art deterministically; requires Pillow. No artwork is generated.
Usage: python tools/import_cc_art.py /path/to/CC/Data
"""
from pathlib import Path
import json, sys
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('/mnt/media/cc-reference/src/Data')
OUT = ROOT / 'TerraCommand/Content'
manifest = []

def load(path):
    image = Image.open(SOURCE / path).convert('RGBA')
    # CC's mask colour is opaque palette magenta, not PNG alpha.
    image.putdata([(r,g,b,0 if (r,g,b)==(255,0,255) else a) for r,g,b,a in image.get_flattened_data()])
    return image

def save(image, target, sources, operation='palette transparency'):
    target = OUT / target
    target.parent.mkdir(parents=True, exist_ok=True)
    image.save(target)
    manifest.append(dict(output=str(target.relative_to(ROOT)), sources=sources, operation=operation))

def fit(image, size):
    image = image.copy()
    scale = min(size[0]/image.width, size[1]/image.height)
    image = image.resize((max(1,round(image.width*scale)),max(1,round(image.height*scale))),Image.Resampling.NEAREST)
    canvas = Image.new('RGBA',size)
    canvas.alpha_composite(image,((size[0]-image.width)//2,size[1]-image.height))
    return canvas

def port(source, target, size=None):
    image=load(source)
    if size: image=fit(image,size)
    save(image,target,[source], 'palette transparency; nearest-neighbour fit' if size else 'palette transparency')

light='Coalition.rte/Actors/Infantry/CoalitionLight/'
heavy='Coalition.rte/Actors/Infantry/CoalitionHeavy/'
brain='Base.rte/Actors/Brains/Brainbot/'
drone='Coalition.rte/Actors/Mecha/GatlingDrone/'
for kind,folder,torso,head,arm,leg,count in [
    ('Soldier',light,'TorsoA','HeadA','ArmFGA','LegFGA',5),
    ('Light',light,'TorsoA','HeadA','ArmFGA','LegFGA',5),
    ('Heavy',heavy,'TorsoB','HeadB','ArmFGB','LegFGB',5),
    ('Brain',brain,'Torso','MobileBrain000','ArmFG','LegFG',6),
]:
    old_size=Image.open(OUT/f'Actors/{kind}/Torso.png').size
    port(folder+torso+'.png',f'Actors/{kind}/Torso.png',old_size)
    if kind=='Soldier':
        face=load(folder+head+'.png'); helmet=load(folder+'HelmetA000.png')
        image=Image.new('RGBA',(14,12));image.alpha_composite(face,(1,0));image.alpha_composite(helmet,(0,0))
        save(image,f'Actors/{kind}/Head.png',[folder+head+'.png',folder+'HelmetA000.png'],'palette transparency; helmet composite')
    else:
        port(folder+head+'.png',f'Actors/{kind}/Head.png',(14,14) if kind=='Brain' else (14,12))
    foot = brain+'FootFG000.png' if kind=='Brain' else light+'FootFGA000.png'
    port(foot,f'Actors/{kind}/Foot.png')
    port(brain+'HandFG.png' if kind=='Brain' else light+'HandFGA.png',f'Actors/{kind}/Hand.png')
    for limb,prefix in [('Arm',arm),('Leg',leg)]:
        for side in ['Front','Back']:
            srcprefix=prefix if side=='Front' else prefix.replace('FG','BG')
            for frame in range(count):
                port(folder+srcprefix+f'{frame:03}.png',f'Actors/{kind}/{limb}{side}{frame}.png')
        # Compatibility pieces for old split-bone callers; normal actors use complete CC frames.
        image=load(folder+prefix+f'{count-1:03}.png');image=image.crop(image.getbbox()).transpose(Image.Transpose.ROTATE_270)
        image=fit(image,(6,14 if kind!='Brain' else 12))
        names=['Thigh','Shin'] if limb=='Leg' else ['UpperArm','Forearm']
        for part,name in enumerate(names):
            save(image.crop((0,part*image.height//2,image.width,(part+1)*image.height//2)),f'Actors/{kind}/{name}.png',[folder+prefix+f'{count-1:03}.png'],'compatibility split, rotated and fitted; normal rendering uses native frames')
    icon=folder+'Icon.png' if kind!='Brain' else brain+'MobileBrain000.png'
    if kind!='Brain':port(icon,f'Items/{dict(Soldier="CloneBody",Light="LightBody",Heavy="HeavyBody")[kind]}.png',(22,30))

port('Base.rte/Actors/Shared/Jetpack.png','Actors/Jetpack.png',(10,18))
port(drone+'MountMobile000.png','Actors/Crab/Body.png',(22,10))
for frame in range(5):
    for side,prefix in [('Front','LegFGA'),('Back','LegBGA')]:
        port(drone+prefix+f'{frame:03}.png',f'Actors/Crab/Leg{side}{frame}.png')
for name in ['Thigh','Shin']:
    port(drone+'LegFGA004.png',f'Actors/Crab/{name}.png',(4,8))
port(drone+'Icon.png','Items/CrabDrone.png',(22,30))
port('Base.rte/Actors/Turrets/SmallTurret/MountStaticA000.png','Actors/Turret/Base.png',(18,10))
port('Base.rte/Actors/Turrets/SmallTurret/TurretSmallA.png','Actors/Turret/Head.png',(12,8))
port('Base.rte/Actors/Turrets/SmallTurret/TurretSmallA.png','Items/TurretKit.png',(22,30))

weapons={
 'ServicePistol':'Coalition.rte/Devices/Weapons/Pistol/Pistol000.png',
 'FieldRifle':'Coalition.rte/Devices/Weapons/AssaultRifle/AssaultRifle000.png',
 'TriBurstRifle':'Coalition.rte/Devices/Weapons/CompactAssaultRifle/CompactAssaultRifle000.png',
 'Longshot':'Coalition.rte/Devices/Weapons/SniperRifle/SniperRifle000.png',
 'Scattergun':'Coalition.rte/Devices/Weapons/Shotgun/Shotgun000.png',
 'Spinner':'Coalition.rte/Devices/Weapons/GatlingGun/GatlingGun000.png',
 'RocketTube':'Base.rte/Devices/Weapons/RocketLauncher/RocketLauncher.png',
 'LightDigger':'Base.rte/Devices/Tools/Digger/LightDigger000.png',
 'HeavyDigger':'Base.rte/Devices/Tools/Digger/HeavyDigger000.png',
 'FragGrenade':'Coalition.rte/Devices/Explosives/FragGrenade/FragGrenade000.png',
 'ImpactGrenade':'Coalition.rte/Devices/Explosives/IncendiaryGrenade/IncendiaryGrenade.png',
 'ClusterGrenade':'Coalition.rte/Devices/Explosives/ClusterGrenade/ClusterGrenade000.png',
 'StickyCharge':'Coalition.rte/Devices/Explosives/TimedExplosive/TimedExplosive000.png',
 'RemoteCharge':'Base.rte/Devices/Explosives/RemoteExplosive/RemoteExplosive000.png',
 'RemoteDetonator':'Base.rte/Devices/Explosives/RemoteExplosive/Detonator000.png',
 'RiotShield':'Coalition.rte/Devices/Shields/CombatShield/CombatShield000.png',
 'Medikit':'Base.rte/Devices/Special/Medikit/MedikitA.png',
}
for name,src in weapons.items():port(src,f'Items/Devices/{name}.png')
port('Base.rte/Devices/Tools/Constructor/ConstructorA000.png','Items/OrbitalConstructor.png')
# Craft retain native dimensions and pivots for physical lever arms.
shiproot='Base.rte/Craft/Dropships/'
for part,src in [('DropshipHatchLeft','HatchALeft.png'),('DropshipHatchRight','HatchARight.png'),('DropshipBody','HullA000.png'),('DropshipEngineLeft','EngineALeft.png'),('DropshipEngineRight','EngineARight.png')]:
    port(shiproot+src,f'NPCs/{part}.png')
craft=Image.new('RGBA',(176,76))
for name,at in [('HullA000.png',(33,8)),('EngineALeft.png',(10,16)),('EngineARight.png',(131,16))]:
    craft.alpha_composite(load(shiproot+name),at)
save(craft,'NPCs/Dropship.png',[shiproot+n for n in ['HullA000.png','EngineALeft.png','EngineARight.png']],'native-resolution hull/engine composite')
port('Base.rte/Craft/Rockets/MK2/RocketMK2000.png','NPCs/CargoShip.png')
port('Base.rte/Craft/StorageCrate/CrateBodyA000.png','NPCs/DropCrate.png')
port('Ronin.rte/Devices/Weapons/RocketPropelledChainsaw/RPCLauncher.png','Items/Devices/Sawthrower.png')
port('Ronin.rte/Devices/Weapons/RocketPropelledChainsaw/RPC.png','Projectiles/SawBlade.png',(14,14))
port('Base.rte/Effects/Wounds/Spark01000.png','Projectiles/DigParticle.png',(2,2))
for frame in range(5):
    port('Base.rte/Craft/Shared/RocketLegA'+f'{frame:03}.png',f'NPCs/RocketLeg{frame}.png')
# Fallback NPC uses a composed native standing actor instead of the old stick figure.
portrait=Image.new('RGBA',(20,42));portrait.alpha_composite(load(light+'TorsoA.png'),(4,13));portrait.alpha_composite(load(light+'HeadA.png'),(4,2))
save(portrait,'NPCs/CloneUnit.png',[light+'TorsoA.png',light+'HeadA.png'],'fallback torso/head composite; normal actors use articulated renderer')
# Mod list icon uses the same Coalition visual identity.
icon=fit(load('Coalition.rte/ModuleIcon.png'),(80,80))
icon.save(ROOT/'TerraCommand/icon.png')
manifest.append(dict(output='TerraCommand/icon.png',sources=['Coalition.rte/ModuleIcon.png'],operation='palette transparency; nearest-neighbour fit'))
(ROOT/'docs/cc-art-manifest.json').write_text(json.dumps(dict(reference_commit='cca55f154ba49a17436cf91d91e1302fe45395db',assets=manifest),indent=2)+'\n')
print(f'Imported {len(manifest)} assets; provenance: docs/cc-art-manifest.json')
