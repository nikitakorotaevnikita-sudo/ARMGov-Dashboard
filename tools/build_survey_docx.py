# -*- coding: utf-8 -*-
"""
Генератор анкеты приоритизации метрик дашборда (Word .docx).
Собирает документ со скриншотами блоков и таблицами MoSCoW + галочка «в MVP».
Скрины: C:\\Users\\vm-operator\\Desktop\\Скрины
Выход:  docs/custdev/2026-07-13-анкета-приоритизации-метрик.docx

Запуск: python tools/build_survey_docx.py
"""
import os
from docx import Document
from docx.shared import Cm, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.oxml.ns import qn
from docx.oxml import OxmlElement

SCR = r"C:\Users\vm-operator\Desktop\Скрины"
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   "docs", "custdev", "2026-07-13-анкета-приоритизации-метрик.docx")

GREEN = RGBColor(0x1E, 0x7A, 0x34)
AMBER = RGBColor(0xB5, 0x6A, 0x00)
NAVY  = RGBColor(0x0B, 0x2A, 0x4A)
GREY  = RGBColor(0x5A, 0x5A, 0x5A)
IMG_W = Cm(16.5)
BOX = "☐"

# ---- Данные: разделы -> блоки -> (заголовок, [скрины], [метрики]) ----
# метрика = (номер/код, название, "что даёт руководителю", рекомендация M|S)
A = [
    ("Соотношение выполненных и просроченных заданий",
     "Одним числом — держит ли регион сроки в целом.", "M",
     ["20260713105132.png"]),
    ("Самый проблемный процесс и этап-затык",
     "Какой процесс идёт дольше всех и на каком именно этапе застревает работа.", "M",
     ["20260713105215.png"]),
    ("Застрявшие задания (просрочка > 3 дней)",
     "Сколько заданий критически просрочены — требуют немедленного вмешательства.", "M",
     ["20260713111105.png"]),
    ("«Мои задания» с личным контролем",
     "Личные задания руководителя из «Входящих» + закрепление тех, что держит на особом контроле.", "M",
     ["20260713131046.png"]),
    ("«Что горит сейчас»",
     "Список процессов с наибольшими проблемами прямо сейчас — быстрый обзор точек внимания.", "S",
     ["20260713131148.png"]),
    ("Обращения граждан: объём и топ-5 вопросов",
     "Сколько обращений и по каким темам чаще пишут заявители (юз-кейс губернатора — предвыездная аналитика).", "M",
     ["20260713131229.png"]),
    ("Светофор процессов",
     "Все процессы в одной таблице: здоровье, количество, этап-затык, просрочка, тренд дисциплины.", "M",
     ["20260713131354.png"]),
]

B = [
    ("Стартовый блок (общая рамка процесса)", ["20260713133013.png"], [
        ("1", "Всего заданий по процессу", "Масштаб процесса.", "S"),
        ("2", "В работе", "Сколько заданий сейчас в работе.", "S"),
        ("3", "Просрочено", "Сколько заданий просрочено по процессу.", "S"),
        ("4", "Средний срок исполнения", "Средняя длительность одного задания.", "S"),
        ("5", "Время прохождения основной массы (95%)", "Реалистичный «худший» срок для 95% заданий.", "S"),
    ]),
    ("Объём и поток", ["20260713133202.png"], [
        ("6", "Воронка этапов (выполнено / в работе / просрочено)", "Как задания распределены по этапам маршрута и где скапливаются.", "S"),
        ("7", "Приток / Отток", "Успевает ли выполнение за поступлением новых заданий.", "S"),
    ]),
    ("Скорость и качество", ["20260713134453.png"], [
        ("8", "Отклонения этапов от норматива", "Какие этапы систематически выбиваются из стандартного времени.", "S"),
        ("9", "Распределение заданий по длительности и объёму", "Есть ли аномально долгие задания.", "S"),
        ("10", "Время на исполнение документа", "Сколько времени реально уходит на работу с документом.", "S"),
        ("11", "Время исполнения vs реальная работа с документом", "Работали над документом или «нажали согласовать не читая».", "M"),
        ("12", "Скорость взятия в работу", "Как быстро задание забирают в работу после поступления.", "M"),
    ]),
    ("Дисциплина и риск", ["20260713142239.png"], [
        ("13", "Динамика исполнительской дисциплины сотрудников", "Как меняется дисциплина людей во времени.", "S"),
        ("14", "Прогноз срыва заданий", "Какие задания уйдут в просрочку в ближайшие дни — предупреждение заранее.", "M"),
        ("15", "Ранжирование заданий по дням просрочки", "Самые «застоявшиеся» задания по глубине просрочки.", "S"),
    ]),
    ("Переделки и возвраты", ["20260713142433.png"], [
        ("16", "Возвраты на доработку (исполнитель / проверяющий)", "Сколько раз документ гоняли на доработку и кто инициатор — «35-я версия».", "M"),
        ("17", "% заданий с первого раза без доработок", "Доля работы, сделанной качественно сразу.", "M"),
        ("18", "Количество петель по процессу", "Сколько раз работа зацикливалась — лишние круги маршрута.", "M"),
    ]),
    ("Люди", ["20260713142732.png", "20260713142853.png"], [
        ("19", "Топ сотрудников по просрочкам", "Кто чаще срывает сроки.", "M"),
        ("20", "Топ сотрудников по исполнению", "Кто тянет объём.", "M"),
        ("21", "Топ по КПД (просрочка + исполнение + объём)", "Объективная сводная оценка исполнителя.", "M"),
        ("22", "Динамика загрузки исполнителей", "Как распределена и меняется нагрузка по людям.", "S"),
    ]),
    ("Срезы процесса", ["20260713142934.png"], [
        ("23", "Срез по этапам", "Показатели в разрезе этапов процесса.", "S"),
        ("24", "Срез по исполнителям", "Показатели в разрезе сотрудников.", "S"),
        ("25", "Срез по ведомству", "Показатели в разрезе ведомств.", "S"),
    ]),
]

