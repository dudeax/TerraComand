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

if __name__ == "__main__":
    body_item(); wand(); unit_fallback(); icon(); jetpack(); soldier(); brainbot(); ship()
