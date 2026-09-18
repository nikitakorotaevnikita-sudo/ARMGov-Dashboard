# -*- coding: utf-8 -*-
"""Собирает src/shared/arm.css из style-блока макета
2026-09-04-mvp-screen-rx_v2_fixed (fixed (2).mhtml).

Каждое правило получает префикс .rx-arm-root, чтобы стили контрола не протекали
в DOM хоста RX; сами значения не меняются — дизайн должен остаться дословным.

Запуск:  python tools/scope_css.py

Исходники лежат в tools/assets/:
  rx_style_block.css — тот же style-блок, что вшит в макет (с плейсхолдером @@FONT@@);
  tabler_sub2.b64    — сабсет Tabler Icons (9 глифов) в base64.
Меняем макет → кладём сюда обновлённый блок → перегенерируем arm.css.
"""
import io, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "assets")
ROOT = ".rx-arm-root"

css = io.open(os.path.join(OUT, "rx_style_block.css"), encoding="utf-8").read()
font = io.open(os.path.join(OUT, "tabler_sub2.b64")).read().strip()
css = css.replace("@@FONT@@", font)


def split_rules(text):
    """Режет CSS верхнего уровня на (prelude, body, is_at) с учётом вложенности."""
    out, buf, depth, i = [], "", 0, 0
    while i < len(text):
        ch = text[i]
        if ch == "{":
            depth += 1
            if depth == 1:
                prelude = buf.strip()
                buf = ""
                i += 1
                continue
        elif ch == "}":
            depth -= 1
            if depth == 0:
                out.append((prelude, buf))
                buf = ""
                i += 1
                continue
        buf += ch
        i += 1
    return out


def prefix_selector(sel):
    parts = []
    for s in sel.split(","):
        s = s.strip()
        if not s:
            continue
        if s == ":root":
            parts.append(ROOT)
        elif s == "body":
            parts.append(ROOT)
        elif s == "*":
            parts.append(ROOT)
            parts.append(ROOT + " *")
        elif s.startswith(ROOT):
            parts.append(s)
        else:
            parts.append(ROOT + " " + s)
    return ",".join(parts)


def render(rules, indent=""):
    res = []
    for prelude, body in rules:
        p = re.sub(r"\s+", " ", prelude).strip()
        if p.startswith("@font-face") or p.startswith("@keyframes"):
            res.append(indent + p + "{" + body.strip() + "}")
        elif p.startswith("@media") or p.startswith("@supports"):
            inner = render(split_rules(body), indent + "  ")
            res.append(indent + p + "{\n" + "\n".join(inner) + "\n" + indent + "}")
        else:
            res.append(indent + prefix_selector(p) + "{" + body.strip() + "}")
    return res


# комментарии верхнего уровня сохраняем отдельно: вырезаем, потом вернём шапкой
comments = re.findall(r"/\*.*?\*/", css, re.S)
clean = re.sub(r"/\*.*?\*/", "", css, flags=re.S)

rules = split_rules(clean)
body = "\n".join(render(rules))

# Адаптация под хост: контрол не знает ширины окна, он знает ширину своей колонки.
# Медиа-запрос макета переводим в контейнерный (канон rx-cover), контейнером делаем .wrap.
# Контейнер именно на .wrap, а не на .rx-arm-root: container-type включает containment,
# и корень стал бы containing block для position:fixed — оверлей диалога перестал бы
# раскрываться на весь экран. Диалог рендерится соседом .wrap, вне контейнера.
media_from = None
for candidate in ("@media(max-width:1100px)", "@media (max-width: 1100px)"):
    if candidate in body:
        media_from = candidate
        break
assert media_from, "изменилась форма медиа-запроса в макете"
body = body.replace(media_from, "@container (max-width:1100px)")
# Класс контейнера — после префиксации rx-arm-wrap (чеклист изоляции CSS).
body += "\n.rx-arm-root .rx-arm-wrap{container-type:inline-size}\n"

# Фон: макет — отдельная страница и красит подложку сам (--page, серый). На обложке
# подложку рисует хост (белая), и серый прямоугольник контрола выделялся заплаткой.
# Контрол становится прозрачным — цвет подложки берётся у хоста.
page_bg = None
for candidate in ("background:var(--page)", "background: var(--page)"):
    if candidate in body:
        page_bg = candidate
        break
assert page_bg, "изменилась заливка подложки в макете"
body = body.replace(page_bg, "background:transparent", 1)

# Ширина: max-width макета обрезала экран на 1440px — при отдалении (Ctrl+колесо)
# виджеты переставали растягиваться и по краям оставались пустые поля.
# На обложке ширину задаёт хост, поэтому ограничение снимаем.
width_re = re.compile(
    r"max-width:\s*1440px;\s*margin:\s*0\s+auto",
    re.M,
)
assert width_re.search(body), "изменилось ограничение ширины в макете"
body = width_re.sub("width:100%", body, count=1)

header = (
    "/* ============================================================\n"
    "   arm.css — вёрстка экрана АРМ руководителя (MVP, 1-й этап).\n"
    "   Дословный перенос style-блока макета 2026-09-04-mvp-screen-rx_v2_fixed:\n"
    "   значения не меняются, каждый селектор получает префикс .rx-arm-root,\n"
    "   чтобы стили контрола не протекали в DOM хоста Directum RX.\n"
    "   Токены :root и правила body перенесены на сам .rx-arm-root.\n"
    "   Шрифт — сабсет Tabler Icons 3.11.0 (9 глифов, MIT, tabler.io) из мокапа\n"
    "   рабочего стола RX, инлайном: контрол не тянет внешних ресурсов.\n"
    "   ============================================================ */\n"
)

dst = os.path.join(HERE, os.pardir, "src", "shared", "arm.css")
io.open(dst, "w", encoding="utf-8").write(header + body + "\n")
print("правил:", len(rules), "| символов:", len(body))
print("комментариев вырезано:", len(comments))
