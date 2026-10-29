// Applied filters are sent to the server. No all-record downloads or per-keystroke requests.
const lfStates=new Map(),lfControllers=new Map();
function lfDate(offset=0){return new Date(Date.now()+3*3600000+offset*86400000).toISOString().slice(0,10);}
function lfState(scope,tab){const key=scope+':'+tab;let state=lfStates.get(key);if(!state){state={fields:{size:'25',sort:'newest'},draft:null,page:0,tokens:{0:''},entry:null};if(scope!=='local'&&['orders','shipping','finance','claims'].includes(tab))Object.assign(state.fields,{from:lfDate(-6),to:lfDate()});state.draft={...state.fields};lfStates.set(key,state);}return state;}
function lfScope(){return tyCurrentStore()?.id||'none';}
function lfTab(kind){return tyTab==='overview'?'overview-'+kind:tyTab;}
function lfSignature(state){return JSON.stringify(Object.fromEntries(Object.entries(state.fields).filter(([,v])=>v!=='').sort(([a],[b])=>a.localeCompare(b))));}
function lfKey(store,kind){const state=lfState(store.id,lfTab(kind));return store.id+':'+kind+':'+lfTab(kind)+':'+lfSignature(state)+':'+state.page;}
const lfOriginalTyKey=tyKey;tyKey=function(store,kind){return lfKey(store,kind);};
function lfDates(fields){const result={};if(fields.from)result.startDate=String(new Date(fields.from+'T00:00:00+03:00').getTime());if(fields.to)result.endDate=String(new Date(fields.to+'T23:59:59.999+03:00').getTime());return result;}
function lfQuery(state){const f=state.fields,query={page:String(state.page),size:f.size||'25',sort:f.sort||'newest',...lfDates(f)};for(const key of ['search','barcode','sku','status','approval','category','channel','carrier'])if(f[key]!==undefined&&f[key]!=='')query[key]=f[key];return query;}
function lfDraft(input){const form=input.form,state=lfState(form.dataset.scope,form.dataset.tab);state.draft[input.name]=input.value;if(input.name==='approval'){state.draft.status='';render();}}
function lfApply(event){event.preventDefault();const form=event.target,scope=form.dataset.scope,tab=form.dataset.tab,state=lfState(scope,tab),fields=Object.fromEntries(new FormData(form));for(const key of Object.keys(fields))fields[key]=String(fields[key]).trim();
 const dates=lfDates(fields);if(fields.from&&fields.to&&Number(dates.startDate)>Number(dates.endDate)){notice('Başlangıç tarihi bitişten sonra olamaz.');return;}
 const store=scope==='local'?null:tyCurrentStore();if(store?.settings.mode==='real'&&['orders','shipping','claims','finance'].includes(tab)){
  if(!fields.from||!fields.to){notice('Başlangıç ve bitiş tarihini birlikte seç.');return;}
  if(Number(dates.endDate)-Number(dates.startDate)>14*86400000){notice('Tek sorguda en fazla 14 günlük tarih aralığı seçebilirsin.');return;}
 }
 state.fields={...fields,size:fields.size||'25',sort:fields.sort||'newest'};state.draft={...state.fields};state.page=0;state.part=0;state.wantLastPart=false;state.tokens={0:''};state.entry=null;lfAbort(scope,tab);tySearch='';render();
}
function lfReset(scope,tab){lfAbort(scope,tab);lfStates.delete(scope+':'+tab);tySearch='';render();}
function lfAbort(scope,tab){lfControllers.get(scope+':'+tab)?.abort();lfControllers.delete(scope+':'+tab);}
function lfRead(store,url,body,controller){const map=typeof tyStoreQueues!=='undefined'?tyStoreQueues:new Map(),prior=map.get(store.id)||Promise.resolve();const current=prior.catch(()=>{}).then(async()=>{
 if(controller.signal.aborted)throw new DOMException('Cancelled','AbortError');
 const response=await fetch(url,{method:body?'POST':url.includes('/integrations/connections/')?'POST':'GET',...(body?{headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}:{}),signal:controller.signal});const payload=await response.json().catch(()=>({}));
 if(payload.connection)data['integrations/connections']=(data['integrations/connections']||[]).map(c=>c.id===payload.connection.id?payload.connection:c);
 if(!response.ok)throw Error(response.status===429?'Çok fazla sorgu yapıldı. Biraz bekleyip tekrar dene.':payload.error||'Kayıtlar yüklenemedi.');return payload.result??payload;
 });const settled=current.then(()=>{},()=>{});map.set(store.id,settled);settled.finally(()=>{if(map.get(store.id)===settled)map.delete(store.id);});return current;}
