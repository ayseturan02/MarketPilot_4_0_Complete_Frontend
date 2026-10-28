// Receiver telemetry is local/admin-only. No webhook credentials enter the browser.
let whReceiverData=null,whReceiverLoading=false,whReceiverError='',whReceiverBusy=null;
function whReceiverAllowed(){return typeof localAccess!=='undefined'&&(!localAccess.configured||localAccess.role==='admin');}
async function whReceiverLoad(){
 if(whReceiverLoading||!whReceiverAllowed())return;
 whReceiverLoading=true;
 try{whReceiverData=await api('system/webhooks');whReceiverError='';}
 catch(error){whReceiverError=error.message;}
 finally{whReceiverLoading=false;whReceiverPaint();}
}
function whReceiverPaint(){
 const element=document.querySelector('[data-webhook-receiver]');
 if(element)element.outerHTML=webhookReceiverPanel();
}
async function whReceiverRetry(id){
 if(whReceiverBusy)return;
 whReceiverBusy=id;whReceiverPaint();
 try{await api('system/webhooks/'+encodeURIComponent(id)+'/retry',{method:'POST',body:'{}'});notice('Bildirim yeniden işlenmek üzere sıraya alındı.');}
 catch(error){whReceiverError=error.message;}
 finally{whReceiverBusy=null;await whReceiverLoad();}
}
async function whReceiverCopy(id){
 const endpoint=whReceiverData?.endpoints.find(e=>e.id===id);
 const value=endpoint?.callbackUrl||endpoint?.callbackPath;
 if(!value)return;
 try{await navigator.clipboard.writeText(value);notice(endpoint.callbackUrl?'Bildirim adresi kopyalandı.':'Alıcı yolu kopyalandı. Canlı abonelik için HTTPS adresi ayrıca gerekir.');}
 catch{notice('Adres kopyalanamadı; ekrandaki adresi seçerek kopyalayabilirsin.');}
}
function webhookReceiverPanel(){
 if(!whReceiverAllowed())return '';
 if(!whReceiverData&&!whReceiverLoading&&!whReceiverError)void whReceiverLoad();
 const data=whReceiverData,inbox=data?.inbox;
 const labels={pending:'Bekliyor',processed:'İşlendi',ignored:'Eski / tekrar kayıt',failed:'Hata'};
 const cards=(data?.endpoints||[]).map(endpoint=>'<article class="wh-endpoint"><div class="panel-title">'+esc(endpoint.connectionName||endpoint.id)+'</div>'+
 '<p><span class="tag '+(endpoint.ready?'tag-green':'tag-orange')+'">'+(endpoint.ready?'Alıcı hazır':'Kurulum eksik')+'</span> '+esc(endpoint.environment||'')+'</p>'+
 '<code class="wh-url">POST '+esc(endpoint.callbackUrl||endpoint.callbackPath)+'</code>'+
 '<button class="btn btn-small" onclick="whReceiverCopy('+jsArg(endpoint.id)+')">Adresi kopyala</button>'+
 (endpoint.ready&&!endpoint.publicUrlReady?'<p>Alıcı kodu hazır; dış HTTPS adresi henüz tanımlı değil.</p>':'')+
 (endpoint.issues.length||endpoint.deliveryIssues.length?'<ul>'+[...endpoint.issues,...endpoint.deliveryIssues].map(issue=>'<li>'+esc(issue)+'</li>').join('')+'</ul>':'')+'</article>').join('');
 return '<section class="card panel wh-receiver" data-webhook-receiver><div class="panel-head"><div><h2>Webhook alıcısı</h2><p>Gelen sipariş bildirimleri doğrulanır, kalıcı kuyruğa yazılır ve arka planda sipariş listesine işlenir.</p></div><button class="btn" onclick="whReceiverLoad()" '+(whReceiverLoading?'disabled':'')+'>Yenile</button></div>'+
 (whReceiverError?'<p role="alert">'+esc(whReceiverError)+'</p>':'')+
 (!data?'<p>Alıcı durumu yükleniyor…</p>':
 (cards?'<div class="wh-endpoints">'+cards+'</div>':'<p><strong>Alıcı kodu mevcut; henüz alıcı tanımlanmamış.</strong> Gerçek bağlantı aşamasında mağaza, bağımsız alıcı anahtarı ve HTTPS adresi tanımlanacak.</p><code class="wh-url">POST '+esc(data.exampleCallbackPath)+'</code>')+
 '<div class="wh-metrics">'+[['Bekliyor',inbox.pending],['İşlendi',inbox.processed],['Eski / tekrar',inbox.ignored],['Hata',inbox.failed]].map(([label,value])=>'<div><small>'+label+'</small><strong>'+value+'</strong></div>').join('')+'</div>'+
 '<p>Son bildirim: '+(inbox.lastReceivedAt?esc(integrationDate(inbox.lastReceivedAt)):'Henüz gelmedi')+'</p>'+
 (inbox.recent.length?'<div class="table-wrap">'+tyTable(['Alıcı','Durum','Deneme','Alındı','İşlem'],inbox.recent.map(item=>'<tr><td>'+esc(item.endpointId)+'</td><td>'+esc(labels[item.state]||item.state)+(item.error?'<small>'+esc(item.error)+'</small>':'')+'</td><td>'+item.attempts+'</td><td>'+esc(integrationDate(item.receivedAt))+'</td><td>'+(item.state==='failed'?'<button class="btn btn-small" '+(whReceiverBusy?'disabled':'')+' onclick="whReceiverRetry('+jsArg(item.id)+')">Tekrar işle</button>':'—')+'</td></tr>'))+'</div>':'<p>Gelen bildirim geçmişi burada görünecek.</p>'))+'</section>';
}
const whOriginalSyncPage=syncPage;
syncPage=function(){return whOriginalSyncPage()+webhookReceiverPanel();};
const whOriginalTrendyolCenter=trendyolCenter;
trendyolCenter=function(){return whOriginalTrendyolCenter()+(tyTab==='overview'?webhookReceiverPanel():'');};
setInterval(()=>{if(!document.hidden&&!modal&&(view==='sync'||view==='trendyol'&&tyTab==='overview'))void whReceiverLoad();},10000);
