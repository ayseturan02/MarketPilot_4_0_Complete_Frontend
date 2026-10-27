// Merkezi stok paneli: harici stok uygulamasından gelen stok tüm kanallara dağıtılır; bir kanaldaki satış diğerlerinden düşülür.
let csData=null,csLoading=false,csBusy=false,csError='',csTimer=null;
const csMarketLabels={trendyol:'Trendyol',hepsiburada:'Hepsiburada',n11:'n11',amazon:'Amazon',boyner:'Boyner',ticimax:'Ticimax'};
const csStatusTones={connected:'green',demo:'blue',readonly:'orange',busy:'orange',error:'red'};
// Directly below the marketplace stores, above the legacy demo channel cards.
const csOriginalIntegrationPanel=integrationPanel;
integrationPanel=function(marketplace){return csOriginalIntegrationPanel(marketplace)+(marketplace?'':centralStockPanel());};

async function csLoad(){if(csLoading)return;csLoading=true;try{csData=await api('integrations/central-stock');csError='';}catch(e){csError=e.message;}finally{csLoading=false;}if(view==='connections')render();csSchedule();}
function csSchedule(){clearTimeout(csTimer);csTimer=null;if(view==='connections'&&csData?.settings.autoSync)csTimer=setTimeout(()=>{csTimer=null;csLoad();},10000);}
// Demo stock jobs finish about two seconds after they are queued, so refresh once more after an action.
function csRefreshSoon(){setTimeout(csLoad,3000);}
async function csPost(path,body,message){if(csBusy)return;csBusy=true;render();try{const result=await api('integrations/central-stock'+path,{method:'POST',body:JSON.stringify(body)});csData=result.view||result;csError='';if(message)notice(typeof message==='function'?message(result):message);csRefreshSoon();}catch(e){notice(e.message);}finally{csBusy=false;render();csSchedule();}}
function csRun(){csPost('/run',{},'Merkezi stok senkronize edildi.');}
function csSaveSettings(event){event.preventDefault();const f=event.target.elements;csPost('/settings',{autoSync:f.autoSync.checked,intervalSeconds:Number(f.intervalSeconds.value),safetyStock:Number(f.safetyStock.value)},'Merkezi stok ayarları kaydedildi.');}
function csDemoSale(event){event.preventDefault();const f=event.target.elements;csPost('/demo-sale',{connectionId:f.store.value,barcode:f.barcode.value,quantity:Number(f.quantity.value)},r=>`${r.order} oluşturuldu; stok diğer kanallarda düşürülüyor.`);}
function csTime(v){return v?new Date(v).toLocaleString('tr-TR',{dateStyle:'short',timeStyle:'short'}):'—';}

