// Тест правил выбора вида визуализации. Чистая функция от формы данных —
// именно она тихо ломается при любой правке, поэтому проверяется отдельно.
// Запуск: node tests/charts.test.js
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'charts.js'), 'utf8');
// charts.js — обычный браузерный скрипт без экспортов. Выполняем его в отдельной
// области и забираем нужные функции наружу. Через eval() было бы короче, но тогда
// тест зависел бы от нестрогого режима модуля — хрупко и неочевидно.
const api = new Function(src + '\nreturn {pickViews: pickViews, renderView: renderView, viewTitle: viewTitle};')();
const pickViews = api.pickViews, renderView = api.renderView, viewTitle = api.viewTitle;

let pass = 0, fail = 0;
function check(name, cond, detail) {
  if (cond) { pass++; console.log('  [ OK ] ' + name); }
  else { fail++; console.log('  [FAIL] ' + name + (detail ? ' — ' + detail : '')); }
}
function ds(cols, rows) {
  return { cols: cols.map(c => ({ name: c[0], title: c[0], type: c[1] })), rows: rows, rowCount: rows.length };
}

const oneRow = ds([['всего', 'number'], ['просрочено', 'number']], [[1579, 635]]);
check('одна строка только с числами — плитки KPI',
      pickViews(oneRow).def === 'kpi', JSON.stringify(pickViews(oneRow)));

const catNum = ds([['НОР', 'text'], ['просрочено', 'number']], [['ДИТ', 412], ['ДФ', 311]]);
check('текст и число — столбики по умолчанию', pickViews(catNum).def === 'bars');
check('для текста с числом доступны доли', pickViews(catNum).views.indexOf('shares') >= 0);

const timeNum = ds([['месяц', 'date'], ['создано', 'number']], [['2026-01-01', 10], ['2026-02-01', 20]]);
check('дата и число — динамика по умолчанию', pickViews(timeNum).def === 'line');

const catTwoNum = ds([['НОР', 'text'], ['в работе', 'number'], ['просрочено', 'number']],
                     [['ДИТ', 900, 412]]);
check('текст и два числа — столбики', pickViews(catTwoNum).def === 'bars');
// Ревью задачи 5: было проверено только def==='bars', но не то, что при двух
// и более числах вид «доли» не предлагается — тихая поломка, которую условие
// nums.length===1 в pickViews не даст пройти незамеченной.
check('текст и два числа — доли не предлагаются', pickViews(catTwoNum).views.indexOf('shares') < 0);

const twoText = ds([['НОР', 'text'], ['вид', 'text'], ['всего', 'number']], [['ДИТ', 'НПА', 5]]);
check('два текста и число — только таблица', pickViews(twoText).views.join() === 'table');

const noNum = ds([['тема', 'text'], ['автор', 'text']], [['а', 'б']]);
check('без чисел — только таблица', pickViews(noNum).views.join() === 'table');

const negative = ds([['НОР', 'text'], ['дельта', 'number']], [['ДИТ', -5], ['ДФ', 7]]);
check('с отрицательными доли не предлагаются', pickViews(negative).views.indexOf('shares') < 0);

const hinted = Object.assign({}, catNum, { hint: 'table' });
check('допустимая подсказка меняет вид по умолчанию', pickViews(hinted).def === 'table');

const badHint = Object.assign({}, catNum, { hint: 'line' });
check('недопустимая подсказка игнорируется', pickViews(badHint).def === 'bars');

// Форма «текст + дата + число» в таблице спеки не описана: фиксируем, что
// побеждает ось времени — иначе правило молча изменится при следующей правке.
const textDateNum = ds([['НОР', 'text'], ['месяц', 'date'], ['создано', 'number']],
                       [['ДИТ', '2026-01-01', 10]]);
check('дата важнее текстовой подписи — динамика', pickViews(textDateNum).def === 'line');

['kpi','bars','line','shares','table'].forEach(function(v){
  var html = renderView(catNum, v);
  check('renderView отдаёт непустую разметку для вида ' + v,
        typeof html === 'string' && html.length > 20, String(html).slice(0, 40));
});
check('renderView на пустом датасете не падает',
      typeof renderView({cols:[],rows:[]}, 'bars') === 'string');
