"""One-shot: arm.css → tokens.css + tabler-icons.css + arm.module.css."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARM = (ROOT / "src/shared/arm.css").read_text(encoding="utf-8")
BLOCK = (ROOT / "tools/assets/rx_style_block.css").read_text(encoding="utf-8")
OUT = ROOT / "src/shared/styles"
OUT.mkdir(parents=True, exist_ok=True)


def main() -> None:
    m = re.search(r":root\s*\{([\s\S]*?)\n\}", BLOCK)
    if not m:
        raise SystemExit("no :root in rx_style_block.css")
    root_body = m.group(1)

    night_start = BLOCK.find(".rx-arm-root[data-theme='Night']")
    night_end = BLOCK.find(".rx-arm-footer-link button")
    night_css = BLOCK[night_start:night_end].strip() if night_start >= 0 else ""

    tokens = f"""/* Поверхности/текст → --theme_* хоста; акценты макета. */
.rx-arm-root {{{root_body}
  background: transparent;
  color: var(--text);
  font-size: 13px;
  font-family:
    'Segoe UI',
    -apple-system,
    BlinkMacSystemFont,
    system-ui,
    'Helvetica Neue',
    Arial,
    sans-serif;
  -webkit-font-smoothing: antialiased;
}}
.rx-arm-root,
.rx-arm-root * {{
  box-sizing: border-box;
}}
{night_css}
"""

    font = re.search(r"@font-face\s*\{[\s\S]*?\n\}", BLOCK)
    if not font:
        raise SystemExit("no @font-face")
    ti_rules = re.findall(r"\.rx-arm-ti[^{]*\{[^}]*\}", BLOCK)
    tabler_parts = [font.group(0)]
    for t in ti_rules:
        t2 = t.replace(
            "font-family: 'rx-arm-tabler-icons' !important",
            "font-family: 'rx-arm-tabler-icons', sans-serif",
        )
        tabler_parts.append(".rx-arm-root " + t2)
    tabler_parts.append(
        """
.rx-arm-root .rx-ico {
  width: 20px;
  height: 20px;
  display: block;
  flex: none;
  object-fit: contain;
}
"""
    )
    tabler = "\n".join(tabler_parts)

    idx = ARM.find(".rx-arm-root .rx-arm-wrap")
    if idx < 0:
        raise SystemExit("no .rx-arm-wrap in arm.css")
    body = ARM[idx:]
    nidx = body.find(".rx-arm-root[data-theme='Night']")
    if nidx >= 0:
        media = body.find("@media", nidx)
        body = body[:nidx] + (body[media:] if media >= 0 else "")

    module_css = (
        "/* CSS Modules: классы макета, изоляция через hash webpack */\n"
        + re.sub(r"\.rx-arm-root\s+", "", body)
    )
    module_css = module_css.replace("grid-column: span 6 !important", "grid-column: span 6")

    # Portal modal: ensure overlay works outside root (absolute vars fallbacks already on :root via host)
    (OUT / "tokens.css").write_text(tokens, encoding="utf-8")
    (OUT / "tabler-icons.css").write_text(tabler, encoding="utf-8")
    (ROOT / "src/shared/arm.module.css").write_text(module_css, encoding="utf-8")
    print("wrote tokens/tabler/arm.module.css", len(tokens), len(tabler), len(module_css))


if __name__ == "__main__":
    main()
