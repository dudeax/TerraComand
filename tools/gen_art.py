#!/usr/bin/env python3
"""Generates the mod's placeholder pixel art (no external deps). Run: python3 tools/gen_art.py"""
import os, struct, zlib, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "TerraCommand")

def png(path, w, h, px):
    raw = b"".join(b"\x00" + bytes(c for x in range(w) for c in px[y][x]) for y in range(h))
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    full = os.path.join(ROOT, path); os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, "wb").write(data); print("wrote", path, w, h)

class C:
    """Canvas drawn at 'art pixel' scale (Terraria art uses 2x2 screen pixels per art pixel)."""
    def __init__(s, w, h, scale=2): s.w, s.h, s.s = w, h, scale; s.p = [[(0,0,0,0)]*(w*scale) for _ in range(h*scale)]
    def set(s, x, y, c):
        if 0 <= x < s.w and 0 <= y < s.h:
            for dy in range(s.s):
                for dx in range(s.s): s.p[y*s.s+dy][x*s.s+dx] = c
    def rect(s, x0, y0, x1, y1, c):
        for y in range(y0, y1+1):
            for x in range(x0, x1+1): s.set(x, y, c)
    def save(s, path): png(path, s.w*s.s, s.h*s.s, s.p)

DARK=(40,44,52,255); STEEL=(120,128,140,255); LIGHT=(180,188,200,255); YEL=(240,200,40,255); BLK=(20,20,24,255)
RED=(200,60,50,255); CYAN=(80,220,230,255); SKIN=(230,180,140,255); GREEN=(80,160,90,255)

def body_item():
    c = C(11, 15)
    c.rect(1, 0, 9, 14, (60,90,120,255)); c.rect(2, 1, 8, 13, (140,210,230,180))
    c.rect(4, 3, 6, 5, SKIN); c.rect(4, 6, 6, 10, (90,90,160,255)); c.rect(4, 11, 4, 12, BLK); c.rect(6, 11, 6, 12, BLK)
    c.rect(1, 0, 9, 0, STEEL); c.rect(1, 14, 9, 14, STEEL)
    c.save("Content/Items/CloneBody.png")

def wand():
    c = C(14, 14)
    for i in range(10): c.set(2+i, 11-i, STEEL); c.set(3+i, 11-i, DARK)
    c.rect(10, 0, 13, 3, YEL); c.rect(11, 1, 12, 2, CYAN)
    c.save("Content/Items/OrbitalConstructor.png")

def unit_fallback():
    c = C(10, 21)
    c.rect(3, 1, 6, 5, SKIN); c.rect(2, 6, 7, 13, GREEN); c.rect(3, 14, 4, 20, DARK); c.rect(5, 14, 6, 20, DARK)
    c.save("Content/NPCs/CloneUnit.png")

