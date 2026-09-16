/* =========================================================================
   Визуализация датасета ИИ-харнесса. Виды выбираются по ФОРМЕ данных:
   типы колонок ставит сервер, клиент лишь смотрит на них. Подсказка модели
   (ds.hint) сужает выбор внутри допустимого, но не расширяет его.
   ========================================================================= */

// Какие виды допустимы для этого датасета и какой из них показать первым.
function pickViews(ds){
  var cols=(ds&&ds.cols)||[], rows=(ds&&ds.rows)||[];
  var nums=cols.filter(function(c){return c.type==='number';});
  var texts=cols.filter(function(c){return c.type==='text';});
  var dates=cols.filter(function(c){return c.type==='date';});
  var views=[], def='table';

  if(!nums.length){ views=['table']; def='table'; }
  else if(rows.length===1 && !texts.length && !dates.length){ views=['kpi','table']; def='kpi'; }
  else if(dates.length===1 && nums.length>=1 && nums.length<=3){ views=['line','bars','table']; def='line'; }
  else if(texts.length===1 && nums.length>=1){
    views=['bars','table']; def='bars';
    // Доли осмысленны только для неотрицательных значений одной меры.
    var neg=rows.some(function(r){return r.some(function(v){return typeof v==='number'&&v<0;});});
    if(!neg && nums.length===1) views.splice(1,0,'shares');
  }
  else { views=['table']; def='table'; }

  if(ds&&ds.hint&&views.indexOf(ds.hint)>=0) def=ds.hint;
  return {views:views, def:def};
}

