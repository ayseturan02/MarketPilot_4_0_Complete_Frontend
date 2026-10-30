// User-facing Trendyol workspace. Transport and operation keys stay internal.
let tyStore='',tyTab='overview',tySearch='',tySaving=false;
const tyData={},tyPages={};
const tyTabs=[['overview','Genel bakış'],['products','Ürünler'],['orders','Siparişler'],['inventory','Stok ve fiyat'],['shipping','Kargo'],['claims','İadeler'],['finance','Finans']];
function tyStores(){return (data['integrations/connections']||[]).filter(c=>c.settings.marketplace==='trendyol');}
function tyCurrentStore(){const list=tyStores();return list.find(c=>c.id===tyStore)||list.find(c=>c.settings.mode==='real'&&c.status==='connected')||list.find(c=>c.id==='trendyol')||list[0]||null;}
function tySelectStore(id){if(tySaving)return;tyStore=id;tySearch='';render();}
function tySelectTab(tab){tyTab=tyTabs.some(item=>item[0]===tab)?tab:'overview';tySearch='';render();}
function tyKey(store,kind){return store.id+':'+kind+':'+(tyPages[store.id+':'+kind]||0);}
function tySource(tab){return tab==='inventory'?'products':tab==='shipping'?'orders':tab;}
function tyRequest(store,url,body){return fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},...(body?{body:JSON.stringify(body)}:{})}).then(async r=>{const b=await r.json().catch(()=>({}));if(b.connection)data['integrations/connections']=(data['integrations/connections']||[]).map(c=>c.id===b.connection.id?b.connection:c);if(!r.ok)throw new Error('Mağaza verileri alınamadı. Bağlantını kontrol edip yeniden dene.');return b.result;});}
function tyLoad(store,kind){
 const key=tyKey(store,kind);if(tyData[key])return;tyData[key]={loading:true};
 const page=tyPages[store.id+':'+kind]||0;
 let task;
 if(kind==='products'||kind==='orders')task=tyRequest(store,'/api/integrations/connections/'+encodeURIComponent(store.id)+'/'+kind+'?page='+page+'&size=20'+(kind==='products'?'&approval=approved':''));
 else if(store.settings.mode==='demo')task=Promise.resolve({items:[],page:0,totalPages:1});
 else{
  const operation={claims:'claims.list',questions:'questions.filter',finance:'finance.settlements'}[kind];
  const query={page:String(page),size:kind==='finance'?'500':'20'};
  if(kind==='finance'){query.transactionType='Sale';query.startDate=String(Date.now()-14*86400000);query.endDate=String(Date.now());}
  task=tyRequest(store,'/api/integrations/trendyol/connections/'+encodeURIComponent(store.id)+'/operations/'+operation,{query}).then(r=>{
   if(!r.sent)throw new Error('Mağaza bağlantısı tamamlanmadı.');
   const d=r.data||{};return {items:Array.isArray(d)?d:Array.isArray(d.content)?d.content:Array.isArray(d.items)?d.items:[],page,totalPages:d.totalPages??null};
  });
 }
 task.then(result=>{tyData[key]={result};}).catch(()=>{tyData[key]={error:'Veriler yüklenemedi. Mağaza bağlantını kontrol edip tekrar dene.'};}).finally(()=>{if(view==='trendyol')render();});
}
function tyRefresh(){const store=tyCurrentStore();if(!store||tySaving)return;for(const k of Object.keys(tyData))if(k.startsWith(store.id+':'))delete tyData[k];render();}
function tyPage(kind,delta){const s=tyCurrentStore();if(!s)return;const key=s.id+':'+kind;tyPages[key]=Math.max(0,(tyPages[key]||0)+delta);tySearch='';render();}
function tyState(store,kind){tyLoad(store,kind);return tyData[tyKey(store,kind)]||{loading:true};}
function tyRows(store,kind){return tyData[tyKey(store,kind)]?.result?.items||[];}
function tySearchChange(value){tySearch=value;const input=document.activeElement;const start=input?.selectionStart;render();const next=document.querySelector('.ty-search');if(next){next.focus();next.setSelectionRange(start??value.length,start??value.length);}}
function tyFiltered(rows){const q=tySearch.toLocaleLowerCase('tr');return q?rows.filter(r=>JSON.stringify(r).toLocaleLowerCase('tr').includes(q)):rows;}
function tyTable(headers,rows,empty='Gösterilecek kayıt bulunmuyor.'){
 return `<div class="table-wrap"><table class="data-table ty-business-table"><thead><tr>${headers.map(h=>`<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.length?rows.join(''):`<tr><td colspan="${headers.length}"><div class="ty-empty">${ic('box',28)}<strong>${esc(empty)}</strong><span>Kayıtlar geldiğinde burada listelenecek.</span></div></td></tr>`}</tbody></table></div>`;
}
function tyStatus(value){const labels={Created:'Yeni',Picking:'Hazırlanıyor',Invoiced:'Faturalandı',Shipped:'Kargoda',Delivered:'Teslim edildi',Cancelled:'İptal',Returned:'İade',UnDelivered:'Teslim edilemedi',WAITING_FOR_ANSWER:'Yanıt bekliyor',ANSWERED:'Yanıtlandı',approved:'Onaylı',pendingApproval:'Onay bekliyor',rejected:'Reddedildi'};return labels[value]||value||'—';}
function tyProductRows(rows,inventory=false){return tyTable(inventory?['Ürün','SKU / Barkod','Stok','Satış fiyatı','Mağazalar','İşlem']:['Ürün','SKU / Barkod','Kategori','Fiyat','Stok','Mağazalar','Durum'],rows.map(p=>`<tr><td><div class="product-cell">${safeExternalUrl(p.images?.[0])?`<img src="${esc(safeExternalUrl(p.images[0]))}" alt="" class="ty-product-image" loading="lazy">`:`<span class="product-placeholder">${ic('box',20)}</span>`}<div><strong>${esc(p.name)}</strong><small>${esc(p.brandName||'')}</small></div></div></td><td>${copyProductIdentifier(p.sku,'SKU')}<small>${copyProductIdentifier(p.barcode,'Barkod')}</small></td>${inventory?'':`<td>${esc(p.categoryName||'—')}</td><td>${integrationMoney(p.salePrice,p.currency)}</td>`}<td><span class="ty-stock ${p.stock===0?'empty':''}">${p.stock??'—'} adet</span></td>${inventory?`<td>${integrationMoney(p.salePrice,p.currency)}</td><td>${productStoreBadges(p)}</td><td><button class="btn btn-small" onclick="tyEditStock(${rows.indexOf(p)})">Düzenle</button></td>`:`<td>${productStoreBadges(p)}</td><td>${esc(tyStatus(p.approvalStatus))}</td>`}</tr>`));}
function tyOrderRows(rows,shipping=false){return tyTable(shipping?['Sipariş','Müşteri','Kargo firması','Takip numarası','Durum','İşlem']:['Sipariş','Müşteri','Tarih','Tutar','Durum','İşlem'],rows.map(o=>`<tr><td><strong>${esc(o.orderNumber)}</strong><small>Paket ${esc(o.packageId)}</small></td><td>${esc(o.customerName||'—')}</td>${shipping?`<td>${esc(o.cargo?.provider||'—')}</td><td>${esc(o.cargo?.trackingNumber||'Henüz atanmadı')}</td>`:`<td>${integrationDate(o.orderDate)}</td><td>${integrationMoney(o.total,o.currency)}</td>`}<td><span class="op-pill">${esc(tyStatus(o.status))}</span></td><td>${shipping?(safeExternalUrl(o.cargo?.trackingLink)?`<a class="btn btn-small" href="${esc(safeExternalUrl(o.cargo.trackingLink))}" target="_blank" rel="noopener noreferrer">Kargoyu takip et</a>`:'—'):`<button class="btn btn-small" onclick="tyOrderDetail(${rows.indexOf(o)})">Detay</button>`}</td></tr>`));}
function tyBusinessRows(store,kind,rows){
 if(kind==='products'||kind==='inventory')return tyProductRows(rows,kind==='inventory');
 if(kind==='orders'||kind==='shipping')return tyOrderRows(rows,kind==='shipping');
 if(kind==='claims')return tyTable(['İade no','Sipariş','Müşteri','Tarih','Durum'],rows.map(r=>`<tr><td>${esc(r.id||r.claimId||'—')}</td><td>${esc(r.orderNumber||'—')}</td><td>${esc([r.customerFirstName,r.customerLastName].filter(Boolean).join(' ')||r.customerName||'—')}</td><td>${integrationDate(r.claimDate||r.orderDate)}</td><td>${esc(tyStatus(r.status||r.claimStatus))}</td></tr>`));
 if(kind==='questions')return tyTable(['Ürün','Müşteri sorusu','Tarih','Durum','İşlem'],rows.map((r,i)=>`<tr><td>${esc(r.productName||r.product?.name||'—')}</td><td>${esc(r.text||'—')}</td><td>${integrationDate(r.creationDate)}</td><td>${esc(tyStatus(r.status))}</td><td>${r.status==='WAITING_FOR_ANSWER'?`<button class="btn btn-small" onclick="tyAnswerQuestion(${i})">Yanıtla</button>`:esc(r.answer?.text||'—')}</td></tr>`));
 return tyTable(['İşlem','Sipariş','Tarih','Tutar','Açıklama'],rows.map(r=>`<tr><td>${esc(r.transactionType==='Sale'?'Satış':r.transactionType||'—')}</td><td>${esc(r.orderNumber||'—')}</td><td>${integrationDate(r.transactionDate||r.orderDate)}</td><td>${integrationMoney(r.credit??r.amount??r.debt,'TRY')}</td><td>${esc(r.description||'—')}</td></tr>`));
}
function trendyolCenter(){
 if(typeof tyEnsureWorkflows==='function')tyEnsureWorkflows();
 integrationEnsureReadiness();const stores=tyStores(),store=tyCurrentStore(),connected=stores.filter(c=>c.settings.mode==='real'&&c.status==='connected').length;
 const top=head('Trendyol Merkezi','Mağazanı ve günlük operasyonlarını tek yerden yönet.',btn('Mağaza ekle',"integrationForm('trendyol')",'btn-primary'));
 const storeBar=`<section class="card ty-business-store"><div class="ty-shop-icon">${ic('bag',24)}</div><div class="ty-shop-picker"><small>MAĞAZAM</small><select aria-label="Mağaza seç" onchange="tySelectStore(this.value)" ${tySaving?'disabled':''}>${stores.map(c=>`<option value="${esc(c.id)}" ${store?.id===c.id?'selected':''}>${esc(c.id==='trendyol' && c.settings.mode==='demo' ? 'Örnek Trendyol mağazası' : c.settings.name)}</option>`).join('')}</select></div><span class="ty-shop-status">${store?.settings.mode==='demo'?'Demo mağaza':store?.status==='connected'?'Bağlı':'Bağlantı bekliyor'}</span><span class="ty-connected-count">${connected} bağlı mağaza</span><button class="btn btn-small" onclick="tyRefresh()" ${tySaving?'disabled':''}>${ic('sync',15)} Yenile</button></section>`;
 if(!store)return top+'<section class="card ty-empty"><strong>Henüz mağaza eklenmedi.</strong><p>Ürün ve siparişlerini görmek için mağazanı ekle.</p></section>';
 const demo=store.settings.mode==='demo'?'<div class="ty-demo-note">Demo mağazayı görüntülüyorsun. Örnek ürün ve siparişler gösterilir; gerçek mağazan etkilenmez.</div>':'';
 const tabs=`<nav class="ty-business-tabs" aria-label="Trendyol bölümleri">${tyTabs.map(([key,label])=>`<button class="${tyTab===key?'active':''}" aria-pressed="${tyTab===key}" onclick="tySelectTab('${key}')">${label}</button>`).join('')}</nav>`;
 let body;
 if(tyTab==='overview'){
  const ps=tyState(store,'products'),os=tyState(store,'orders');
  if(ps.error||os.error)body=tyError();else if(ps.loading||os.loading)body=tyLoading();else{
   const products=ps.result.items,orders=os.result.items;
   body=`<div class="ty-metric-grid">${[['Görüntülenen ürünler',products.length,'box'],['Görüntülenen siparişler',orders.length,'bag'],['Stokta olmayan',products.filter(p=>p.stock===0).length,'alert'],['Sipariş tutarı',integrationMoney(orders.reduce((n,o)=>n+(o.total||0),0),'TRY'),'wallet']].map(([label,value,icon])=>`<div class="card ty-business-metric"><span>${label}${ic(icon,18)}</span><strong>${value}</strong><small>Yüklenen kayıtlar üzerinden</small></div>`).join('')}</div><section class="card ty-business-content"><div class="panel-head"><div class="panel-title">Son siparişler</div><button class="btn btn-small" onclick="tySelectTab('orders')">Tüm siparişler</button></div>${tyOrderRows(orders.slice(0,5))}</section>`;
  }
 }else if(typeof tyExtendedTabs!=='undefined'&&tyExtendedTabs.has(tyTab)){body=tyExtendedPage(store);
 }else{
  const kind=tySource(tyTab),state=tyState(store,kind);
  const title=tyTabs.find(t=>t[0]===tyTab)[1];
  if(state.error)body=tyError();else if(state.loading)body=tyLoading();else{
   const result=state.result,rows=tyFiltered(result.items);const page=tyPages[store.id+':'+kind]||0;
   const more=result.hasMore!=null?result.hasMore:result.totalPages!=null?page+1<result.totalPages:result.items.length>=(kind==='finance'?500:20);
   body=`<section class="card ty-business-content"><div class="panel-head"><div><div class="panel-title">${title}</div><p>${tyTab==='finance'?'Son 14 gündeki satış hareketleri':`${result.items.length} kayıt yüklendi`}</p></div><input class="ty-search" aria-label="Kayıtlarda ara" placeholder="Bu sayfada ara…" value="${esc(tySearch)}" oninput="tySearchChange(this.value)"></div>${tyBusinessRows(store,tyTab,rows)}<div class="ty-list-footer"><button class="btn btn-small" ${page===0?'disabled':''} onclick="tyPage('${kind}',-1)">Önceki</button><span>Sayfa ${page+1}</span><button class="btn btn-small" ${more?'':'disabled'} onclick="tyPage('${kind}',1)">Sonraki</button></div></section>`;
  }
 }
 return top+storeBar+demo+tabs+(typeof tyWorkflowBar==='function'?tyWorkflowBar(store):'')+body;
}
function tyLoading(){return '<section class="card ty-empty" role="status"><span class="ty-loading-dot"></span><strong>Mağaza bilgileri yükleniyor…</strong></section>';}
function tyError(){return '<section class="card ty-empty" role="alert"><strong>Mağaza bilgileri yüklenemedi.</strong><p>Bağlantını kontrol edip tekrar dene.</p><button class="btn" onclick="tyRefresh()">Tekrar dene</button></section>';}
function tyOrderDetail(index){const s=tyCurrentStore(),rows=tyFiltered(tyRows(s,'orders'));const order=rows[index];if(!order)return;integrationResults[s.id]={operation:'orders',result:{items:[order]}};integrationOrderDetail(s.id,0);}
function tyEditStock(index){const s=tyCurrentStore(),p=tyFiltered(tyRows(s,'products'))[index];if(!p)return;showModal(`<form class="store-form" onsubmit="tySaveStock(event,${index})"><h2>Stok ve fiyatı düzenle</h2><p class="store-form-subtitle">${esc(p.name)}</p><div class="store-form-grid"><label class="store-field"><span>Stok adedi</span><input type="number" name="stock" min="0" step="1" value="${p.stock??0}" required></label><label class="store-field"><span>Satış fiyatı (₺)</span><input type="number" name="price" min="0.01" step="0.01" value="${p.salePrice??''}" required></label></div><div class="store-form-footer"><button class="btn" type="button" onclick="closeModal()">Vazgeç</button><button class="btn btn-primary" ${tySaving?'disabled':''}>Kaydet</button></div></form>`);}
async function tyBusinessWrite(key,request){const s=tyCurrentStore();if(!s||tySaving)return false;if(s.settings.mode==='demo'){notice('Demo mağazada değişiklik kaydedilmez. Gerçek mağazanı bağlayarak devam edebilirsin.');return false;}if(!confirm('Değişiklikler Trendyol mağazana uygulanacak. Onaylıyor musun?'))return false;tySaving=true;render();try{const result=await tyRequest(s,'/api/integrations/trendyol/connections/'+encodeURIComponent(s.id)+'/operations/'+key,{...request,confirm:true});if(!result.sent){notice('Değişiklik kaydedilmedi. Bu mağazada düzenleme henüz etkin değil.');return false;}notice('İşlemin mağazana iletildi. Güncel durum biraz sonra yansıyabilir.');modal=null;for(const k of Object.keys(tyData))if(k.startsWith(s.id+':'))delete tyData[k];return true;}catch{notice('İşlem tamamlanamadı. Lütfen tekrar dene.');return false;}finally{tySaving=false;render();}}
async function tySaveStock(event,index){event.preventDefault();const s=tyCurrentStore(),p=tyFiltered(tyRows(s,'products'))[index];if(!p?.barcode)return;const stock=Number(event.target.elements.stock.value),price=Number(event.target.elements.price.value);if(!Number.isInteger(stock)||stock<0||!Number.isFinite(price)||price<=0){notice('Stok ve fiyatı kontrol et.');return;}await tyBusinessWrite('inventory.updatePriceAndInventory',{body:{items:[{barcode:p.barcode,quantity:stock,salePrice:price,listPrice:Math.max(p.listPrice??price,price)}]}});}
function tyAnswerQuestion(index){const s=tyCurrentStore(),q=tyFiltered(tyRows(s,'questions'))[index];if(!q)return;showModal(`<form class="store-form" onsubmit="tySendAnswer(event,${index})"><h2>Müşteri sorusunu yanıtla</h2><p class="store-form-subtitle">${esc(q.text)}</p><label class="store-field"><span>Yanıtın</span><textarea class="ty-answer" name="answer" required minlength="10" maxlength="2000" rows="5"></textarea></label><div class="store-form-footer"><button class="btn" type="button" onclick="closeModal()">Vazgeç</button><button class="btn btn-primary">Yanıtı gönder</button></div></form>`);}
async function tySendAnswer(event,index){event.preventDefault();const s=tyCurrentStore(),q=tyFiltered(tyRows(s,'questions'))[index];if(!q)return;const text=event.target.elements.answer.value.trim();if(text.length<10){notice('Yanıt en az 10 karakter olmalı.');return;}await tyBusinessWrite('questions.answer',{path:{id:String(q.id)},body:{text}});}
