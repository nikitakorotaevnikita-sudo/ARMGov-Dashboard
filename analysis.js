/* =========================================================================
   Представление и рендер ответа POST /api/ai/analysis.
   Зависит от charts.js: esc, pickViews, renderView, viewTitle.
   Клиент не пересчитывает подтверждённые facts — только показывает их.
   ========================================================================= */

var ANALYSIS_LABEL_MAX = 40;

function truncAnalysisLabel(s){
  s=String(s==null?'':s);
  return s.length>ANALYSIS_LABEL_MAX ? s.slice(0,ANALYSIS_LABEL_MAX-1)+'…' : s;
}

// Сервер кодирует title/verifiedText/commentary через HtmlEncode — вставляем как есть.
function serverText(s){ return String(s==null?'':s); }

function mapColType(t){
  if(t==='string')return 'text';
  if(t==='boolean')return 'bool';
  return t||'text';
}

function filterEqual(cell, filterVal){
  if(cell==null||cell===undefined)return filterVal==null;
  if(typeof filterVal==='boolean')return cell===filterVal;
  if(typeof filterVal==='number'&&typeof cell==='number')return cell===filterVal;
  return String(cell)===String(filterVal);
}

function cellPlain(v){
  if(v==null)return null;
  return v;
}

function isTruncatedData(truncation){
  if(!truncation)return false;
  if(truncation.rows||truncation.columns||truncation.bytes)return true;
  return truncation.cells&&truncation.cells.length>0;
}

function findStoredResult(datasets, resultId){
  if(!datasets||!resultId)return null;
  for(var i=0;i<datasets.length;i++){
    if(datasets[i]&&datasets[i].resultId===resultId)return datasets[i];
  }
  return null;
}

function toChartDataset(storedResult, block){
  if(!storedResult||!storedResult.data||!block)return null;
  var data=storedResult.data;
  var columns=data.columns||[];
  var colMap={};
  columns.forEach(function(c,i){colMap[c.name]=i;});

  var rows=(data.rows||[]).slice();
  if(block.equalsFilter){
    rows=rows.filter(function(row){
      for(var k in block.equalsFilter){
        if(!Object.prototype.hasOwnProperty.call(block.equalsFilter,k))continue;
        var idx=colMap[k];
        if(idx==null||idx>=row.length||!filterEqual(row[idx],block.equalsFilter[k]))return false;
      }
      return true;
    });
  }

  var colNames=block.columns||[];
  var cols=colNames.map(function(name){
    var idx=colMap[name];
    var spec=idx!=null?columns[idx]:null;
    return {name:name,title:(spec&&spec.title)||name,type:mapColType(spec&&spec.type)};
  });
  var outRows=rows.map(function(row){
    return colNames.map(function(name){
      var idx=colMap[name];
      return idx!=null&&idx<row.length?cellPlain(row[idx]):null;
    });
  });

  var kind=block.kind||'table';
  return {
    source:data.source||'sql',
    resultId:storedResult.resultId,
    cols:cols,
    rows:outRows,
    truncated:isTruncatedData(data.truncation),
    rowCount:data.rowCount!=null?data.rowCount:(data.rows||[]).length,
    limit:block.limit,
    hint:kind==='table'?null:kind
  };
}

function formatPeriod(from, to){
  if(!from&&!to)return '';
  var f=from?String(from).slice(0,10):'…';
  var t=to?String(to).slice(0,10):'…';
  return f+' — '+t;
}

function adaptAnalysisSteps(steps, datasets){
  return (steps||[]).map(function(s){
    if(!s)return null;
    var ds=findStoredResult(datasets,s.resultId);
    var sql=ds&&ds.data&&ds.data.effectiveSql;
    return {
      action:s.tool||s.status||'',
      tool:s.tool,
      ms:s.elapsedMs,
      error:s.error?(s.error.message||s.error.code):null,
      sql:sql,
      rows:ds&&ds.data&&ds.data.rows?ds.data.rows.length:null
    };
  }).filter(Boolean);
}

