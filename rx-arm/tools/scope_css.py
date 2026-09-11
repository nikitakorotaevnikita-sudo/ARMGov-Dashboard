# -*- coding: utf-8 -*-
"""Собирает src/shared/arm.css из style-блока макета 2026-09-04-mvp-screen-rx.html.

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
assert "@media(max-width:1100px)" in body, "изменилась форма медиа-запроса в макете"
body = body.replace("@media(max-width:1100px)", "@container (max-width:1100px)")
body += "\n.rx-arm-root .wrap{container-type:inline-size}\n"

# Фон: макет — отдельная страница и красит подложку сам (белую, как область обложки).
# Контрол её не красит вовсе: цвет подложки берётся у хоста, иначе при смене темы
# хоста на странице останется белый прямоугольник контрола.
assert "background:#FFFFFF;color:var(--text)" in body, "изменилась заливка подложки в макете"
body = body.replace("background:#FFFFFF;color:var(--text)", "background:transparent;color:var(--text)")

# Ширину задаёт хост. В макете с 11.09 ограничения уже нет — держим проверку, чтобы
# вернувшийся max-width не уехал в контрол незамеченным (при отдалении масштаба
# виджеты переставали растягиваться и по краям оставались пустые поля).
assert "max-width:1440px" not in body, "в макете вернулось ограничение ширины — снять его"

header = (
    "/* ============================================================\n"
    "   arm.css — вёрстка экрана АРМ руководителя (MVP, 1-й этап).\n"
    "   Дословный перенос style-блока макета 2026-09-04-mvp-screen-rx.html:\n"
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