/* ---------- SVG charts ---------- */
function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,function(c){return{'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c];});}
function rect(x,y,w,h,f,r){return '<rect x="'+x+'" y="'+y+'" width="'+w+'" height="'+Math.max(0,h)+'" fill="'+f+'"'+(r?' rx="'+r+'"':'')+'/>';}
function txt(x,y,s,a,sz,f){return '<text x="'+x+'" y="'+y+'" text-anchor="'+(a||'middle')+'" font-size="'+(sz||11)+'" fill="'+(f||'var(--muted)')+'" font-family="var(--font)">'+esc(s)+'</text>';}
function svgStack(data,h,showVal,minSlots){
  h=h||180; if(!data||!data.length)return '<div class="sub">нет данных</div>';
  var n=data.length,slots=Math.max(n,minSlots||0);
  var pt=18,pb=(showVal===false?10:34),pl=8,pr=8,ch=h-pt-pb,W=Math.max(slots*46,260);
  var mx=Math.max(1,Math.max.apply(null,data.map(function(d){return (d.ontime||0)+(d.overdue||0);})));
  var bw=(W-pl-pr)/slots,bw2=Math.min(bw-12,30);
  var body=data.map(function(d,i){
    var cx=pl+i*bw+bw/2,x=cx-bw2/2,total=(d.ontime||0)+(d.overdue||0);
    var hov=(d.overdue||0)/mx*ch,hon=(d.ontime||0)/mx*ch,y0=pt+ch,s='';
    var yov=y0-hov; if(hov>0)s+=rect(x,yov,bw2,hov,'var(--red)');
    var yon=yov-hon; if(hon>0)s+=rect(x,yon,bw2,hon,'var(--green)',2);
    if(showVal!==false){s+=txt(cx,(total>0?Math.min(yov,yon):y0)-5,total,'middle',11,'var(--text)');s+=txt(cx,h-14,d.label||d.month,'middle',10,'var(--subtle)');}
    return s;
  }).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+body+'</svg>';
}
// спарклайн дисциплины: линия доли «в срок» (ontime/(ontime+overdue)) по месяцам
function svgSpark(data,h){
  h=h||42;if(!data||!data.length)return '<span class="subtle" style="font-size:12px">—</span>';
  var pts=data.map(function(d){var t=(d.ontime||0)+(d.overdue||0);return t>0?100*(d.ontime||0)/t:null;});
  var idx=[];pts.forEach(function(v,i){if(v!=null)idx.push(i);});
  if(idx.length<2)return '<span class="subtle" style="font-size:12px">мало данных</span>';
  var W=200,pad=5,n=pts.length,ch=h-pad*2;
  var X=function(i){return pad+(n>1?i/(n-1):0)*(W-pad*2);};
  var Y=function(v){return pad+(1-v/100)*ch;};
  var seg=idx.map(function(i,k){return (k?'L':'M')+X(i).toFixed(1)+' '+Y(pts[i]).toFixed(1);}).join(' ');
  var li=idx[idx.length-1],last=pts[li],lx=X(li),ly=Y(last);
  var col=last>=75?'var(--green)':(last>=50?'var(--amber)':'var(--red)');
  var area=seg+' L'+lx.toFixed(1)+' '+(pad+ch).toFixed(1)+' L'+X(idx[0]).toFixed(1)+' '+(pad+ch).toFixed(1)+' Z';
  var base=Y(50).toFixed(1);
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="none" style="display:block;max-width:240px">'+
    '<line x1="'+pad+'" y1="'+base+'" x2="'+(W-pad)+'" y2="'+base+'" stroke="var(--border)" stroke-width="1" stroke-dasharray="4 4" vector-effect="non-scaling-stroke"/>'+
    '<path d="'+area+'" fill="'+col+'" opacity="0.12"/>'+
    '<path d="'+seg+'" fill="none" stroke="'+col+'" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke"/>'+
    '<circle cx="'+lx.toFixed(1)+'" cy="'+ly.toFixed(1)+'" r="2.6" fill="'+col+'" vector-effect="non-scaling-stroke"/></svg>';
}
function svgBars(data,h,color){
  h=h||180; if(!data||!data.length)return '<div class="sub">нет данных</div>';
  var pt=18,pb=34,pl=8,pr=8,ch=h-pt-pb,n=data.length,W=Math.max(n*64,280);
  var mx=Math.max(1,Math.max.apply(null,data.map(function(d){return d.value;})));
  var bw=(W-pl-pr)/n,bw2=Math.min(bw-16,46);
  var body=data.map(function(d,i){
    var cx=pl+i*bw+bw/2,x=cx-bw2/2,bh=d.value/mx*ch,y=pt+ch-bh;
    return rect(x,y,bw2,bh,color||'var(--accent)',3)+txt(cx,y-5,d.value,'middle',11,'var(--text)')+txt(cx,h-14,d.label,'middle',10,'var(--subtle)');
  }).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+body+'</svg>';
}
function legend(col,v,lbl){return '<span class="muted"><span style="display:inline-block;width:10px;height:10px;border-radius:2px;background:'+col+';margin-right:5px"></span><b style="color:'+col+'">'+v+'</b> '+lbl+'</span>';}

/* ---------- Рендереры видов ---------- */
// Потолок категорий на графике. Больше двадцати столбиков глазами не читаются,
// но обрезать молча нельзя: иначе руководитель решит, что подразделений двадцать.
var CHART_TOP_N = 20;
// Палитра серий — только токены из tokens.css, новых цветов не вводим.
var CHART_PAL = ['var(--accent)','var(--green)','var(--amber)','var(--red)','var(--blue)','var(--subtle)'];

function viewTitle(v){
  return {kpi:'Плитки',bars:'Столбики',line:'Динамика',shares:'Доли',table:'Таблица'}[v]||v;
}

// Индексы колонок: первая текстовая — подпись, первая датовая — ось времени, числовые — меры.
function dsRoles(ds){
  var cols=ds.cols||[];
  var label=-1,date=-1,nums=[];
  cols.forEach(function(c,i){
    if(c.type==='number')nums.push(i);
    else if(c.type==='date'&&date<0)date=i;
    else if(c.type==='text'&&label<0)label=i;
  });
  return {label:label,date:date,nums:nums};
}

function renderView(ds,view){
  if(!ds||!ds.cols||!ds.cols.length||!ds.rows)return '<div class="sub">нет данных</div>';
  if(view==='kpi')return renderKpi(ds);
  if(view==='bars')return renderBars(ds);
  if(view==='line')return renderLine(ds);
  if(view==='shares')return renderShares(ds);
  return renderTable(ds);
}

function renderKpi(ds){
  var r=ds.rows[0]||[];
  return '<div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(180px,1fr))">'+
    ds.cols.map(function(c,i){
      return '<div class="kpi"><div class="kpi-label">'+esc(c.title)+'</div>'+
             '<div class="kpi-value">'+esc(String(r[i]==null?'—':r[i]))+'</div></div>';
    }).join('')+'</div>';
}

// Подпись об усечении: руководитель должен видеть, что категорий было больше.
function topNote(shown,total){
  return shown>=total?'':'<div class="sub" style="margin:6px 0 0">показаны '+shown+
    ' из '+total+', остальные в таблице</div>';
}

function renderBars(ds){
  var R=dsRoles(ds); if(!R.nums.length)return renderTable(ds);
  var li=R.label>=0?R.label:(R.date>=0?R.date:0);
  // Сортировка по первому показателю: руководителя интересует «у кого хуже».
  var rows=ds.rows.slice().sort(function(a,b){
    return (Number(b[R.nums[0]])||0)-(Number(a[R.nums[0]])||0);});
  var total=rows.length; if(total>CHART_TOP_N)rows=rows.slice(0,CHART_TOP_N);
  var note=topNote(rows.length,total);

  if(R.nums.length===1){
    var mi=R.nums[0];
    return svgBars(rows.map(function(r){
      return {label:String(r[li]),value:Number(r[mi])||0};}),260)+note;
  }
  var labels=rows.map(function(r){return String(r[li]);});
  var series=R.nums.map(function(ci){
    return {name:(ds.cols[ci].title||ds.cols[ci].name),
            values:rows.map(function(r){return Number(r[ci])||0;})};});
  return svgGroupedBars(labels,series,260)+note;
}

// Несколько показателей по одной категории — серии РЯДОМ, не стопкой.
// Стопка уместна для частей целого; два произвольных показателя («в работе» и
// «просрочено») частями целого не являются, и стопка соврала бы про сумму.
function svgGroupedBars(labels,series,h){
  h=h||260; if(!labels.length||!series.length)return '<div class="sub">нет данных</div>';
  var pt=18,pb=34,pl=8,pr=8,ch=h-pt-pb,n=labels.length,k=series.length;
  var W=Math.max(n*(26*k+34),300);
  var mx=1; series.forEach(function(se){se.values.forEach(function(v){if(v>mx)mx=v;});});
  var gw=(W-pl-pr)/n, bw=Math.max(6,Math.min((gw-16)/k,26));
  var body=labels.map(function(lb,i){
    var x0=pl+i*gw+(gw-bw*k)/2, s='';
    series.forEach(function(se,j){
      var v=se.values[i]||0, bh=v/mx*ch, x=x0+j*bw, y=pt+ch-bh;
      s+=rect(x+1,y,bw-2,bh,CHART_PAL[j%CHART_PAL.length],2)+
         txt(x+bw/2,y-4,v,'middle',10,'var(--text)');
    });
    return s+txt(pl+i*gw+gw/2,h-14,lb,'middle',10,'var(--subtle)');
  }).join('');
  var leg=series.map(function(se,j){
    return '<span class="muted" style="font-size:12px"><span style="display:inline-block;width:10px;'+
      'height:10px;border-radius:2px;background:'+CHART_PAL[j%CHART_PAL.length]+
      ';margin-right:5px"></span>'+esc(se.name)+'</span>';}).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+
    rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+body+'</svg>'+
    '<div class="row" style="gap:14px;margin-top:6px">'+leg+'</div>';
}

function renderLine(ds){
  var R=dsRoles(ds); if(R.date<0||!R.nums.length)return renderBars(ds);
  var mi=R.nums[0];
  var data=ds.rows.map(function(r){return {label:String(r[R.date]).slice(0,10),value:Number(r[mi])||0};})
                  .sort(function(a,b){return a.label<b.label?-1:1;});
  var W=Math.max(data.length*56,320),h=260,pt=18,pb=34,pl=40,pr=10,ch=h-pt-pb;
  var mx=Math.max.apply(null,data.map(function(d){return d.value;}))||1;
  var X=function(i){return pl+(data.length>1?i/(data.length-1):0.5)*(W-pl-pr);};
  var Y=function(v){return pt+(1-v/mx)*ch;};
  var seg=data.map(function(d,i){return (i?'L':'M')+X(i).toFixed(1)+' '+Y(d.value).toFixed(1);}).join(' ');
  var dots=data.map(function(d,i){return '<circle cx="'+X(i).toFixed(1)+'" cy="'+Y(d.value).toFixed(1)+'" r="3" fill="var(--accent)"/>'+
    txt(X(i),Y(d.value)-8,d.value,'middle',11,'var(--text)')+txt(X(i),h-14,d.label,'middle',10,'var(--subtle)');}).join('');
  return '<svg viewBox="0 0 '+W+' '+h+'" width="100%" height="'+h+'" preserveAspectRatio="xMidYMid meet" style="max-width:100%;display:block">'+
    rect(pl,pt+ch,W-pl-pr,1,'var(--border)')+
    '<path d="'+seg+'" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linejoin="round"/>'+dots+'</svg>';
}

function renderShares(ds){
  var R=dsRoles(ds); if(!R.nums.length)return renderTable(ds);
  var li=R.label>=0?R.label:0, mi=R.nums[0];
  var data=ds.rows.map(function(r){return {label:String(r[li]),value:Number(r[mi])||0};})
                  .sort(function(a,b){return b.value-a.value;});
  var total=data.reduce(function(s,d){return s+d.value;},0)||1;
  var pal=CHART_PAL;
  if(data.length<=6){
    // Кольцо: на демо «долю» привычно видеть круглой, но при большом числе
    // категорий круг перестаёт читаться — тогда ниже рисуются полосы.
    var cx=110,cy=110,r=80,sw=28,off=0,circ=2*Math.PI*r;
    var arcs=data.map(function(d,i){
      var len=d.value/total*circ;
      var s='<circle cx="'+cx+'" cy="'+cy+'" r="'+r+'" fill="none" stroke="'+pal[i%pal.length]+'" stroke-width="'+sw+
            '" stroke-dasharray="'+len.toFixed(1)+' '+(circ-len).toFixed(1)+'" stroke-dashoffset="'+(-off).toFixed(1)+
            '" transform="rotate(-90 '+cx+' '+cy+')"/>';
      off+=len; return s;
    }).join('');
    var leg=data.map(function(d,i){
      return '<div style="display:flex;align-items:center;gap:8px;padding:3px 0;font-size:13px">'+
        '<span style="width:10px;height:10px;border-radius:2px;background:'+pal[i%pal.length]+'"></span>'+
        '<span style="flex:1">'+esc(d.label)+'</span><b>'+d.value+'</b>'+
        '<span class="subtle">'+(100*d.value/total).toFixed(1)+'%</span></div>';
    }).join('');
    return '<div style="display:flex;gap:24px;align-items:center;flex-wrap:wrap">'+
      '<svg viewBox="0 0 220 220" width="220" height="220">'+arcs+'</svg><div style="flex:1;min-width:220px">'+leg+'</div></div>';
  }
  return data.map(function(d,i){
    var p=100*d.value/total;
    return '<div style="display:flex;align-items:center;gap:10px;padding:4px 0;font-size:13px">'+
      '<span style="width:180px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'+esc(d.label)+'</span>'+
      '<span style="flex:1;height:10px;background:var(--surface-2);border-radius:5px;overflow:hidden">'+
      '<span style="display:block;height:100%;width:'+p.toFixed(1)+'%;background:'+pal[i%pal.length]+'"></span></span>'+
      '<b style="min-width:48px;text-align:right">'+d.value+'</b>'+
      '<span class="subtle" style="min-width:48px;text-align:right">'+p.toFixed(1)+'%</span></div>';
  }).join('');
}

function renderTable(ds){
  var head=ds.cols.map(function(c){return '<th>'+esc(c.title)+'</th>';}).join('');
  var body=ds.rows.map(function(r){
    return '<tr>'+r.map(function(v,i){
      var num=ds.cols[i]&&ds.cols[i].type==='number';
      return '<td style="text-align:'+(num?'right':'left')+'">'+esc(String(v==null?'—':v))+'</td>';
    }).join('')+'</tr>';
  }).join('');
  return '<div style="overflow:auto;max-height:420px"><table style="width:100%"><thead><tr>'+head+'</tr></thead><tbody>'+body+'</tbody></table></div>';
}