TOP10 = [
    "Соблюдение сроков (выполнено / просрочено) — стартовая",
    "Самый проблемный процесс и этап-затык — стартовая",
    "Застрявшие задания > 3 дней — стартовая",
    "«Мои задания» с личным контролем — стартовая",
    "Обращения граждан: объём и топ-вопросы — стартовая",
    "Светофор процессов — стартовая",
    "Возвраты и «с первого раза» + петли (виджеты 16–18) — глубокая",
    "Реальная работа с документом / скорость реакции (11–12) — глубокая",
    "Прогноз срыва заданий (14) — глубокая",
    "Топ сотрудников по КПД / просрочкам (19–21) — глубокая",
]

# ---------------- helpers ----------------
def set_cell_bg(cell, hexcolor):
    tcPr = cell._tc.get_or_add_tcPr()
    shd = OxmlElement('w:shd')
    shd.set(qn('w:val'), 'clear'); shd.set(qn('w:color'), 'auto'); shd.set(qn('w:fill'), hexcolor)
    tcPr.append(shd)

def set_col_widths(table, widths):
    table.autofit = False
    table.allow_autofit = False
    for row in table.rows:
        for i, w in enumerate(widths):
            row.cells[i].width = w

def rec_text(cell, code):
    cell.text = ""
    p = cell.paragraphs[0]; p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    r = p.add_run("Обязательно" if code == "M" else "Желательно")
    r.bold = True; r.font.size = Pt(9)
    r.font.color.rgb = GREEN if code == "M" else AMBER

def eval_cell(cell):
    cell.text = ""
    p = cell.paragraphs[0]; p.paragraph_format.space_after = Pt(0)
    for i, lbl in enumerate(("Обязательно", "Желательно", "Не нужно")):
        if i: p = cell.add_paragraph()
        p.paragraph_format.space_after = Pt(0)
        run = p.add_run(f"{BOX} {lbl}"); run.font.size = Pt(9)

def mvp_cell(cell):
    cell.text = ""
    p = cell.paragraphs[0]; p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = p.add_run(BOX); run.font.size = Pt(14)

def add_metric_table(doc, rows):
    """rows: list of (num, name, value, rec)"""
    t = doc.add_table(rows=1, cols=5)
    t.style = "Table Grid"
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    hdr = t.rows[0].cells
    labels = ["№", "Метрика и что она даёт руководителю", "Рекомендация\nDirectum", "Ваша оценка", "В MVP"]
    for c, lbl in zip(hdr, labels):
        c.text = ""
        pr = c.paragraphs[0]; pr.alignment = WD_ALIGN_PARAGRAPH.CENTER
        run = pr.add_run(lbl); run.bold = True; run.font.size = Pt(9)
        run.font.color.rgb = RGBColor(0xFF, 0xFF, 0xFF)
        set_cell_bg(c, "0B2A4A")
    for (num, name, value, rec) in rows:
        cells = t.add_row().cells
        # №
        cells[0].text = ""; p0 = cells[0].paragraphs[0]; p0.alignment = WD_ALIGN_PARAGRAPH.CENTER
        r0 = p0.add_run(str(num)); r0.bold = True; r0.font.size = Pt(9)
        # name + value
        cells[1].text = ""
        pn = cells[1].paragraphs[0]; pn.paragraph_format.space_after = Pt(1)
        rn = pn.add_run(name); rn.bold = True; rn.font.size = Pt(9.5); rn.font.color.rgb = NAVY
        pv = cells[1].add_paragraph(); rv = pv.add_run(value); rv.font.size = Pt(9); rv.font.color.rgb = GREY
        # rec / eval / mvp
        rec_text(cells[2], rec)
        eval_cell(cells[3])
        mvp_cell(cells[4])
    set_col_widths(t, [Cm(0.9), Cm(8.8), Cm(2.4), Cm(3.2), Cm(1.2)])
    return t

def add_screens(doc, files):
    for f in files:
        path = os.path.join(SCR, f"Pasted image {f}")
        if os.path.exists(path):
            p = doc.add_paragraph(); p.alignment = WD_ALIGN_PARAGRAPH.CENTER
            p.add_run().add_picture(path, width=IMG_W)
        else:
            doc.add_paragraph(f"[скрин не найден: {f}]")

