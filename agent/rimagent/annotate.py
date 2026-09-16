"""Set-of-Mark annotation for map screenshots: axis grid + numbered marks on things + anchor boxes."""
from __future__ import annotations

import io
from typing import Any

from PIL import Image, ImageDraw, ImageFont


def _font(size: int):
    for name in ("/System/Library/Fonts/Supplemental/Arial Bold.ttf", "/System/Library/Fonts/Helvetica.ttc", "/Library/Fonts/Arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except Exception:  # noqa: BLE001
            continue
    return ImageFont.load_default()


def annotate(png: bytes, cx: int, cz: int, cells_wide: float, wpx: int, hpx: int, things: list[dict[str, Any]], anchors: dict[str, Any], marks: bool = True) -> tuple[bytes, dict[int, dict[str, Any]]]:
    img = Image.open(io.BytesIO(png)).convert("RGBA")
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    cell = wpx / cells_wide                     # pixels per cell
    # the camera is centred on the centre of cell (cx, cz); z grows upward
    def px(x: float) -> float: return wpx / 2 + (x - (cx + 0.5)) * cell
    def py(z: float) -> float: return hpx / 2 - (z - (cz + 0.5)) * cell
    f_small, f_mark = _font(max(10, int(cell * 0.9))), _font(max(11, int(cell * 1.1)))
    x0 = int(cx - cells_wide / 2) - 1; x1 = int(cx + cells_wide / 2) + 2
    z0 = int(cz - cells_wide * hpx / wpx / 2) - 1; z1 = int(cz + cells_wide * hpx / wpx / 2) + 2
    # grid every 5 cells, labelled
    for x in range(x0, x1):
        if x % 5 == 0:
            X = px(x)
            d.line([(X, 0), (X, hpx)], fill=(255, 255, 255, 70 if x % 10 else 130), width=1)
            d.text((X + 2, 2), str(x), fill=(255, 255, 0, 230), font=f_small)
            d.text((X + 2, hpx - f_small.size - 4), str(x), fill=(255, 255, 0, 230), font=f_small)
    for z in range(z0, z1):
        if z % 5 == 0:
            Z = py(z)
            d.line([(0, Z), (wpx, Z)], fill=(255, 255, 255, 70 if z % 10 else 130), width=1)
            d.text((2, Z - f_small.size), str(z), fill=(255, 255, 0, 230), font=f_small)
            d.text((wpx - 34, Z - f_small.size), str(z), fill=(255, 255, 0, 230), font=f_small)
    # anchors as boxes with names
    for name, rect in (anchors or {}).items():
        try:
            mn, mx = rect["min"], rect["max"]
            d.rectangle([px(mn[0]), py(mx[1] + 1), px(mx[0] + 1), py(mn[1])], outline=(0, 255, 255, 220), width=2)
            d.text((px(mn[0]) + 3, py(mx[1] + 1) + 2), name, fill=(0, 255, 255, 255), font=f_mark)
        except Exception:  # noqa: BLE001
            pass
    table: dict[int, dict[str, Any]] = {}
    if marks:
        n = 0
        SKIP = ("Wall", "Door", "Fence", "Sandbag", "Conduit", "Barricade", "Embrasure", "Column")
        for t in things:
            pos = t.get("pos")
            if not pos or any(k in str(t.get("def", "")) for k in SKIP):
                continue
            if n >= 60:
                break
            n += 1
            size = t.get("size") or [1, 1]
            X, Z = px(pos[0] + 0.5), py(pos[1] + 0.5)
            r = max(7, cell * 0.45)
            color = (255, 80, 80, 235) if t.get("state") != "built" else (80, 160, 255, 235)
            d.ellipse([X - r, Z - r, X + r, Z + r], fill=color, outline=(0, 0, 0, 255), width=1)
            txt = str(n)
            tw = d.textlength(txt, font=f_mark)
            d.text((X - tw / 2, Z - f_mark.size / 2 - 1), txt, fill=(255, 255, 255, 255), font=f_mark)
            table[n] = {"id": t.get("id"), "def": t.get("def"), "at": pos, "state": t.get("state"), "rot": t.get("rot"), "size": size}
    out = Image.alpha_composite(img, overlay).convert("RGB")
    buf = io.BytesIO()
    out.save(buf, format="PNG", optimize=True)
    return buf.getvalue(), table