def icon():
    c = C(40, 40)
    for y in range(40):
        for x in range(40): c.set(x, y, (12, 14+y//3, 30+y, 255))
    for (x, y) in [(5,4),(30,7),(18,3),(35,15),(9,12),(25,11)]: c.set(x, y, (255,255,255,255))
    # cratered terrain
    import math
    for x in range(40):
        top = 26 + int(2*math.sin(x/4))
        for y in range(top, 40):
            d = math.hypot(x-24, y-27)
            if d < 6: continue
            c.set(x, y, (120,80,50,255) if y < top+2 else (90,60,40,255) if (x+y)%5 else (70,70,80,255))
    # rocket
    c.rect(9, 10, 11, 22, LIGHT); c.set(10, 9, RED); c.rect(7, 19, 8, 23, RED); c.rect(12, 19, 13, 23, RED)
    c.rect(9, 23, 11, 25, YEL); c.rect(10, 26, 10, 27, (255,140,40,255))
    # unit
    c.rect(29, 15, 31, 17, SKIN); c.rect(28, 18, 32, 22, GREEN); c.rect(28, 23, 29, 25, DARK); c.rect(31, 23, 32, 25, DARK); c.rect(33, 19, 37, 19, DARK)
    c.save("icon.png")

def jetpack():
    # a CC-style backpack jetpack worn on the torso's back (drawn facing right: the pack is on the left)
    c = C(5, 9)
    c.rect(0, 0, 4, 6, DARK); c.rect(1, 1, 3, 5, STEEL); c.rect(1, 1, 3, 1, LIGHT)
    c.set(2, 3, YEL)
    c.rect(1, 7, 3, 8, BLK); c.set(2, 8, (90, 90, 96, 255))   # nozzle
    c.save("Content/Actors/Jetpack.png")

# ---------------------------------------------------------------- CC-style actors (original designs)
# Each part is drawn facing right at Terraria's 2x art scale. Limbs hang straight down from their joint (top centre);
# the renderer rotates them. Torso/jar stands on its hip (bottom centre); heads sit on the neck (bottom centre).

OLIVE=(104,116,72,255); OLIVE_D=(66,74,46,255); OLIVE_L=(150,160,104,255); OUT=(28,30,22,255)
VISOR=(80,190,210,255); VISOR_L=(170,240,250,255); BOOT=(58,44,34,255); BOOT_L=(92,72,54,255)
METAL=(130,136,146,255); METAL_D=(78,82,92,255); METAL_L=(196,202,210,255)
GLASS=(150,220,235,120); GLASS_L=(220,250,255,170); BRAIN=(226,120,150,255); BRAIN_D=(176,80,110,255)

def soldier():
    d = "Content/Actors/Soldier/"
    t = C(6, 8)                                   # torso: 12x16, hip at bottom centre
    t.rect(0, 0, 5, 7, OUT); t.rect(1, 1, 4, 5, OLIVE); t.rect(3, 1, 4, 4, OLIVE_L)   # chest plate on the front
    t.rect(1, 0, 4, 0, OLIVE_D)                   # collar
    t.rect(1, 6, 4, 6, OUT); t.set(3, 6, (200, 170, 60, 255))  # belt + buckle
    t.rect(1, 7, 4, 7, OLIVE_D)                   # pelvis
    t.save(d + "Torso.png")
    h = C(6, 6)                                   # head: helmet + visor, sits on the neck
    h.rect(0, 0, 5, 5, OUT); h.rect(1, 1, 4, 3, OLIVE); h.rect(1, 1, 3, 1, OLIVE_L)
    h.rect(0, 2, 5, 2, OLIVE_D)                   # helmet rim
    h.rect(3, 3, 5, 4, VISOR); h.set(5, 3, VISOR_L)
    h.rect(1, 4, 2, 5, OLIVE_D)                   # jaw guard
    h.save(d + "Head.png")
    th = C(2, 4); th.rect(0, 0, 1, 3, OLIVE); th.rect(0, 0, 0, 3, OLIVE_D); th.set(1, 0, OLIVE_L); th.save(d + "Thigh.png")
    sh = C(2, 4); sh.rect(0, 0, 1, 3, OLIVE_D); sh.rect(0, 0, 1, 0, OLIVE_L); sh.save(d + "Shin.png")       # knee pad on top
    f = C(3, 2); f.rect(0, 0, 2, 1, BOOT); f.rect(0, 0, 1, 0, BOOT_L); f.set(2, 1, OUT); f.save(d + "Foot.png")
    ua = C(2, 4); ua.rect(0, 0, 1, 3, OLIVE); ua.rect(0, 0, 1, 0, OLIVE_L); ua.save(d + "UpperArm.png")      # shoulder pad
    fa = C(2, 4); fa.rect(0, 0, 1, 2, OLIVE_D); fa.rect(0, 3, 1, 3, OUT); fa.save(d + "Forearm.png")        # glove at the end

def brainbot():
    d = "Content/Actors/Brain/"
    t = C(8, 9)                                   # the jar is the torso: 16x18, hip at bottom centre
    t.rect(1, 0, 6, 0, METAL_D); t.rect(0, 1, 7, 1, METAL)                      # lid
    t.rect(0, 2, 7, 6, GLASS); t.rect(1, 3, 6, 5, BRAIN); t.rect(2, 3, 2, 5, BRAIN_D); t.rect(4, 3, 4, 5, BRAIN_D)
    t.rect(1, 2, 1, 5, GLASS_L)                                                  # glint
    t.rect(0, 7, 7, 8, METAL_D); t.rect(1, 7, 6, 7, METAL); t.set(5, 8, (230, 70, 60, 255))  # base + status light
    t.save(d + "Torso.png")
    h = C(4, 2); h.rect(0, 0, 3, 1, METAL_D); h.rect(1, 0, 3, 0, METAL_L); h.set(3, 1, (230, 70, 60, 255)); h.save(d + "Head.png")  # sensor
    th = C(3, 4); th.rect(0, 0, 2, 3, METAL); th.rect(0, 0, 0, 3, METAL_D); th.save(d + "Thigh.png")
    sh = C(3, 4); sh.rect(0, 0, 2, 3, METAL_D); sh.rect(0, 0, 2, 0, METAL_L); sh.save(d + "Shin.png")
    f = C(4, 2); f.rect(0, 0, 3, 1, METAL_D); f.rect(0, 0, 2, 0, METAL); f.save(d + "Foot.png")
    ua = C(2, 4); ua.rect(0, 0, 1, 3, METAL); ua.save(d + "UpperArm.png")
    fa = C(2, 4); fa.rect(0, 0, 1, 3, METAL_D); fa.rect(0, 3, 1, 3, METAL_L); fa.save(d + "Forearm.png")

def ship():
    # an original cargo rocket: 14x34 art (28x68 px), door on the lower body, fins and a nozzle at the bottom
    c = C(14, 34)
    HULL=(176,182,190,255); HULL_D=(112,118,128,255); HULL_L=(222,226,232,255); STRIPE=(200,120,40,255)
    for y in range(0, 7):                                   # nose cone
        half = 1 + y * 5 // 6
        c.rect(7 - half, y, 6 + half, y, HULL); c.set(7 - half, y, HULL_D); c.set(6 + half, y, HULL_D)
    c.rect(6, 0, 7, 1, RED)
    c.rect(2, 7, 11, 29, HULL); c.rect(2, 7, 2, 29, HULL_D); c.rect(11, 7, 11, 29, HULL_D); c.rect(4, 7, 4, 29, HULL_L)
    c.rect(2, 15, 11, 15, STRIPE); c.rect(2, 16, 11, 16, HULL_D)
    c.rect(6, 9, 8, 11, BLK); c.rect(6, 9, 7, 10, CYAN)    # window
    c.rect(4, 19, 9, 27, HULL_D); c.rect(5, 20, 8, 26, (150, 156, 166, 255)); c.set(8, 23, YEL)   # cargo door (closed)
    for x0, x1 in ((0, 1), (12, 13)):                       # fins
        c.rect(x0, 24, x1, 33, HULL_D); c.rect(x0, 24, x1, 24, HULL)
    c.rect(4, 30, 9, 33, DARK); c.rect(5, 32, 8, 33, BLK)  # nozzle
    c.save("Content/NPCs/CargoShip.png")

def devices():
    # original CC-style devices (facing right). Guns: body, barrel, grip, magazine, stock.
    d = "Content/Items/Devices/"
    GUN=(70,74,82,255); GUN_D=(40,42,48,255); GUN_L=(120,126,136,255); WOOD=(120,84,52,255); OLV=(96,108,70,255)
    ORG=(210,120,40,255); RED2=(190,50,40,255)
    def gun(name, w, h, body, barrel, grip, mag=None, stock=None, accent=None, extra=None):
        c = C(w, h)
        bx0, by0, bx1, by1 = body
        c.rect(bx0, by0, bx1, by1, GUN); c.rect(bx0, by0, bx1, by0, GUN_L); c.rect(bx0, by1, bx1, by1, GUN_D)
        x0, y, x1 = barrel
        c.rect(x0, y, x1, y, GUN_D)
        gx, gy0, gy1 = grip
        c.rect(gx, gy0, gx + 1, gy1, GUN_D)
        if mag: c.rect(*mag, GUN_D)
        if stock: c.rect(*stock, WOOD if accent is None else accent)
        if extra: extra(c)
        c.save(d + name + ".png")
    gun("ServicePistol", 9, 6, (1, 1, 7, 2), (7, 1, 8), (2, 3, 5), mag=(4, 3, 4, 4))
    gun("FieldRifle", 20, 7, (4, 1, 14, 3), (14, 2, 19), (6, 4, 6), mag=(9, 4, 10, 6), stock=(0, 1, 3, 3), accent=OLV)
    gun("TriBurstRifle", 20, 7, (4, 1, 15, 3), (15, 2, 19), (6, 4, 6), mag=(10, 4, 11, 5), stock=(0, 2, 3, 3),
        extra=lambda c: (c.set(12, 1, ORG), c.set(13, 1, ORG)))
    gun("Scattergun", 19, 6, (4, 1, 11, 3), (11, 1, 18), (5, 4, 5), stock=(0, 1, 3, 3),
        extra=lambda c: (c.rect(11, 2, 18, 2, GUN_D), c.rect(12, 3, 15, 3, WOOD)))
    gun("Longshot", 26, 7, (6, 2, 15, 3), (15, 2, 25), (8, 4, 6), mag=(11, 4, 11, 5), stock=(0, 2, 5, 4),
        extra=lambda c: (c.rect(8, 0, 13, 1, GUN_D), c.set(13, 0, CYAN)))
    gun("Spinner", 22, 8, (3, 2, 12, 5), (12, 3, 21), (5, 6, 7), mag=(7, 6, 10, 7),
        extra=lambda c: (c.rect(12, 2, 21, 2, GUN_D), c.rect(12, 4, 21, 4, GUN_D), c.rect(12, 2, 13, 5, GUN_L)))
    gun("RocketTube", 22, 7, (2, 1, 20, 4), (20, 2, 21), (8, 5, 6),
        extra=lambda c: (c.rect(2, 1, 20, 1, OLV), c.rect(20, 1, 21, 4, GUN_D), c.rect(0, 2, 1, 3, GUN_D), c.set(11, 0, RED2)))
    def saw_extra(c):
        c.rect(13, 0, 18, 5, GUN_L); c.rect(14, 1, 17, 4, GUN_D)   # the blade housing
        for (x, y) in ((13, 0), (18, 0), (13, 5), (18, 5)): c.set(x, y, (200, 200, 210, 255))
    gun("Sawthrower", 19, 7, (2, 1, 13, 4), (13, 2, 13), (5, 5, 6), stock=(0, 2, 1, 3), accent=ORG, extra=saw_extra)
    def digger_extra(tip):
        def f(c):
            c.rect(10, 1, 12, 4, (90, 150, 190, 255)); c.rect(13, 1, 13 + tip, 4, (200, 210, 220, 255))
            c.set(13 + tip, 2, CYAN); c.set(13 + tip, 3, CYAN)
        return f
    gun("LightDigger", 16, 6, (2, 1, 10, 4), (10, 2, 10), (4, 5, 5), extra=digger_extra(1))
    gun("HeavyDigger", 19, 7, (2, 1, 11, 5), (11, 2, 11), (4, 6, 6), mag=(7, 6, 8, 6), extra=digger_extra(4))
    def grenade(name, body, band, cap):
        c = C(6, 7)
        c.rect(1, 2, 4, 6, body); c.rect(0, 3, 5, 5, body); c.rect(1, 4, 4, 4, band)
        c.rect(2, 0, 3, 1, cap); c.set(4, 1, GUN_D)
        c.save(d + name + ".png")
    grenade("FragGrenade", OLV, (60, 70, 44, 255), GUN_L)
    grenade("ImpactGrenade", (170, 60, 50, 255), (220, 200, 60, 255), GUN_L)
    grenade("ClusterGrenade", (90, 90, 110, 255), ORG, GUN_L)
    c = C(7, 5); c.rect(0, 1, 6, 4, (70, 90, 60, 255)); c.rect(1, 2, 5, 3, (100, 140, 80, 255)); c.set(3, 0, RED2); c.save(d + "StickyCharge.png")
    c = C(8, 5); c.rect(0, 1, 7, 4, GUN); c.rect(1, 2, 6, 3, GUN_D); c.set(6, 1, RED2); c.set(1, 0, GUN_L); c.save(d + "RemoteCharge.png")
    c = C(5, 8); c.rect(0, 2, 4, 7, GUN); c.rect(1, 3, 3, 4, GUN_D); c.set(2, 5, RED2); c.rect(3, 0, 3, 1, GUN_L); c.save(d + "RemoteDetonator.png")
    c = C(8, 16); c.rect(0, 0, 7, 15, (60, 66, 80, 255)); c.rect(1, 1, 6, 14, (110, 120, 140, 255))
    c.rect(1, 3, 6, 5, (170, 220, 240, 160)); c.rect(1, 10, 6, 10, (240, 220, 60, 255)); c.save(d + "RiotShield.png")
    c = C(8, 7); c.rect(0, 1, 7, 6, (230, 230, 230, 255)); c.rect(0, 1, 7, 1, (200, 200, 200, 255))
    c.rect(3, 2, 4, 5, RED2); c.rect(2, 3, 5, 4, RED2); c.rect(3, 0, 4, 0, GUN); c.save(d + "Medikit.png")
    # projectiles
    c = C(7, 7)
    c.rect(1, 1, 5, 5, (190, 196, 206, 255)); c.rect(2, 2, 4, 4, (120, 126, 136, 255)); c.set(3, 3, GUN_D)
    for (x, y) in ((3, 0), (0, 3), (6, 3), (3, 6), (0, 0), (6, 6), (6, 0), (0, 6)): c.set(x, y, (230, 232, 240, 255))
    c.save("Content/Projectiles/SawBlade.png")
    c = C(1, 1); c.set(0, 0, (120, 220, 255, 255)); c.save("Content/Projectiles/DigParticle.png")

if __name__ == "__main__":
    body_item(); wand(); unit_fallback(); icon(); jetpack(); soldier(); brainbot(); ship(); devices()
