"""Extract lean 32×32 SVGs for chosen Обложка cells from the full sheet."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent / "assets" / "rx-icons"
SRC = ROOT / "oblozhka.svg"
OUT = ROOT / "picked"

# (logical name, col, row) — grid origin (10,12)/(10,64), step 40
PICKS = [
    ("users", 0, 0),           # две фигуры
    ("eye-check", 4, 0),       # документы + лупа (контроль)
    ("heartbeat", 5, 0),       # аптечка / здоровье
    ("clipboard-list", 8, 0),  # блокнот с карандашом (поручения)
    ("settings", 8, 1),        # простая шестерёнка (без силуэта)
]

SIZE = 32
OX0, OY0, OY1, STEP = 10, 12, 64, 40

# Element tags that draw
EL_RE = re.compile(
    r"<(path|rect|circle|ellipse|polygon|polyline|line|g)\b[^>]*?(?:/>|>.*?</\1>)",
    re.S | re.I,
)


def cell_origin(col: int, row: int) -> tuple[int, int]:
    return OX0 + col * STEP, (OY0 if row == 0 else OY1)


def first_coords(el: str) -> tuple[float, float] | None:
    """Rough anchor point of an element for cell assignment."""
    m = re.search(r'\bx="([^"]+)"', el)
    n = re.search(r'\by="([^"]+)"', el)
    if m and n:
        try:
            return float(m.group(1)), float(n.group(1))
        except ValueError:
            pass
    d = re.search(r'\bd="([^"]+)"', el)
    if d:
        nums = re.findall(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?", d.group(1))
        if len(nums) >= 2:
            try:
                return float(nums[0]), float(nums[1])
            except ValueError:
                pass
    pts = re.search(r'\bpoints="([^"]+)"', el)
    if pts:
        nums = re.findall(r"[-+]?\d*\.?\d+", pts.group(1))
        if len(nums) >= 2:
            return float(nums[0]), float(nums[1])
    return None


def in_cell(x: float, y: float, ox: int, oy: int, pad: float = 2.0) -> bool:
    return (ox - pad) <= x <= (ox + SIZE + pad) and (oy - pad) <= y <= (oy + SIZE + pad)


def main() -> None:
    c = SRC.read_text(encoding="utf-8")
    OUT.mkdir(parents=True, exist_ok=True)

    # Drop defs/clipPaths and background rect — keep drawable content only
    body = re.sub(r"<defs>.*?</defs>", "", c, flags=re.S)
    body = re.sub(r'<rect width="872"[^/]*/>', "", body)

    # Collect top-level drawing chunks: clip groups as wholes + loose elements
    chunks: list[str] = []
    for m in re.finditer(r'<g clip-path="url\(#clip[^"]+\)">(.*?)</g>', body, re.S):
        chunks.append(m.group(0))
    # Remove clip groups from body, then pick remaining self-closing / simple tags
    loose = re.sub(r'<g clip-path="url\(#clip[^"]+\)">.*?</g>', "", body, flags=re.S)
    for m in re.finditer(
        r"<(path|rect|circle|ellipse|polygon|polyline|line)\b[^>]*?/>",
        loose,
    ):
        chunks.append(m.group(0))

    print(f"chunks: {len(chunks)}")

    for name, col, row in PICKS:
        ox, oy = cell_origin(col, row)
        parts: list[str] = []
        for ch in chunks:
            # For clip groups, use clip rect origin from id mapping via content coords
            pt = first_coords(ch)
            if pt is None:
                # try any number pair inside
                nums = re.findall(r"[-+]?\d+\.?\d*", ch)
                if len(nums) >= 2:
                    pt = float(nums[0]), float(nums[1])
                else:
                    continue
            if in_cell(pt[0], pt[1], ox, oy):
                # Strip clip-path wrapper — keep inner paths only for lean SVG
                inner = re.sub(
                    r'^<g clip-path="url\(#[^"]+\)">|</g>$',
                    "",
                    ch,
                )
                parts.append(inner if inner != ch else ch)

        # Also include any path whose ALL significant coords are in cell (second pass on loose paths)
        svg = (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" '
            f'viewBox="{ox} {oy} {SIZE} {SIZE}" fill="none">\n'
            + "\n".join(parts)
            + "\n</svg>\n"
        )
        fp = OUT / f"{name}.svg"
        fp.write_text(svg, encoding="utf-8")
        print(f"{name}: {len(parts)} parts -> {fp} ({fp.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