function lfPrune(store,kind,current){const keys=Object.keys(tyData).filter(k=>k!==current&&k.startsWith(store.id+':'+kind+':'));while(keys.length>=8)delete tyData[keys.shift()];const all=Object.keys(tyData).filter(k=>k!==current);while(all.length>=80)delete tyData[all.shift()];}
tyLoad=function(store,kind){const state=lfState(store.id,lfTab(kind)),key=tyKey(store,kind),requestPage=state.page,requestSignature=lfSignature(state);if(tyData[key])return;const entry={loading:true};tyData[key]=entry;lfPrune(store,kind,key);const controller=new AbortController();lfControllers.set(store.id+':'+lfTab(kind),controller);const fields=state.fields,query=lfQuery(state);let url,body;
 if(store.settings.mode==='demo')url='/api/integrations/demo/connections/'+encodeURIComponent(store.id)+'/search/'+encodeURIComponent(kind)+'?'+new URLSearchParams(query);
 else if(kind==='products'){
  const parameters={page:String(state.page),size:fields.size||'25',approval:fields.approval||'approved'};if(fields.barcode)parameters.barcode=fields.barcode;if(fields.sku)parameters.stockCode=fields.sku;if(fields.status)parameters.status=fields.status;
  if(state.tokens[state.page])parameters.nextPageToken=state.tokens[state.page];url='/api/integrations/connections/'+encodeURIComponent(store.id)+'/products?'+new URLSearchParams(parameters);
 }else if(kind==='orders'){
  const stream=!fields.search&&!fields.barcode&&fields.status!=='Verified';const parameters={page:String(state.page),size:fields.size||'25',stream:String(stream),...lfDates(fields)};
  if(fields.search)parameters.orderNumber=fields.search;if(fields.barcode)parameters.barcode=fields.barcode;if(fields.status)parameters.status=fields.status;if(stream&&state.tokens[state.page])parameters.nextCursor=state.tokens[state.page];if(!stream)parameters.orderByDirection=fields.sort==='oldest'?'ASC':'DESC';
  url='/api/integrations/connections/'+encodeURIComponent(store.id)+'/orders?'+new URLSearchParams(parameters);
 }else{
  const op={claims:'claims.list',finance:'finance.settlements'}[kind];if(!op){entry.loading=false;entry.error='Bu liste desteklenmiyor.';return;}
  const parameters={page:String(state.page),size:kind==='finance'?'500':fields.size||'25',...lfDates(fields)};if(fields.search)parameters.orderNumber=fields.search;if(kind==='claims'&&fields.status)parameters.claimItemStatus=fields.status;if(kind==='finance')parameters.transactionType=fields.status||'Sale';
  url='/api/integrations/trendyol/connections/'+encodeURIComponent(store.id)+'/operations/'+op;body={query:parameters};
 }
 lfRead(store,url,body,controller).then(result=>{if(tyData[key]!==entry||requestSignature!==lfSignature(state))return;if(result.sent===false)throw Error('Mağaza bağlantısı tamamlanmadı.');if(result.data!==undefined){const source=result.data||{};result={items:Array.isArray(source)?source:source.content||source.items||[],page:state.page,size:kind==='finance'?500:Number(fields.size||25),totalElements:source.totalElements,totalPages:source.totalPages};}
  if(!Array.isArray(result.items))throw Error('Liste yanıtı okunamadı.');if(state.wantLastPart&&kind==='products'){state.part=Math.max(0,Math.ceil(result.items.length/Number(fields.size||25))-1);state.wantLastPart=false;}entry.loading=false;entry.result=result;if(result.nextCursor||result.nextPageToken)state.tokens[requestPage+1]=result.nextCursor||result.nextPageToken;
 }).catch(error=>{if(error.name==='AbortError'){if(tyData[key]===entry)delete tyData[key];return;}entry.loading=false;entry.error=error.message;}).finally(()=>{if(view==='trendyol'&&tyKey(store,kind)===key)render();});
};
function lfCanNext(store,kind,result){const state=lfState(store.id,lfTab(kind));if(result.hasMore!==undefined&&result.hasMore!==null)return result.hasMore===true&&!!result.nextCursor;if(store.settings.mode==='real'&&kind==='products'&&(state.page+1)*Number(state.fields.size||25)>=10000&&!result.nextPageToken)return false;return result.totalPages!=null?state.page+1<result.totalPages:!!result.nextPageToken;}
tyPage=function(kind,delta){const store=tyCurrentStore();if(!store)return;const state=lfState(store.id,lfTab(kind)),result=tyData[tyKey(store,kind)]?.result;if(delta>0&&(!result||!lfCanNext(store,kind,result)))return;lfAbort(store.id,lfTab(kind));state.page=Math.max(0,state.page+delta);tySearch='';render();};
tyFiltered=function(rows){return rows;};
function lfReload(){const store=tyCurrentStore();if(!store)return;lfAbort(store.id,tyTab);for(const key of Object.keys(tyData))if(key.startsWith(store.id+':'))delete tyData[key];const state=lfState(store.id,tyTab);state.tokens={0:''};state.page=0;state.part=0;if(store.settings.mode==='demo')tyDemoEnsure(store,true);render();}
tyRefresh=lfReload;
function lfSelect(scope,tab,name,label,options){const state=lfState(scope,tab),value=state.draft?.[name]??state.fields[name]??'';return `<label><span>${label}</span><select name="${name}" onchange="lfDraft(this)">${options.map(([v,l])=>`<option value="${esc(v)}" ${String(value)===String(v)?'selected':''}>${esc(l)}</option>`).join('')}</select></label>`;}
function lfInput(scope,tab,name,label,type='text',placeholder=''){const state=lfState(scope,tab),value=state.draft?.[name]??state.fields[name]??'';return `<label><span>${label}</span><input name="${name}" type="${type}" value="${esc(value)}" placeholder="${esc(placeholder)}" ${type==='number'?'min="0" step="any"':'maxlength="120"'} oninput="lfDraft(this)"></label>`;}
function lfForm(scope,tab){const store=scope==='local'?null:tyCurrentStore(),demo=scope==='local'||store?.settings.mode==='demo',products=['products','inventory','barcodes'].includes(tab),orders=['orders','shipping','invoices','labels','shipments'].includes(tab);const input=(...args)=>lfInput(scope,tab,...args),select=(...args)=>lfSelect(scope,tab,...args);let controls='';
 if(products){
  if(demo)controls+=input('search','Ürün / SKU / barkod','text','Tüm kayıtlarda ara');
  controls+=input('barcode','Tam barkod')+input('sku','Tam SKU');
  if(scope==='local')controls+=select('status','Durum',[['','Tümü'],['Aktif','Aktif'],['Pasif','Pasif']])+input('category','Kategori');
  else controls+=select('approval','Yayın grubu',demo?[['all','Tümü'],['approved','Onaylı'],['unapproved','Onay bekleyen / reddedilen']]:[['approved','Onaylı'],['unapproved','Onay bekleyen / reddedilen']])+select('status','Yayın durumu',[['','Tümü'],...(lfState(scope,tab).draft.approval==='unapproved'?[['pendingApproval','Onay bekliyor'],['rejected','Reddedildi']]:[['onSale','Satışta'],['notOnSale','Satışta değil']])]);
 }else{
  controls+=input('search',orders?(demo?'Sipariş / müşteri':'Tam sipariş numarası'):'Sipariş / açıklama','text',demo?'Tüm kayıtlarda ara':'Sipariş numarası');
  if(tab==='shipments')controls+=input('carrier','Kargo firması');
  if(orders){controls+=select('status','Sipariş durumu',scope==='local'&&tab==='shipments'?[['','Tümü'],['Hazırlanıyor','Hazırlanıyor'],['Kargoya verildi','Kargoya verildi'],['Teslim edildi','Teslim edildi'],['İptal','İptal']]:scope==='local'?[['','Tümü'],['Yeni','Yeni'],['Hazırlanıyor','Hazırlanıyor'],['Kargolandı','Kargolandı'],['Tamamlandı','Tamamlandı'],['İptal','İptal']]:[['','Tümü'],['Created','Yeni'],['Picking','Hazırlanıyor'],['Invoiced','Faturalandı'],['Shipped','Kargoda'],['Delivered','Teslim edildi'],['Cancelled','İptal'],['Returned','İade']]);if(scope!=='local')controls+=input('barcode','Ürün barkodu');}
  else if(tab==='claims')controls+=select('status','İade durumu',demo?[['','Tümü'],['İnceleniyor','İnceleniyor'],['Onaylandı','Onaylandı']]:[['','Tümü'],['WaitingInAction','İşlem bekliyor'],['Accepted','Kabul edildi'],['Rejected','Reddedildi']]);
  else if(tab==='finance')controls+=select('status','Hareket türü',[...(demo?[['','Tümü']]:[]),['Sale','Satış'],['Return','İade'],[demo?'Commission':'CommissionPositive','Komisyon'],...(demo?[['Cargo','Kargo']]:[])]);
  else if(tab==='history')controls+=select('status','İşlem durumu',[['','Tümü'],['pending','Bekliyor'],['completed','Tamamlandı'],['failed','Başarısız'],['retried','Yeniden denendi']]);
  controls+=input('from','Başlangıç','date')+input('to','Bitiş','date');
 }
 if(scope==='local'&&tab!=='shipments')controls+=select('channel','Satış kanalı',[['','Tümü'],...Object.keys(channelColors).map(c=>[c,c])]);
 const sorts=products&&demo?[['newest','En yeni'],['oldest','En eski'],['name','Ürün adı'],['stockAsc','Stok: azdan çoğa'],['stockDesc','Stok: çoktan aza'],['priceAsc','Fiyat: düşükten yükseğe'],['priceDesc','Fiyat: yüksekten düşüğe']]:demo?[['newest','En yeni'],['oldest','En eski'],['priceAsc','Tutar: düşükten yükseğe'],['priceDesc','Tutar: yüksekten düşüğe']]:[['newest','Varsayılan sıra']];
 controls+=select('sort','Sıralama',sorts);
 return `<form class="lf-filter-form" data-scope="${esc(scope)}" data-tab="${tab}" onsubmit="lfApply(event)"><div class="lf-filter-heading">${ic('filter',16)} Filtrele <small>Filtreler tüm sorgu sonuçlarına uygulanır.</small></div><div class="lf-filter-grid">${controls}</div><div class="lf-filter-actions"><button class="btn" type="button" onclick="lfReset(${jsArg(scope)},'${tab}')">Temizle</button><button class="btn btn-primary" type="submit">${ic('search',14)} Filtreleri uygula</button></div></form>`;
}
function lfFooter(scope,tab,result,kind){const state=lfState(scope,tab),store=scope==='local'?null:tyCurrentStore(),page=state.page,size=Number(state.fields.size||25),canNext=scope==='local'?page+1<result.totalPages:lfCanNext(store,kind,result),count=store?.settings.mode==='real'&&kind==='products'?lfVisibleRows(result.items,kind).length:result.items.length,label=result.totalElements!=null?`${Number(result.totalElements).toLocaleString('tr-TR')} eşleşen kayıt · bu sayfada ${count}`:`Bu sayfada ${count} kayıt`;const action=scope==='local'?'hpPage':'tyPage';return `<div class="lf-list-footer"><span>${label}</span><div><button class="btn btn-small" ${page===0?'disabled':''} onclick="${action}('${kind}',-1)">Önceki</button><strong>Sayfa ${page+1}${store?.settings.mode==='real'&&kind==='products'&&result.items.length>size?'.'+((state.part||0)+1):''}${result.totalPages!=null?' / '+Math.max(result.totalPages,1):''}</strong><button class="btn btn-small" ${canNext?'':'disabled'} onclick="${action}('${kind}',1)">Sonraki</button></div></div>`;}
const lfCoreTyCenter=trendyolCenter;
trendyolCenter=function(){let html=lfCoreTyCenter();const store=tyCurrentStore();if(!store||tyTab==='overview')return html;const tabs=['products','inventory','orders','shipping','claims','finance'];if(!tabs.includes(tyTab))return html;const kind=tySource(tyTab),entry=tyData[tyKey(store,kind)];let content;
 if(entry?.loading||!entry)content=tyLoading();else if(entry.error)content=`<section class="card ty-empty" role="alert"><strong>${esc(entry.error)}</strong><button class="btn" onclick="lfReload()">Tekrar dene</button></section>`;else content=`<section class="card ty-business-content"><div class="panel-head"><div class="panel-title">${esc(tyTabs.find(t=>t[0]===tyTab)[1])}</div></div>${tyBusinessRows(store,tyTab,lfVisibleRows(entry.result.items,kind))}${lfFooter(store.id,tyTab,entry.result,kind)}</section>`;
 const marker='<nav class="ty-business-tabs"',start=html.indexOf(marker),end=html.indexOf('</nav>',start);if(end<0)return html;return html.slice(0,end+6)+lfForm(store.id,tyTab)+(typeof tyWorkflowBar==='function'?tyWorkflowBar(store):'')+content;
};
const hpStates=new Map();
function hpState(type){let s=hpStates.get(type);if(!s){s={signature:'',entry:null};hpStates.set(type,s);}return s;}
function hpLoad(type){const state=lfState('local',type),holder=hpState(type),signature=lfSignature(state)+':'+state.page;if(holder.signature===signature&&holder.entry)return;holder.controller?.abort();holder.signature=signature;const entry={loading:true};holder.entry=entry;const controller=new AbortController();holder.controller=controller;
 fetch('/api/'+type+'/search?'+new URLSearchParams(lfQuery(state)),{signal:controller.signal}).then(async r=>{const b=await r.json().catch(()=>({}));if(!r.ok)throw Error(b.error||'Liste yüklenemedi.');return b;}).then(result=>{if(holder.entry!==entry)return;entry.result=result;entry.loading=false;data[type]=result.items;}).catch(e=>{if(e.name!=='AbortError'){entry.error=e.message;entry.loading=false;}}).finally(()=>{if(holder.entry===entry&&((type==='products'&&['products','inventory','barcodes'].includes(view))||view===type||(type==='shipments'&&view==='shipping')))render();});
}
function hpPage(type,delta){const state=lfState('local',type),result=hpState(type).entry?.result;if(delta>0&&(!result||state.page+1>=result.totalPages))return;state.page=Math.max(0,state.page+delta);render();}
listing=function(type){hpLoad(type);const entry=hpState(type).entry;let body=entry?.error?`<section class="card ty-empty">${esc(entry.error)}</section>`:entry?.loading||!entry?tyLoading():`<section class="card table-card">${type==='products'?productTable(entry.result.items):orderTable(entry.result.items)}${lfFooter('local',type,entry.result,type)}</section>`;return head(type==='products'?'Ürün Yönetimi':'Sipariş Yönetimi','Kayıtlarını filtrele ve yalnızca ihtiyacın olan sayfayı yükle.','')+lfForm('local',type)+body;};
const hpOriginalMutate=mutate;mutate=async function(fn,message){for(const holder of hpStates.values()){holder.controller?.abort();holder.signature='';holder.entry=null;}return hpOriginalMutate(fn,message);};

