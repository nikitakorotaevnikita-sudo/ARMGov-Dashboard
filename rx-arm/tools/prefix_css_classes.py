# -*- coding: utf-8 -*-
"""Префикс rx-arm- для коротких/общих CSS-классов (чеклист изоляции от хоста).

Классы arme-/armk-/armt-/armr-/armtl-/armp-/dcp- уже уникальны — не трогаем.
В TSX переименовываем только строковые литералы внутри className=...

Запуск: python tools/prefix_css_classes.py && python tools/scope_css.py
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CSS = ROOT / "tools" / "assets" / "rx_style_block.css"

CLASS_RENAMES: list[tuple[str, str]] = [
    ("w-footer-link", "rx-arm-footer-link"),
    ("card-head", "rx-arm-card-head"),
    ("card-body", "rx-arm-card-body"),
    ("cfgw-grid", "rx-arm-cfgw-grid"),
    ("ti-arrow-narrow-right", "rx-arm-ti-arrow-narrow-right"),
    ("ti-clipboard-list", "rx-arm-ti-clipboard-list"),
    ("ti-eye-check", "rx-arm-ti-eye-check"),
    ("ti-heartbeat", "rx-arm-ti-heartbeat"),
    ("ti-settings", "rx-arm-ti-settings"),
    ("ti-search", "rx-arm-ti-search"),
    ("ti-users", "rx-arm-ti-users"),
    ("ti-check", "rx-arm-ti-check"),
    ("ti-x", "rx-arm-ti-x"),
    ("cfgw", "rx-arm-cfgw"),
    ("tbtn", "rx-arm-tbtn"),
    ("wico", "rx-arm-wico"),
    ("wrap", "rx-arm-wrap"),
    ("card", "rx-arm-card"),
    ("ti", "rx-arm-ti"),
    ("primary", "rx-arm-primary"),
    ("ghost", "rx-arm-ghost"),
    ("risk", "rx-arm-risk"),
    ("over", "rx-arm-over"),
    ("bad", "rx-arm-bad"),
    ("ok", "rx-arm-ok"),
    ("on", "rx-arm-on"),
    ("ct", "rx-arm-ct"),
    ("ci", "rx-arm-ci"),
    ("red", "rx-arm-red"),
    ("orange", "rx-arm-orange"),
    ("amber", "rx-arm-amber"),
    # однобуквенные модификаторы метрик / вкладок / диалога
    ("l", "rx-arm-l"),
    ("c", "rx-arm-c"),
    ("n", "rx-arm-n"),
    ("a", "rx-arm-a"),
    ("r", "rx-arm-r"),
    ("x", "rx-arm-x"),
]


def rename_tokens(body: str) -> str:
    for old, new in CLASS_RENAMES:
        body = re.sub(rf"(?<![A-Za-z0-9_-]){re.escape(old)}(?![A-Za-z0-9_-])", new, body)
    return body


def rename_in_css(text: str) -> str:
    for old, new in CLASS_RENAMES:
        text = re.sub(rf"\.{re.escape(old)}(?=[\s\.\:\,\{{\[>+~]|$)", f".{new}", text)
    return text


def rename_quoted_strings_in_classname(text: str) -> str:
    def repl_attr(m: re.Match[str]) -> str:
        expr = m.group(1)

        def repl_str(sm: re.Match[str]) -> str:
            q = sm.group(1)
            return f"{q}{rename_tokens(sm.group(2))}{q}"

        expr = re.sub(r"([\"'])([^\"']*)\1", repl_str, expr)
        expr = re.sub(r"`([^`]*)`", lambda sm: f"`{rename_tokens(sm.group(1))}`", expr)
        return f"className={{{expr}}}"

    # className={...}
    text = re.sub(r"className=\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", repl_attr, text)

    # className="..." / className='...'
    def repl_plain(m: re.Match[str]) -> str:
        return f"className={m.group(1)}{rename_tokens(m.group(2))}{m.group(1)}"

    text = re.sub(r"className=([\"'])([^\"']*)\1", repl_plain, text)
    return text


def main() -> None:
    CSS.write_text(rename_in_css(CSS.read_text(encoding="utf-8")), encoding="utf-8")
    print(f"updated {CSS.relative_to(ROOT)}")

    for path in list(ROOT.glob("src/**/*.tsx")) + list(ROOT.glob("src/**/*.ts")):
        if path.name.endswith(".d.ts"):
            continue
        raw = path.read_text(encoding="utf-8")
        out = rename_quoted_strings_in_classname(raw)
        if path.name == "icons.tsx":
            out = out.replace("ti ti-", "rx-arm-ti rx-arm-ti-")
            out = out.replace("`ti ", "`rx-arm-ti ")
        if path.name == "data.ts" and "dueTone" in raw:
            out = rename_tokens(out) if "armr-d" in out or "dueTone" in out else out
            # only touch return string literals of dueTone
            out = re.sub(
                r"(return )([\"'])([^\"']*)\2",
                lambda m: m.group(1) + m.group(2) + rename_tokens(m.group(3)) + m.group(2),
                out,
            )
        if out != raw:
            path.write_text(out, encoding="utf-8")
            print(f"updated {path.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