function analysisSelectionRequest(originalQuestion, mention, employeeId, selections){
  var sel=(selections||[]).slice();
  sel.push({mention:String(mention||''), employeeId:employeeId});
  return {question:String(originalQuestion||''), selections:sel};
}

function analysisViewModel(response){
  response=response||{};
  var status=response.status||'failed';
  var report=response.report;
  var datasets=response.datasets||[];
  var blocks=[];

  if(report&&report.blocks){
    report.blocks.forEach(function(block, index){
      var stored=findStoredResult(datasets,block.resultId);
      var dataset=stored?toChartDataset(stored,block):null;
      var pv=dataset?pickViews(dataset):{views:['table'],def:'table'};
      var forceTable=status==='incomplete';
      blocks.push({
        index:index,
        kind:block.kind||'table',
        resultId:block.resultId,
        dataset:dataset,
        views:forceTable?['table']:pv.views,
        defView:forceTable?'table':pv.def
      });
    });
  }

  return {
    status:status,
    title:report?report.title:null,
    interpretation:report?report.interpretation:null,
    verifiedText:report&&report.verifiedText?report.verifiedText.slice():[],
    commentary:report?report.commentary:null,
    blocks:blocks,
    warnings:response.warnings||[],
    error:response.error||null,
    clarification:response.clarification||null,
    steps:adaptAnalysisSteps(response.steps, datasets),
    elapsedMs:response.elapsedMs||0,
    datasets:datasets
  };
}

function renderInterpretationMeta(interp){
  if(!interp)return '';
  var parts=[];
  if(interp.label)parts.push(serverText(interp.label));
  if(interp.unit)parts.push('('+serverText(interp.unit)+')');
  var period=formatPeriod(interp.from, interp.to);
  if(period)parts.push('период: '+esc(period));
  if(interp.dateField)parts.push('дата: '+esc(interp.dateField));
  return parts.length?'<div class="analysis-meta sub">'+parts.join(' · ')+'</div>':'';
}

function renderAnalysisBlocks(vm, uiState){
  uiState=uiState||{};
  var blockViews=uiState.blockViews||{};
  if(!vm.blocks.length)return '';
  return vm.blocks.map(function(block){
    var view=blockViews[block.index]!=null?blockViews[block.index]:block.defView;
    var chips=(block.views||[]).map(function(v){
      return '<span class="expl'+(v===view?' on':'')+'" data-block="'+block.index+'" data-view="'+v+'">'+esc(viewTitle(v))+'</span>';
    }).join('');
    var body='';
    if(!block.dataset||!block.dataset.rows||!block.dataset.rows.length){
      body='<div class="sub">нет данных</div>';
    } else {
      body=renderView(block.dataset, view);
    }
    var cap='';
    if(block.dataset){
      cap='<div class="sub" style="margin:8px 0 0">источник: '+esc(block.dataset.source)+
        ' · '+esc(block.dataset.resultId)+
        (block.dataset.truncated?' · показана часть строк':'')+'</div>';
    }
    return '<div class="analysis-block card" style="margin-top:12px">'+
      '<div class="cardhead" style="align-items:center"><div class="row" style="gap:8px;flex-wrap:wrap">'+chips+'</div></div>'+
      body+cap+'</div>';
  }).join('');
}

function renderDatasetTables(datasets){
  if(!datasets||!datasets.length)return '';
  return datasets.map(function(ds){
    if(!ds||!ds.data)return '';
    var block={kind:'table',resultId:ds.resultId,columns:(ds.data.columns||[]).map(function(c){return c.name;})};
    var chart=toChartDataset(ds, block);
    if(!chart)return '';
    return '<div class="analysis-block card" style="margin-top:12px">'+
      '<div class="sub" style="margin:0 0 8px">таблица · '+esc(ds.resultId)+'</div>'+
      renderView(chart,'table')+'</div>';
  }).join('');
}