# ---------------- build ----------------
doc = Document()
# поля страницы
for s in doc.sections:
    s.top_margin = Cm(1.8); s.bottom_margin = Cm(1.8)
    s.left_margin = Cm(1.8); s.right_margin = Cm(1.8)

# базовый шрифт
style = doc.styles["Normal"]; style.font.name = "Calibri"; style.font.size = Pt(10)

# --- титул ---
h = doc.add_paragraph(); h.alignment = WD_ALIGN_PARAGRAPH.CENTER
r = h.add_run("Анкета приоритизации метрик дашборда"); r.bold = True; r.font.size = Pt(18); r.font.color.rgb = NAVY
sub = doc.add_paragraph(); sub.alignment = WD_ALIGN_PARAGRAPH.CENTER
rs = sub.add_run("АРМ руководителя · Аналитика процессов Directum RX — выбор состава для MVP (1-й этап)")
rs.font.size = Pt(11); rs.font.color.rgb = GREY
meta = doc.add_paragraph(); meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
rm = meta.add_run("Для: заказчик / ВДЛ   ·   Дата: 13.07.2026"); rm.font.size = Pt(9.5); rm.font.color.rgb = GREY

# --- цель и инструкция ---
doc.add_paragraph()
hp = doc.add_heading("Как заполнять", level=1)
doc.add_paragraph(
    "Ниже — все метрики дашборда, сгруппированные по экранам и блокам, с реальными скриншотами на данных стенда. "
    "Задача — отобрать состав для первого этапа (MVP): что даёт максимум ценности сразу.")
for line in [
    "• В колонке «Ваша оценка» по каждой метрике отметьте один вариант: Обязательно / Желательно / Не нужно.",
    "• В колонке «В MVP» поставьте галочку у тех, что берём в первый этап. Ориентир — не более 10 метрик.",
    "• «Рекомендация Directum» — наш совет по итогам интервью (15.06.2026): с чего начать. Решение — за вами.",
]:
    p = doc.add_paragraph(line); p.paragraph_format.space_after = Pt(2)

# --- рекомендованный топ-10 ---
doc.add_heading("Рекомендованный Directum топ-10 для MVP", level=1)
doc.add_paragraph(
    "Приоритет №1 заказчика — поручения и исполнительская дисциплина; запрос — process mining "
    "(узкие места, возвраты, «читают ли документ»), а не «дашборд-картинка». Держим набор компактным "
    "(руководитель ≠ аналитик). Отсюда рекомендация:").paragraph_format.space_after = Pt(4)
for i, item in enumerate(TOP10, 1):
    p = doc.add_paragraph(style="List Number"); p.add_run(item).font.size = Pt(10)
note = doc.add_paragraph()
rnote = note.add_run("Остальные метрики (тяжёлая аналитика: воронка, распределения, приток/отток, загрузка, срезы) "
                     "помечены «Желательно» — кандидаты во 2-й этап.")
rnote.italic = True; rnote.font.size = Pt(9); rnote.font.color.rgb = GREY

doc.add_page_break()

# --- РАЗДЕЛ A ---
doc.add_heading("Раздел A. Стартовая страница — «Обзор для руководителя»", level=1)
doc.add_paragraph("Верхнеуровневый экран: состояние региона «с одного взгляда».").runs[0].italic = True
for idx, (name, value, rec, screens) in enumerate(A, 1):
    doc.add_heading(f"A{idx}. {name}", level=2)
    add_screens(doc, screens)
    add_metric_table(doc, [(f"A{idx}", name, value, rec)])
    doc.add_paragraph()

doc.add_page_break()

# --- РАЗДЕЛ B ---
doc.add_heading("Раздел B. Страница глубокой аналитики процесса", level=1)
doc.add_paragraph("Детальный разбор одного процесса: process mining маршрута, качества и людей.").runs[0].italic = True
for (btitle, screens, rows) in B:
    doc.add_heading(btitle, level=2)
    add_screens(doc, screens)
    add_metric_table(doc, rows)
    doc.add_paragraph()

# --- итог ---
doc.add_heading("Итог", level=1)
tot = doc.add_paragraph()
tot.add_run("Отмечено в MVP: ______ / 10        ").bold = True
tot.add_run("Подпись заказчика: ____________________        Дата: __________")
doc.add_paragraph()
com = doc.add_paragraph(); com.add_run("Комментарии / чего не хватает в списке:").bold = True
for _ in range(3):
    doc.add_paragraph("_" * 95)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
doc.save(OUT)
print("saved:", OUT)

# сводка для проверки
nM = sum(1 for x in A if x[2] == "M") + sum(1 for b in B for r in b[2] if r[3] == "M")
nS = sum(1 for x in A if x[2] == "S") + sum(1 for b in B for r in b[2] if r[3] == "S")
print(f"metrics: A={len(A)}, B={sum(len(b[2]) for b in B)}, total={len(A)+sum(len(b[2]) for b in B)}")
print(f"recommend: Обязательно={nM}, Желательно={nS}")