// Filter the demo's document/job tables before paging, too.
const lfOriginalExtended=tyExtendedPage;
tyExtendedPage=function(store){
 const demo=store.settings.mode==='demo';
 if(!['history','invoices','labels'].includes(tyTab)||!demo)return lfOriginalExtended(store);
 const kind=tyTab,state=lfState(store.id,kind);tyLoad(store,kind);const entry=tyData[tyKey(store,kind)],title={history:'İşlem geçmişi',invoices:'Fatura ve garanti',labels:'Etiketler'}[kind];
 let content;if(entry?.error)content=`<div class="ty-empty">${esc(entry.error)}</div>`;else if(!entry||entry.loading)content=tyLoading();else{
  const rows=entry.result.items;let table;
  if(kind==='history')table=tyTable(['İşlem','Tarih','Durum','Ayrıntı','İşlem'],rows.map(j=>`<tr><td>${esc(j.title)}</td><td>${integrationDate(j.createdAt)}</td><td><span class="ty-demo-job-state ${j.state}">${({pending:'Bekliyor',completed:'Tamamlandı',failed:'Başarısız',retried:'Yeniden denendi'})[j.state]||esc(j.state)}</span></td><td>${esc(j.error||j.reference||'—')}</td><td>${j.state==='failed'?`<button class="btn btn-small" onclick="tyDemoAction('retry',{reference:${jsArg(j.id)}})">Yeniden dene</button>`:'—'}</td></tr>`));
  else if(kind==='invoices')table=tyTable(['Sipariş','Müşteri','Fatura','Tutar','Belge'],rows.map(o=>`<tr><td>${esc(o.orderNumber)}</td><td>${esc(o.customerName)}</td><td>${esc(o.invoice.number||'Bekliyor')}</td><td>${integrationMoney(o.total,o.currency)}</td><td>${o.invoice.number?`<button class="btn btn-small" onclick="tyDemoPrintOrder(${jsArg(o.packageId)},true)">Önizle</button>`:'Henüz oluşturulmadı'}</td></tr>`));
  else table=tyTable(['Sipariş','Kargo','Takip','Etiket'],rows.map(o=>`<tr><td>${esc(o.orderNumber)}</td><td>${esc(o.cargo.provider)}</td><td>${esc(o.cargo.trackingNumber)}</td><td><button class="btn btn-small" onclick="tyDemoPrintOrder(${jsArg(o.packageId)},false,true)">Önizle</button></td></tr>`));
  content=table+lfFooter(store.id,kind,entry.result,kind);
 }
 return lfForm(store.id,kind)+`<section class="card ty-business-content"><div class="panel-head"><div><div class="panel-title">${title}</div><p>Demo kayıtlar · filtreler tüm kayıtlar üzerinde uygulanır.</p></div></div>${content}</section>`;
};

