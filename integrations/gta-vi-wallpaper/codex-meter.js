(function () {
  'use strict';
  var root=document.getElementById('codex-meter'),base='http://127.0.0.1:19188';
  var token=(window.CODEX_METER_CONNECTION||{}).token;
  var $=function(id){return document.getElementById('meter-'+id);};
  var online=false,pending=false,paused=false,timer=null,generation=0,state=null,detailsSignature=null;
  var compact=new Intl.NumberFormat('fr-FR',{notation:'compact',maximumFractionDigits:2});
  var dollars=new Intl.NumberFormat('fr-FR',{style:'currency',currency:'USD',maximumFractionDigits:2});
  function number(value){return typeof value==='number'&&Number.isFinite(value)?compact.format(value):'—';}
  function money(value){return typeof value==='number'&&Number.isFinite(value)?dollars.format(value):'—';}
  function layout(){window.dispatchEvent(new Event('conradlayout'));}
  function duration(minutes){return !minutes?'QUOTA':minutes%1440===0?minutes/1440+' JOURS':minutes%60===0?minutes/60+' HEURES':minutes+' MIN';}
  function remaining(quota){return quota&&Number.isFinite(quota.usedPercent)?Math.max(0,Math.min(100,100-quota.usedPercent)):null;}
  function reset(quota){
    if(!quota||!quota.resetsAt)return 'Reset indisponible';
    var date=new Date(quota.resetsAt*1000);
    if(!Number.isFinite(date.getTime()))return 'Reset indisponible';
    if(date.getTime()<Date.now())return 'Reset à actualiser';
    return 'Reset '+date.toLocaleString('fr-FR',{day:'2-digit',month:'2-digit',hour:'2-digit',minute:'2-digit'});
  }
  async function request(path,post){
    var controller=new AbortController(),timeout=setTimeout(function(){controller.abort();},post?90000:5000);
    try{
      var response=await fetch(base+path,{method:post?'POST':'GET',cache:'no-store',headers:{'X-Codex-Meter-Token':token},signal:controller.signal});
      if(!response.ok)throw new Error(response.status===409?'Actualisation déjà en cours.':'Codex Meter indisponible.');
      return await response.json();
    }finally{clearTimeout(timeout);}
  }
  function controls(){ $('refresh').disabled=!online||pending||!!(state&&state.refreshing); }
  function error(text){$('error').textContent=text||'';$('error').hidden=!text;layout();}
  function quota(name,value){
    var left=remaining(value);
    $(name+'-label').textContent=duration(value&&value.windowDurationMins);
    $(name).textContent=left===null?'—':Math.round(left)+'%';
    $(name+'-bar').style.width=(left===null?0:left)+'%';
    $(name+'-reset').textContent=value?reset(value):'—';
  }
  function node(tag,text){var el=document.createElement(tag);el.textContent=text;return el;}
  function render(next){
    state=next;
    var s=next&&next.snapshot;
    root.classList.toggle('online',online&&!!s);
    var fetched=s?new Date(s.fetchedAt):null;
    var stale=s&&Date.now()-fetched.getTime()>((next.refreshIntervalMinutes||15)+1)*60000;
    $('status').textContent=!online?'CODEX METER HORS LIGNE':next.refreshing?'ACTUALISATION…':!s?'EN ATTENTE DE CODEX':next.error||stale?'DERNIÈRE MESURE':'MAJ '+fetched.toLocaleTimeString('fr-FR',{hour:'2-digit',minute:'2-digit'});
    var limits=s&&s.limits||[],main=limits.find(function(l){return l.id==='codex';})||limits[0];
    quota('primary',main&&main.primary);quota('secondary',main&&main.secondary);
    $('secondary-card').hidden=!!s&&!(main&&main.secondary);
    root.querySelector('.meter-overview').classList.toggle('single-quota',$('secondary-card').hidden);
    $('today').textContent=number(s&&s.todayTokens);
    $('today-cost').textContent=s&&s.estimatedPricing&&s.todayApiDollars===0?'—':money(s&&s.todayApiDollars);
    $('today-cost').title=s&&s.estimatedPricing?'Estimation partielle : tarifs approximatifs ou manquants.':'Équivalent API estimé, pas une facture.';
    $('total').textContent=number(s&&s.lifetimeTokens);$('total-cost').textContent=money(s&&s.totalApiDollars);
    controls();
    var signature=JSON.stringify(s);
    if(signature===detailsSignature)return;
    detailsSignature=signature;
    $('limit-list').replaceChildren();
    limits.forEach(function(limit){
      var row=node('div','');row.className='meter-limit-row';row.appendChild(node('strong',limit.name));
      [limit.primary,limit.secondary].forEach(function(q){
        var column=node('span',''),left=remaining(q);column.append(node('b',left===null?'—':Math.round(left)+'% restant · '+duration(q.windowDurationMins)),node('br',''),node('span',q?reset(q):''));row.appendChild(column);
      });$('limit-list').appendChild(row);
    });
    $('credits').textContent=s&&s.resetCredits!==null?s.resetCredits+' crédit(s) de reset disponible(s)':'';
    $('model-list').replaceChildren();
    var models=s&&s.models||[];
    models.slice().sort(function(a,b){return b.totalTokens-a.totalTokens;}).forEach(function(model){
      var row=node('div','');row.className='meter-model-row';var name=node('span',model.model);name.appendChild(node('small',model.effort||''));
      row.append(name,node('span',number(model.totalTokens)),node('span',money(model.dollarAmount)));$('model-list').appendChild(row);
    });
    $('model-note').textContent=!models.length?'Détail des modèles indisponible.':'Tokens et équivalent API cumulés · estimation, pas une facture.'+(s.estimatedPricing?' Certains tarifs sont approximatifs ou manquants.':'');
    layout();
  }
  async function poll(){
    clearTimeout(timer);if(paused||document.hidden)return;
    var current=generation;
    try{var next=await request('/state');if(current!==generation)return;online=true;render(next);}
    catch(_){if(current!==generation)return;online=false;render(null);}
    finally{if(current===generation&&!paused&&!document.hidden)timer=setTimeout(poll,online?15000:5000);}
  }
  ['limits','models'].forEach(function(name){
    $(name+'-button').addEventListener('click',function(){
      var open=$(name).hidden;
      ['limits','models'].forEach(function(other){$(other).hidden=other!==name||!open;$(other+'-button').setAttribute('aria-expanded',String(other===name&&open));});
      if(open)window.dispatchEvent(new CustomEvent('dashboarddrawer',{detail:'codex'}));
      layout();
    });
  });
  window.addEventListener('dashboarddrawer',function(event){if(event.detail==='codex')return;['limits','models'].forEach(function(name){$(name).hidden=true;$(name+'-button').setAttribute('aria-expanded','false');});layout();});
  $('refresh').addEventListener('click',async function(){
    if(!online||pending||state&&state.refreshing)return;
    pending=true;controls();error(null);$('status').textContent='ACTUALISATION…';
    try{var next=await request('/refresh',true);render(next);if(next.error)error(next.error);}
    catch(e){error(e.name==='AbortError'?'Actualisation trop longue. Réessayez depuis Codex Meter.':e.message);}
    finally{pending=false;controls();generation++;poll();}
  });
  function resume(){generation++;clearTimeout(timer);if(!paused&&!document.hidden&&token)poll();}
  window.addEventListener('conradpause',function(event){paused=event.detail;resume();});
  document.addEventListener('visibilitychange',resume);
  render(null);if(token)poll();else $('status').textContent='CODEX METER À ASSOCIER';
})();
