// Тест представления ответа /api/ai/analysis. Запуск: node tests/analysis.test.js
const fs = require('fs');
const path = require('path');
const assert = require('node:assert/strict');

const root = path.join(__dirname, '..');
const chartsSrc = fs.readFileSync(path.join(root, 'charts.js'), 'utf8');
const charts = new Function(chartsSrc + '\nreturn {pickViews,renderView,viewTitle,esc};')();
global.pickViews = charts.pickViews;
global.renderView = charts.renderView;
global.viewTitle = charts.viewTitle;
global.esc = charts.esc;
global.thinkBlock = function(m, label){
  label = label || 'Размышления';
  if(!m.steps||!m.steps.length)return '';
  return '<details class="think"><summary>'+label+' · '+m.steps.length+'</summary></details>';
};

const api = new Function(
  fs.readFileSync(path.join(root, 'analysis.js'), 'utf8') +
  '\nreturn {analysisViewModel,renderAnalysis,analysisSelectionRequest,toChartDataset};')();

let pass = 0, fail = 0;
function check(name, cond, detail) {
  if (cond) { pass++; console.log('  [ OK ] ' + name); }
  else { fail++; console.log('  [FAIL] ' + name + (detail ? ' — ' + detail : '')); }
}

function col(name, type, title) {
  return { name, type, title: title || name };
}

function stored(id, columns, rows, extra) {
  return Object.assign({
    runId: 'run1',
    resultId: id,
    data: {
      source: 'sql',
      columns: columns,
      rows: rows,
      effectiveSql: 'select 1',
      retrievedAt: '2026-09-01T00:00:00+03:00',
      truncation: { rows: false, columns: false, cells: [], bytes: false },
      warnings: []
    }
  }, extra || {});
}

// XSS и legacy chart.from
var failHtml = api.renderAnalysis({
  runId: 'x', status: 'failed', report: null, datasets: [], steps: [],
  warnings: [], error: { code: 'bad_report', message: '<script>alert(1)</script>' }
});
check('ошибка экранирует script', !failHtml.includes('<script>'), failHtml.slice(0, 80));
check('нет chart.from в разметке', !failHtml.includes('chart.from'));

// completed, 2 blocks
var ds1 = stored('r1', [col('name', 'string'), col('n', 'number')], [['А', 5], ['Б', 3]]);
var ds2 = stored('r2', [col('dept', 'string'), col('cnt', 'number')], [['ДИТ', 10]]);
var completed = {
  runId: 'r', status: 'completed', elapsedMs: 1200, warnings: [],
  datasets: [ds1, ds2],
  steps: [{ n: 1, tool: 'execute_sql', status: 'ok', elapsedMs: 40, resultId: 'r1', error: null }],
  report: {
    title: 'Сравнение',
    interpretation: { metricId: 'm', label: 'Поручения', unit: 'шт', from: '2025-09-01', to: '2026-09-01', dateField: 'created' },
    verifiedText: ['У А — 5, у Б — 3'],
    facts: {},
    blocks: [
      { kind: 'bars', resultId: 'r1', columns: ['name', 'n'], equalsFilter: null, limit: null },
      { kind: 'table', resultId: 'r2', columns: ['dept', 'cnt'], equalsFilter: null, limit: null }
    ],
    commentary: 'Комментарий модели, не проверен: возможна сезонность'
  }
};
var cHtml = api.renderAnalysis(completed);
check('completed — заголовок отчёта', cHtml.indexOf('Сравнение') >= 0);
check('completed — два блока', (cHtml.match(/analysis-block/g) || []).length >= 2);
check('completed — подтверждённые показатели отдельно', cHtml.indexOf('Подтверждённые показатели') >= 0);
check('completed — комментарий с маркировкой', cHtml.indexOf('не проверен') >= 0);
check('completed — период над графиками', cHtml.indexOf('2025-09-01') >= 0);