// Было: viewTitle(v).length > 2 — проходит даже если бы функция возвращала
// английский ключ как есть (все пять ключей длиннее двух символов), то есть
// проверка ничего не утверждала. Фиксируем конкретные русские названия.
check('у каждого вида — конкретное русское название для чипа',
      viewTitle('kpi') === 'Плитки' && viewTitle('bars') === 'Столбики' &&
      viewTitle('line') === 'Динамика' && viewTitle('shares') === 'Доли' &&
      viewTitle('table') === 'Таблица',
      JSON.stringify(['kpi','bars','line','shares','table'].map(viewTitle)));

// §7 спеки: два и более показателя рисуются сериями рядом, а не только первый.
// Признак — в разметке присутствуют оба названия колонок (легенда серий).
var twoSeries = ds([['НОР','text'],['в работе','number'],['просрочено','number']],
                   [['ДИТ',900,412],['ДФ',700,311]]);
var twoHtml = renderView(twoSeries, 'bars');
check('при двух показателях рисуются обе серии',
      twoHtml.indexOf('в работе') >= 0 && twoHtml.indexOf('просрочено') >= 0,
      twoHtml.slice(0, 80));

// Молчаливое обрезание запрещено (§7): при >20 категориях должна быть подпись.
var many = [];
for (var i = 0; i < 40; i++) many.push(['НОР ' + i, 40 - i]);
var manyHtml = renderView(ds([['НОР','text'],['просрочено','number']], many), 'bars');
check('при 40 категориях сказано, сколько показано',
      manyHtml.indexOf('20 из 40') >= 0, manyHtml.slice(-140));

// Ревью круга 1, п.1: отрицательное значение в svgBars получает высоту 0
// (клампится Math.max(0,h)) и подпись рисуется за пределами viewBox — столбик
// и число визуально пропадают. svgBars трогать нельзя (пять экранов на нём),
// поэтому при отрицательных значениях renderBars должен уходить в таблицу,
// где число видно как есть.
var negBars = ds([['НОР', 'text'], ['дельта', 'number']], [['ДИТ', -5], ['ДФ', 7]]);
var negHtml = renderView(negBars, 'bars');
check('отрицательные значения не теряются в столбиках — уходят в таблицу',
      negHtml.indexOf('<table') >= 0 && negHtml.indexOf('-5') >= 0,
      negHtml.slice(0, 160));

// Ревью круга 1, п.2: bool печатался как true/false — не по-русски.
var boolDs = ds([['есть просрочка', 'bool'], ['актив', 'bool']], [[true, false]]);
var boolKpi = renderView(boolDs, 'kpi');
check('логическое значение в плитках — по-русски, не true/false',
      boolKpi.indexOf('да') >= 0 && boolKpi.indexOf('нет') >= 0 &&
      boolKpi.indexOf('true') < 0 && boolKpi.indexOf('false') < 0,
      boolKpi.slice(0, 200));
var boolTable = renderView(boolDs, 'table');
check('логическое значение в таблице — по-русски, не true/false',
      boolTable.indexOf('да') >= 0 && boolTable.indexOf('нет') >= 0 &&
      boolTable.indexOf('true') < 0 && boolTable.indexOf('false') < 0,
      boolTable.slice(0, 200));

// Ревью круга 1, п.4: guard !ds.rows не срабатывает на rows:[] (пустой массив
// истинен) — датасет с колонками, но без строк, должен давать ту же подпись
// «нет данных», что и датасет без колонок, а не пустой график.
// Проверяем на 'table' и 'shares': на 'bars' с одним показателем баг не виден —
// там случайно спасает собственный guard пустоты внутри svgBars.
var noRows = ds([['НОР', 'text'], ['просрочено', 'number']], []);
check('датасет без строк, вид «таблица» — «нет данных», как и без колонок',
      renderView(noRows, 'table').indexOf('нет данных') >= 0,
      renderView(noRows, 'table'));
check('датасет без строк, вид «доли» — «нет данных», как и без колонок',
      renderView(noRows, 'shares').indexOf('нет данных') >= 0,
      renderView(noRows, 'shares'));

console.log('\nИТОГ: PASS=' + pass + ' FAIL=' + fail);
process.exit(fail ? 1 : 0);