function centralStockPanel(){
  if(!csData&&!csLoading&&!csError)csLoad();
  if(!csData)return `<section class="card panel cs-panel"><div class="panel-head"><div><div class="panel-title">Merkezi Stok</div><div class="panel-sub">${csError?esc(csError):'Yükleniyor…'}</div></div></div></section>`;
  if(csData.settings.autoSync&&!csTimer&&!csLoading)csSchedule();
  const d=csData,s=d.settings,demoStores=d.stores.filter(x=>x.mode==='demo');
  const source=d.source.mode==='real'?`<span class="tag tag-green">Harici stok uygulaması · ${esc(d.source.host)}</span>${d.source.writes?'':'<span class="tag tag-orange">Satış bildirimi kapalı</span>'}`:'<span class="tag tag-blue">Demo stok uygulaması</span>';
  const head=d.stores.map(x=>`<th title="${esc(x.name)}">${esc(csMarketLabels[x.marketplace]||x.marketplace)}<small>${esc(x.name.length>18?x.name.slice(0,18)+'…':x.name)}</small></th>`).join('');
  const rows=d.items.map(i=>`<tr><td><strong>${esc(i.name||i.sku)}</strong><small>${esc(i.sku)} · ${esc(i.barcode)}</small></td><td class="cs-num">${i.quantity<0?`<span class="tag tag-red">${i.quantity}</span>`:i.quantity}</td><td class="cs-num"><strong>${i.available}</strong></td><td class="cs-num">${i.unreported||'—'}</td>${i.channels.map(c=>`<td class="cs-num ${c.stock==null?'cs-muted':c.stock===i.available?'cs-ok':'cs-diff'}">${c.stock??'—'}</td>`).join('')}</tr>`).join('');
  const sales=d.sales.map(x=>`<tr><td>${csTime(x.at)}</td><td>${esc(x.storeName)}</td><td>${esc(x.orderNumber)}</td><td>${esc(x.sku)} × ${x.quantity}</td><td>${x.reported?'<span class="tag tag-green">Kaynağa bildirildi</span>':`<span class="tag tag-orange" title="${esc(x.reportError||'')}">Bekliyor</span>`}</td></tr>`).join('');
  const stores=d.stores.filter(x=>x.status).map(x=>`<li><span class="tag tag-${csStatusTones[x.status.status]||'blue'}">${esc(csMarketLabels[x.marketplace]||x.marketplace)}</span> <strong>${esc(x.name)}</strong> — ${esc(x.status.message)}</li>`).join('');
  const sellable=d.items.filter(i=>i.available>0);
  return `<section class="card panel cs-panel">
  <div class="panel-head"><div><div class="panel-title">Merkezi Stok</div><div class="panel-sub">Stok kaynağı: ${source} Bir kanalda satış olduğunda stok merkezde düşer ve kalan miktar diğer tüm kanallara gönderilir.</div></div>
  <div class="cs-actions"><button class="btn btn-primary" onclick="csRun()" ${csBusy||d.running?'disabled':''}>${csBusy||d.running?'Senkronize ediliyor…':'Şimdi senkronize et'}</button><button class="btn" onclick="csLoad()">Yenile</button></div></div>
  <div class="cs-meta"><span>Son çalıştırma: <strong>${csTime(d.lastRunAt)}</strong></span><span>Kaynak okundu: <strong>${csTime(d.lastSourceAt)}</strong></span>${d.lastError?`<span class="tag tag-red">${esc(d.lastError)}</span>`:''}</div>
  <form class="cs-settings" onsubmit="csSaveSettings(event)"><label class="cs-check"><input type="checkbox" name="autoSync" ${s.autoSync?'checked':''}> Otomatik senkronizasyon</label><label>Aralık (sn)<input type="number" name="intervalSeconds" min="30" max="3600" value="${s.intervalSeconds}" required></label><label>Güvenlik stoğu<input type="number" name="safetyStock" min="0" max="100000" value="${s.safetyStock}" required></label><button class="btn" type="submit" ${csBusy?'disabled':''}>Kaydet</button></form>
  <p class="cs-hint">Güvenlik stoğu kadar adet kanallara hiç gösterilmez; eşzamanlı satışlarda fazla satışa karşı tampon sağlar.${d.source.mode==='real'?'':' Gerçek stok uygulamanızı bağlamak için sunucuda <code>Marketplaces:StockSource:BaseUrl</code> ve <code>ApiKey</code> tanımlayın (README → Merkezi stok).'}</p>
  ${d.items.length?`<div class="table-wrap"><table class="table cs-table"><thead><tr><th>Ürün</th><th>Merkez</th><th>Satılabilir</th><th>Bildirilmemiş satış</th>${head}</tr></thead><tbody>${rows}</tbody></table></div><p class="cs-hint"><span class="cs-legend cs-ok"></span>merkezle aynı <span class="cs-legend cs-diff"></span>eşitleniyor veya kanal yönetiyor (FBA, n11depom) <span class="cs-legend cs-muted"></span>bilinmiyor</p>`:'<p class="cs-hint">Henüz stok okunmadı. “Şimdi senkronize et” ile başlayın.</p>'}
  ${demoStores.length&&sellable.length?`<form class="cs-demo" onsubmit="csDemoSale(event)"><strong>Demo satış dene</strong><select name="store">${demoStores.map(x=>`<option value="${esc(x.id)}">${esc(csMarketLabels[x.marketplace]||x.marketplace)} · ${esc(x.name)}</option>`).join('')}</select><select name="barcode">${sellable.map(i=>`<option value="${esc(i.barcode)}">${esc(i.name||i.sku)}</option>`).join('')}</select><input type="number" name="quantity" min="1" max="100" value="1" required><button class="btn" type="submit" ${csBusy?'disabled':''}>Satış oluştur</button></form>`:''}
  <div class="cs-columns"><div><div class="panel-title">Son satışlar</div>${sales?`<div class="table-wrap"><table class="table"><thead><tr><th>Tarih</th><th>Mağaza</th><th>Sipariş</th><th>Ürün</th><th>Kaynak</th></tr></thead><tbody>${sales}</tbody></table></div>`:'<p class="cs-hint">Henüz izlenen yeni satış yok.</p>'}</div>
  <div><div class="panel-title">Kanal durumu</div>${stores?`<ul class="cs-list">${stores}</ul>`:'<p class="cs-hint">Senkronizasyon sonrası görünür.</p>'}<div class="panel-title">Olaylar</div><ul class="cs-list">${d.events.slice(0,12).map(e=>`<li class="cs-${esc(e.level)}">${csTime(e.date)} — ${esc(e.message)}</li>`).join('')||'<li>—</li>'}</ul></div></div>
</section>`;
}