// no_data
var noDataHtml = api.renderAnalysis({ runId: 'n', status: 'no_data', report: null, datasets: [], steps: [], warnings: [] });
check('no_data — явное отсутствие', noDataHtml.indexOf('Нет данных') >= 0);
check('no_data — без svg/chart primitives', noDataHtml.indexOf('<svg') < 0);

// clarification 2 employees
var clarHtml = api.renderAnalysis({
  runId: 'c', status: 'needs_clarification', report: null, datasets: [], steps: [], warnings: [],
  clarification: {
    question: 'Кого выбрать?',
    candidates: [
      { id: 101, name: 'Иванов Иван', department: 'ДИТ' },
      { id: 202, name: 'Иванов Петр', department: 'ДФ' }
    ]
  }
}, { canvasIndex: 2 });
check('clarification — два кандидата', (clarHtml.match(/clarify-btn/g) || []).length === 2);
check('clarification — ФИО и подразделение', clarHtml.indexOf('ДИТ') >= 0 && clarHtml.indexOf('ДФ') >= 0);
check('clarification — onclick с индексом', clarHtml.indexOf('pickCanvasEntity(2,') >= 0);

// incomplete + table
var incDs = stored('r9', [col('x', 'string'), col('y', 'number')], [['a', 1]]);
var incHtml = api.renderAnalysis({
  runId: 'i', status: 'incomplete', report: null,
  datasets: [incDs], steps: [], warnings: ['отчёт не принят']
});
check('incomplete — предупреждение', incHtml.indexOf('Частичный результат') >= 0);
check('incomplete — только таблица', incHtml.indexOf('<table') >= 0);

// unsafe integer id in clarification
var bigId = '9007199254740992';
var bigHtml = api.renderAnalysis({
  runId: 'b', status: 'needs_clarification', report: null, datasets: [], steps: [], warnings: [],
  clarification: { question: 'Кого?', candidates: [{ id: bigId, name: 'Большой ID', department: 'X' }] }
});
check('unsafe integer — id не теряет точность в onclick', bigHtml.indexOf(bigId) >= 0);

// partial shares (truncated dataset)
var shareDs = stored('rs', [col('name', 'string'), col('n', 'number')], [['A', 100], ['B', 100]]);
shareDs.data.truncation = { rows: true, columns: false, cells: [], bytes: false };
shareDs.data.rowCount = 500;
var shareReport = Object.assign({}, completed.report, {
  blocks: [{ kind: 'shares', resultId: 'rs', columns: ['name', 'n'], equalsFilter: null, limit: null }]
});
var shareHtml = api.renderAnalysis(Object.assign({}, completed, { datasets: [shareDs], report: shareReport }));
check('partial shares — честная оговорка', shareHtml.indexOf('не полный итог') >= 0);

// analysisSelectionRequest
var req = api.analysisSelectionRequest('Сравни Иванова', 'Иванов', 101, [{ mention: 'Петров', employeeId: 202 }]);
check('selection request — исходный вопрос', req.question === 'Сравни Иванова');
check('selection request — накопленные selections', req.selections.length === 2 && req.selections[1].employeeId === 101);

// toChartDataset preserves source/resultId
var chart = api.toChartDataset(ds1, { kind: 'bars', resultId: 'r1', columns: ['name', 'n'] });
check('toChartDataset — source и resultId', chart.source === 'sql' && chart.resultId === 'r1');
check('toChartDataset — cols/rows', chart.cols.length === 2 && chart.rows.length === 2);

// viewModel
var vm = api.analysisViewModel(completed);
check('viewModel — два блока', vm.blocks.length === 2);
check('viewModel — status', vm.status === 'completed');

// trace label
check('ход анализа — подпись', cHtml.indexOf('Ход анализа') >= 0);

console.log('\nИТОГ: PASS=' + pass + ' FAIL=' + fail);
process.exit(fail ? 1 : 0);
