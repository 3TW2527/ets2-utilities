"""Draws the Mod Manager pictures (276x162 JPG) for the generated mods.

Run from anywhere: python make_icons.py  (needs Pillow; writes next to this script).
The JPGs are committed and embedded in Ets2u.Core, so this only reruns when the art changes.
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

W, H, S = 276, 162, 4          # final size, supersampling factor
OUT = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
BG_TOP, BG_BOTTOM = (30, 34, 44), (16, 18, 24)
TEXT, MUTED = (240, 242, 246), (150, 158, 172)


def font(name, size):
    return ImageFont.truetype(os.path.join(FONTS, name), size * S)


def canvas(accent):
    img = Image.new("RGB", (W * S, H * S))
    d = ImageDraw.Draw(img)
    for y in range(H * S):  # vertical gradient
        t = y / (H * S - 1)
        d.line([(0, y), (W * S, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)))
    d.rectangle([0, 0, W * S, 5 * S], fill=accent)  # accent strip on top
    return img, d


def label(d, title, accent):
    d.text((118 * S, 50 * S), "ETS2 UTILITIES", font=font("seguisb.ttf", 11), fill=accent)
    y = 66
    for line in title.split("\n"):
        d.text((118 * S, y * S), line, font=font("segoeuib.ttf", 21), fill=TEXT)
        y += 26


def p(*pts):
    return [(x * S, y * S) for x, y in pts]


def no_damage(d, a):
    # Shield with a tick.
    cx, top = 60, 34
    shield = [(cx, top), (cx + 34, top + 12), (cx + 30, top + 58), (cx, top + 92), (cx - 30, top + 58), (cx - 34, top + 12)]
    d.polygon(p(*shield), fill=a)
    inner = [(cx, top + 10), (cx + 25, top + 19), (cx + 22, top + 55), (cx, top + 81), (cx - 22, top + 55), (cx - 25, top + 19)]
    d.polygon(p(*inner), fill=(24, 27, 35))
    d.line(p((cx - 14, top + 46), (cx - 3, top + 58), (cx + 16, top + 34)), fill=a, width=7 * S, joint="curve")


def earnings(d, a):
    # Stack of coins with an upward arrow.
    for i in range(4):
        y = 112 - i * 13
        d.ellipse(p((26, y - 8), (78, y + 8)), fill=tuple(max(0, c - 40) for c in a))
        d.rectangle(p((26, y - 9), (78, y)), fill=tuple(max(0, c - 40) for c in a))
        d.ellipse(p((26, y - 17), (78, y - 1)), fill=a)
    d.line(p((88, 104), (88, 46)), fill=TEXT, width=6 * S)
    d.polygon(p((76, 58), (100, 58), (88, 38)), fill=TEXT)


def coach(d, a):
    # Coach seen from the front, with a heart above it.
    d.rounded_rectangle(p((28, 54), (92, 128)), radius=10 * S, fill=a)
    d.rounded_rectangle(p((35, 62), (85, 92)), radius=4 * S, fill=(24, 27, 35))
    d.rectangle(p((34, 108), (46, 114)), fill=TEXT)
    d.rectangle(p((74, 108), (86, 114)), fill=TEXT)
    d.rectangle(p((32, 128), (44, 136)), fill=(10, 11, 14))
    d.rectangle(p((76, 128), (88, 136)), fill=(10, 11, 14))
    hx, hy, r = 60, 34, 8  # heart
    d.ellipse(p((hx - 2 * r, hy - r), (hx, hy + r)), fill=TEXT)
    d.ellipse(p((hx, hy - r), (hx + 2 * r, hy + r)), fill=TEXT)
    d.polygon(p((hx - 2 * r + 1, hy + 3), (hx + 2 * r - 1, hy + 3), (hx, hy + 2 * r + 4)), fill=TEXT)


def route(d, a):
    # Folded map with a route line and a position arrow.
    xs = [22, 54, 70, 98]
    folds = [(22, 44, 50, 36, 50, 120, 22, 128), (50, 36, 72, 44, 72, 128, 50, 120), (72, 44, 98, 36, 98, 120, 72, 128)]
    for i, f in enumerate(folds):
        d.polygon(p(*zip(f[0::2], f[1::2])), fill=a if i != 1 else tuple(max(0, c - 45) for c in a))
    pts = [(30, 112), (44, 96), (58, 102), (74, 78), (90, 64)]
    d.line(p(*pts), fill=TEXT, width=4 * S, joint="curve")
    ang = math.atan2(64 - 78, 90 - 74)
    tip = (90 + 8 * math.cos(ang), 64 + 8 * math.sin(ang))
    left = (90 + 9 * math.cos(ang + 2.5), 64 + 9 * math.sin(ang + 2.5))
    right = (90 + 9 * math.cos(ang - 2.5), 64 + 9 * math.sin(ang - 2.5))
    d.polygon(p(tip, left, right), fill=TEXT)


MODS = {
    "ets2u_nodamage": ("No damage", (76, 175, 120), no_damage),
    "ets2u_economy": ("Bigger\nearnings", (236, 178, 46), earnings),
    "ets2u_coach": ("Coach\ncomfort", (84, 150, 230), coach),
    "ets2u_ui": ("Route advisor\nposition", (214, 98, 74), route),
}

for name, (title, accent, art) in MODS.items():
    img, d = canvas(accent)
    art(d, accent)
    label(d, title, accent)
    img.resize((W, H), Image.LANCZOS).save(os.path.join(OUT, f"{name}.jpg"), quality=92)
    print("wrote", name + ".jpg")
