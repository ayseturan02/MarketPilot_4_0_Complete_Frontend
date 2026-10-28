// Scheduler and durable outbox controls; credentials are never edited here.
let auData=null,auLoading=false,auError='',auSaving=false,auDirty=false,auRetrying=null;
function auCanManage(){return typeof localAccess!=='undefined'&&(!localAccess.configured||localAccess.role==='admin');}
async function auLoad(){
 if(auLoading)return;auLoading=true;
 try{auData=await api('sync/automation');auError='';}catch(error){auError=error.message;}
 finally{auLoading=false;auPaint();}
}
function auPaint(){const panel=document.querySelector('[data-automation]');if(panel&&!auDirty)panel.outerHTML=automationPanel();}
async function auSave(event){
 event.preventDefault();if(auSaving)return;
 const f=event.target.elements;auSaving=true;
 try{
  const response=await api('sync/automation',{method:'POST',body:JSON.stringify({enabled:f.enabled.checked,stockEnabled:f.stockEnabled.checked,intervalSeconds:Number(f.intervalSeconds.value),lookbackDays:Number(f.lookbackDays.value)})});
  auDirty=false;notice(response.message);await auLoad();
 }catch(error){auError=error.message;notice(error.message);}
 finally{auSaving=false;auPaint();}
}
async function auRetry(id){
 if(auRetrying)return;auRetrying=id;auPaint();
 try{await api('sync/outbox/'+encodeURIComponent(id)+'/retry',{method:'POST',body:'{}'});await auLoad();}
 catch(error){auError=error.message;}
 finally{auRetrying=null;auPaint();}
}
function automationPanel(){
 if(!auData&&!auLoading&&!auError)void auLoad();
 const a=auData?.automation,q=auData?.outbox,s=a?.settings,manage=auCanManage();
 const names={idle:'Bekliyor',running:'Çalışıyor',completed:'Tamamlandı',retry:'Tekrar denenecek',queued:'Sırada',dispatching:'Gönderiliyor',waiting:'Doğrulanıyor',uncertain:'Sonuç belirsiz',blocked:'İzin / bağlantı bekliyor',failed:'Hata',superseded:'Yeni hedefle değiştirildi'};
 return '<section class="card panel au-panel" data-automation><div class="panel-head"><div><h2>Otomatik senkronizasyon</h2><p>Sipariş çekme ve stok gönderim işleri sunucuda zamanlanır; tarayıcı kapansa da devam eder.</p></div><button class="btn" onclick="auLoad()">Yenile</button></div>'+
 (auError?'<p role="alert">'+esc(auError)+'</p>':'')+(!auData?'<p>Otomasyon durumu yükleniyor…</p>':
 '<p><span class="tag '+(s.enabled?'tag-green':'tag-orange')+'">'+(s.enabled?'Otomasyon açık':'Otomasyon kapalı')+'</span> '+(a.realStoresEnabled?'Yapılandırılmış gerçek bağlantılar da dahil.':'Gerçek bağlantılar kapalı; yalnız demo mağazalar işlenir.')+'</p>'+
 (manage?'<form class="au-form" oninput="auDirty=true" onsubmit="auSave(event)"><label><input type="checkbox" name="enabled" '+(s.enabled?'checked':'')+'> Zamanlanmış sipariş çekme</label><label><input type="checkbox" name="stockEnabled" '+(s.stockEnabled?'checked':'')+'> Kaynak stoğunu kanallara dağıt</label><label>Aralık (saniye)<input type="number" name="intervalSeconds" min="30" max="3600" required value="'+s.intervalSeconds+'"></label><label>Sipariş kontrol aralığı (gün)<input type="number" name="lookbackDays" min="1" max="14" required value="'+s.lookbackDays+'"></label><button class="btn btn-primary" '+(auSaving?'disabled':'')+'>'+(auSaving?'Kaydediliyor…':'Otomasyonu kaydet')+'</button></form><p>Otomasyonu kapatmak yeni zamanlanmış taramaları durdurur; önceden kabul edilmiş kuyruk işleri tamamlanmaya devam eder.</p>':'<p>Ayarları değiştirmek için yönetici yetkisi gerekir.</p>')+
 '<div class="au-metrics">'+[['Sırada',q.queued],['Doğrulanıyor',q.waiting],['Engellenmiş',q.blocked],['Hata',q.failed],['Tamamlandı',q.completed]].map(([title,value])=>'<div><small>'+title+'</small><strong>'+value+'</strong></div>').join('')+'</div>'+
 (a.stockError?'<p role="alert">'+esc(a.stockError)+'</p>':'')+
 '<h3>Zamanlanmış sipariş okumaları</h3>'+
 (a.stores.length?'<div class="table-wrap">'+tyTable(['Mağaza','Durum','Son başarılı okuma','Sonraki deneme','Kayıt'],a.stores.map(store=>'<tr><td>'+esc(store.storeName)+'</td><td>'+esc(names[store.state]||store.state)+(store.error?'<small>'+esc(store.error)+'</small>':'')+'</td><td>'+esc(integrationDate(store.lastSuccessAt))+'</td><td>'+esc(integrationDate(store.nextRunAt))+'</td><td>'+store.lastCount+'</td></tr>'))+'</div>':'<p>Otomasyon açıldığında mağazaların zaman planı burada görünür.</p>')+
 '<h3>Kalıcı stok gönderim kuyruğu</h3><p>İstek gönderilmesi tamamlanma sayılmaz; kanal sonucu doğrulanır. Yarım kalan gerçek gönderimler kontrol edilmeden yeniden gönderilmez.</p>'+
 (q.recent.length?'<div class="table-wrap">'+tyTable(['Mağaza','Barkod / hedef','Durum','Deneme','İşlem'],q.recent.slice(0,20).map(job=>'<tr><td>'+esc(job.storeName)+'</td><td>'+esc(job.barcode)+' / '+job.quantity+'</td><td>'+esc(names[job.state]||job.state)+(job.error?'<small>'+esc(job.error)+'</small>':'')+'</td><td>'+job.attempts+'</td><td>'+(manage&&['failed','blocked'].includes(job.state)?'<button class="btn btn-small" '+(auRetrying?'disabled':'')+' onclick="auRetry('+jsArg(job.id)+')">Tekrar dene</button>':'—')+'</td></tr>'))+'</div>':'<p>Henüz stok gönderim işi yok.</p>'))+'</section>';
}
const auOriginalSyncPage=syncPage;
syncPage=function(){return auOriginalSyncPage()+automationPanel();};
setInterval(()=>{if(view==='sync'&&!document.hidden&&!modal)void auLoad();},3000);
