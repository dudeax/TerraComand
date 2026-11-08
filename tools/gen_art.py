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

def pad_tile():
    # 3x2 tile, 16px frames with 2px padding -> 54x36 px. Draw at 1x then place frames.
    full = [[(0,0,0,0)]*54 for _ in range(36)]
    art = C(24, 16)  # 48x32 art at scale 2
    art.rect(0, 10, 23, 15, DARK); art.rect(0, 10, 23, 10, LIGHT)
    for x in range(0, 24, 4): art.rect(x, 12, x+1, 13, YEL)
    art.rect(2, 2, 3, 9, STEEL); art.rect(20, 2, 21, 9, STEEL)  # gantry arms
    art.rect(2, 2, 6, 3, STEEL); art.rect(17, 2, 21, 3, STEEL)
    art.set(3, 1, RED); art.set(20, 1, RED)
    for fy in range(2):
        for fx in range(3):
            for y in range(16):
                for x in range(16): full[fy*18+y][fx*18+x] = art.p[fy*16+y][fx*16+x]
    png("Content/Tiles/LaunchPad.png", 54, 36, full)

def pad_item():
    c = C(16, 10); c.rect(0, 6, 15, 9, DARK); c.rect(0, 6, 15, 6, LIGHT)
    for x in range(0, 16, 4): c.rect(x, 8, x+1, 8, YEL)
    c.rect(1, 1, 1, 5, STEEL); c.rect(14, 1, 14, 5, STEEL); c.set(1, 0, RED); c.set(14, 0, RED)
    c.save("Content/Tiles/LaunchPadItem.png")

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

def remote():
    c = C(10, 14)
    c.rect(1, 3, 8, 13, DARK); c.rect(2, 4, 7, 7, CYAN); c.rect(2, 9, 3, 10, RED); c.rect(6, 9, 7, 10, YEL)
    c.rect(7, 0, 7, 2, STEEL); c.set(7, 0, RED)
    c.save("Content/Items/OrbitalUplink.png")

def pod():
    c = C(13, 17)
    c.rect(2, 2, 10, 14, STEEL); c.rect(3, 3, 9, 13, (90,96,110,255)); c.rect(5, 0, 7, 1, DARK)
    c.rect(3, 6, 9, 7, YEL); c.rect(0, 13, 2, 16, DARK); c.rect(10, 13, 12, 16, DARK); c.rect(4, 15, 8, 16, RED)
    c.save("Content/Projectiles/DropPod.png")

def rocket():
    c = C(7, 15)
    c.rect(2, 1, 4, 11, LIGHT); c.set(3, 0, RED); c.rect(2, 4, 4, 5, DARK)
    c.rect(0, 9, 1, 12, RED); c.rect(5, 9, 6, 12, RED); c.rect(2, 12, 4, 14, YEL)
    c.save("Content/Projectiles/LaunchRocket.png")

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

if __name__ == "__main__":
    pad_tile(); pad_item(); body_item(); wand(); remote(); pod(); rocket(); unit_fallback(); icon(); jetpack()