function renderClarification(clarification, uiState){
  if(!clarification)return '';
  var idx=uiState&&uiState.canvasIndex!=null?uiState.canvasIndex:0;
  var h='<div class="card analysis-clarify" style="border-color:var(--amber)">'+
    '<div class="card-h" style="color:var(--amber)">Нужно уточнение</div>'+
    '<div class="sub" style="margin:0 0 10px">'+esc(clarification.question||'Выберите сотрудника')+'</div>'+
    '<div class="clarify-list">';
  (clarification.candidates||[]).forEach(function(c){
    h+='<button type="button" class="btn clarify-btn" onclick="pickCanvasEntity('+idx+','+
      JSON.stringify(c.name)+','+JSON.stringify(String(c.id))+')">'+
      '<span class="clarify-name">'+esc(c.name)+'</span>'+
      '<span class="clarify-dept sub">'+esc(c.department)+'</span></button>';
  });
  return h+'</div></div>';
}

function renderAnalysisTrace(vm){
  if(!vm.steps||!vm.steps.length)return '';
  var trace=thinkBlock({steps:vm.steps,elapsedMs:vm.elapsedMs,truncated:false}, 'Ход анализа');
  var sqlBlocks='';
  (vm.datasets||[]).forEach(function(ds){
    if(!ds||!ds.data||!ds.data.effectiveSql)return;
    sqlBlocks+='<details class="analysis-sql"><summary>SQL · '+esc(ds.resultId)+'</summary>'+
      '<pre class="analysis-sql-pre">'+esc(ds.data.effectiveSql)+'</pre></details>';
  });
  return trace+sqlBlocks;
}

function renderAnalysis(response, uiState){
  var vm=analysisViewModel(response);
  var h='';

  if(vm.warnings&&vm.warnings.length){
    h+='<div class="analysis-warn sub" style="margin:0 0 10px;color:var(--amber)">'+
      vm.warnings.map(function(w){return esc(w);}).join(' · ')+'</div>';
  }

  if(vm.status==='failed'){
    h+='<div class="card" style="border-color:var(--red)"><div class="card-h" style="color:var(--red)">Не получилось</div>'+
      '<div class="sub" style="margin:0">'+esc(vm.error&&vm.error.message?vm.error.message:'ошибка анализа')+'</div></div>';
    return h+renderAnalysisTrace(vm);
  }

  if(vm.status==='needs_clarification'){
    h+=renderClarification(vm.clarification, uiState);
    return h+renderAnalysisTrace(vm);
  }

  if(vm.status==='no_data'){
    h+='<div class="card"><div class="card-h">Нет данных</div>'+
      '<div class="sub" style="margin:0">За указанный период или условия данных не найдено — нулевой график не показываем.</div></div>';
    return h+renderAnalysisTrace(vm);
  }

  if(vm.status==='incomplete'){
    h+='<div class="analysis-incomplete banner">Частичный результат: отчёт не подтверждён полностью, показаны только таблицы.</div>';
    h+=renderDatasetTables(vm.datasets);
    return h+renderAnalysisTrace(vm);
  }

  if(vm.title){
    h+='<div class="analysis-title card-h" style="margin:0 0 8px">'+serverText(vm.title)+'</div>';
  }
  h+=renderInterpretationMeta(vm.interpretation);

  if(vm.verifiedText&&vm.verifiedText.length){
    h+='<div class="analysis-verified card" style="margin-top:12px">'+
      '<div class="sub" style="margin:0 0 6px;font-weight:600">Подтверждённые показатели</div>'+
      vm.verifiedText.map(function(line){
        return '<div class="analysis-verified-line">'+serverText(line)+'</div>';
      }).join('')+'</div>';
  }

  h+=renderAnalysisBlocks(vm, uiState);

  if(vm.commentary){
    h+='<div class="analysis-commentary card" style="margin-top:12px;border-style:dashed">'+
      '<div class="sub" style="margin:0">'+serverText(vm.commentary)+'</div></div>';
  }

  return h+renderAnalysisTrace(vm);
}