// Inventory uses the same bounded product search while keeping movement entry/history.
inventory=function(){return listing('products')+`<div style="margin:18px 0">${btn('Stok girişi / çıkışı','openStockMovement()','btn-primary')}</div><section class="card ty-business-content"><div class="panel-head"><div><div class="panel-title">Son stok hareketleri</div><p>Son 50 hareket gösterilir.</p></div></div>${tyTable(['Tarih','Ürün','Değişim','Kalan','Neden'],(data.movements||[]).slice(0,50).map(m=>`<tr><td>${dt(m.at)}</td><td>${esc(m.productName)}</td><td>${m.delta}</td><td>${m.balance}</td><td>${esc(m.reason)}</td></tr>`))}</section>`;};

// A provider content page may contain many variants. Keep rendered rows bounded too.
function lfVisibleRows(rows,kind=tySource(tyTab)){
 const store=tyCurrentStore();if(!store||store.settings.mode!=='real'||kind!=='products')return rows;
 const state=lfState(store.id,lfTab(kind)),size=Number(state.fields.size||25),part=state.part||0;
 return rows.slice(part*size,(part+1)*size);
}
tyFiltered=function(rows){return lfVisibleRows(rows);};
const lfWholePageCanNext=lfCanNext;
lfCanNext=function(store,kind,result){const state=lfState(store.id,lfTab(kind));if(store.settings.mode==='real'&&kind==='products'&&((state.part||0)+1)*Number(state.fields.size||25)<result.items.length)return true;return lfWholePageCanNext(store,kind,result);};
const lfWholeProviderPage=tyPage;
tyPage=function(kind,delta){const store=tyCurrentStore();if(store?.settings.mode==='real'&&kind==='products'){
 const state=lfState(store.id,lfTab(kind)),result=tyData[tyKey(store,kind)]?.result,size=Number(state.fields.size||25),part=state.part||0;
 if(result&&delta>0&&(part+1)*size<result.items.length){state.part=part+1;render();return;}
 if(delta<0&&part>0){state.part=part-1;render();return;}
 state.part=0;
 if(delta<0&&state.page>0){state.wantLastPart=true;const oldPage=state.page;lfWholeProviderPage(kind,delta);if(state.page!==oldPage){const previous=tyData[tyKey(store,kind)]?.result;if(previous){state.part=Math.max(0,Math.ceil(previous.items.length/size)-1);state.wantLastPart=false;render();}}return;}
 }return lfWholeProviderPage(kind,delta);};

