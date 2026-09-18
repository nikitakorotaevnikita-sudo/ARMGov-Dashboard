"""Split exported Figma Обложка SVG into per-icon tiles by clip-path groups."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent / "assets" / "rx-icons"
SRC = ROOT / "oblozhka.svg"
OUT = ROOT / "tiles"
SIZE = 32


def bbox_of_paths(body: str) -> tuple[float, float, float, float] | None:
    nums = [float(n) for n in re.findall(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?", body)]
    # Too noisy — instead find M/H/V/L coords roughly via all numbers and take min/max of those that look like x/y
    # Better: match path d="..." and extract coordinate pairs loosely
    xs: list[float] = []
    ys: list[float] = []
    for d in re.findall(r'\bd="([^"]+)"', body):
        # tokenize numbers in order; for absolute cmds alternate x,y is imperfect but OK for bbox
        vals = [float(n) for n in re.findall(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?", d)]
        # Heuristic: collect all values; treat even as x-ish by taking overall min/max of all
        # Then refine: most icons live in a 40×40 cell — use min/max of all coords
        for v in vals:
            if 0 <= v <= 872:
                xs.append(v)
            if 0 <= v <= 153:
                ys.append(v)
    if not xs or not ys:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def main() -> None:
    c = SRC.read_text(encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)

    # Extract clipPath defs for inclusion
    defs_m = re.search(r"(<defs>.*?</defs>)", c, re.S)
    defs = defs_m.group(1) if defs_m else ""

    groups = list(
        re.finditer(
            r'<g clip-path="url\(#(clip\d+_9136_60126)\)">(.*?)</g>',
            c,
            re.S,
        )
    )
    print(f"clip groups: {len(groups)}")

    index: list[tuple[int, str, float, float]] = []
    for i, m in enumerate(groups):
        clip_id, body = m.group(1), m.group(2)
        bb = bbox_of_paths(body)
        if bb:
            x0, y0, x1, y1 = bb
            # Snap to 40px grid starting at 10,12 / 10,64
            cx = (x0 + x1) / 2
            cy = (y0 + y1) / 2
            col = round((cx - 10 - 16) / 40)
            row = 0 if cy < 58 else 1
            ox = 10 + col * 40
            oy = 12 if row == 0 else 64
        else:
            ox, oy = 0.0, 0.0
            col, row = -1, -1

        tile = (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" '
            f'viewBox="{ox} {oy} {SIZE} {SIZE}" fill="none">\n'
            f"{defs}\n"
            f'<g clip-path="url(#{clip_id})">{body}</g>\n'
            f"</svg>\n"
        )
        name = f"icon_{i:02d}_r{row}_c{col}_x{int(ox)}_y{int(oy)}.svg"
        (OUT / name).write_text(tile, encoding="utf-8")
        index.append((i, name, ox, oy))
        print(f"  {name} bbox={bb}")

    # Also crop remaining absolute paths that sit between groups? For now grid-crop full sheet.
    # Full-sheet grid tiles (includes non-grouped icons like document/list)
    sheet_inner = c[c.find("<rect") : c.rfind("</svg>")].strip()
    # strip outer white rect fill for transparency? keep it
    for row, y0 in enumerate((12, 64)):
        for col in range(21):
            x0 = 10 + col * 40
            if x0 + SIZE > 872:
                break
            tile = (
                f'<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" '
                f'viewBox="{x0} {y0} {SIZE} {SIZE}" fill="none">\n'
                f"{sheet_inner}\n"
                f"</svg>\n"
            )
            name = f"cell_r{row}_c{col:02d}.svg"
            (OUT / name).write_text(tile, encoding="utf-8")

    preview = [
        "<!doctype html><meta charset=utf-8><title>RX Обложка icons</title>",
        "<style>body{font:12px/1.3 system-ui;background:#eee;padding:16px}",
        "h2{margin:20px 0 8px}.row{display:flex;flex-wrap:wrap;gap:10px}",
        ".cell{width:72px;text-align:center;background:#fff;padding:6px;border-radius:4px;border:1px solid #ddd}",
        ".cell img{width:40px;height:40px;display:block;margin:0 auto 4px;image-rendering:pixelated}",
        "code{font-size:9px;word-break:break-all}</style>",
        "<h1>clip groups</h1><div class=row>",
    ]
    for i, name, ox, oy in index:
        preview.append(
            f'<div class=cell><img src="tiles/{name}" alt=""><code>{name}</code></div>'
        )
    preview.append("</div><h1>grid cells row0</h1><div class=row>")
    for col in range(21):
        name = f"cell_r0_c{col:02d}.svg"
        if (OUT / name).exists():
            preview.append(
                f'<div class=cell><img src="tiles/{name}" alt=""><code>r0 c{col}</code></div>'
            )
    preview.append("</div><h1>grid cells row1</h1><div class=row>")
    for col in range(21):
        name = f"cell_r1_c{col:02d}.svg"
        if (OUT / name).exists():
            preview.append(
                f'<div class=cell><img src="tiles/{name}" alt=""><code>r1 c{col}</code></div>'
            )
    preview.append("</div>")
    (ROOT / "preview.html").write_text("\n".join(preview), encoding="utf-8")
    print("wrote", ROOT / "preview.html")


if __name__ == "__main__":
    main()
