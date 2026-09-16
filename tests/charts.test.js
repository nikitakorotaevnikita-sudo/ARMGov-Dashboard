// Тест правил выбора вида визуализации. Чистая функция от формы данных —
// именно она тихо ломается при любой правке, поэтому проверяется отдельно.
// Запуск: node tests/charts.test.js
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'charts.js'), 'utf8');
// charts.js — обычный браузерный скрипт без экспортов. Выполняем его в отдельной
// области и забираем нужные функции наружу. Через eval() было бы короче, но тогда
// тест зависел бы от нестрогого режима модуля — хрупко и неочевидно.
const api = new Function(src + '\nreturn {pickViews: pickViews};')();
const pickViews = api.pickViews;

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

console.log('\nИТОГ: PASS=' + pass + ' FAIL=' + fail);
process.exit(fail ? 1 : 0);
