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