barcodes=function(){hpLoad('products');const entry=hpState('products').entry;return head('Barkod Yönetimi','Ürünleri barkod, SKU veya ada göre tüm kayıtlarda ara.')+lfForm('local','products')+(entry?.result?'<section class="card table-card">'+tyTable(['Ürün','SKU','Barkod','İşlem'],entry.result.items.map(p=>'<tr><td>'+esc(p.name)+'</td><td>'+copyProductIdentifier(p.sku,'SKU')+'</td><td>'+copyProductIdentifier(p.barcode,'Barkod')+'</td><td><button class="btn btn-small" onclick="openBarcode(&quot;'+esc(p.id)+'&quot;)">Düzenle</button></td></tr>'))+lfFooter('local','products',entry.result,'products')+'</section>':tyLoading());};
shipping=function(){hpLoad('shipments');const entry=hpState('shipments').entry;return head('Kargo Yönetimi','Gönderileri sipariş, takip numarası, durum ve tarihe göre filtrele.',btn('Yeni gönderi','openShipment()','btn-primary'))+lfForm('local','shipments')+(entry?.result?'<section class="card table-card">'+tyTable(['Sipariş','Firma','Takip numarası','Durum','İşlem'],entry.result.items.map(s=>'<tr><td>'+esc(s.orderId)+'</td><td>'+esc(s.carrier)+'</td><td>'+copyProductIdentifier(s.trackingNumber,'Takip numarası')+'</td><td>'+tag(s.status)+'</td><td><button class="btn btn-small" onclick="openShipmentStatus(&quot;'+esc(s.id)+'&quot;)">Durum</button></td></tr>'))+lfFooter('local','shipments',entry.result,'shipments')+'</section>':tyLoading());};
