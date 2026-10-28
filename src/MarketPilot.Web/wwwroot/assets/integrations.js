let integrationBusy = null;
let integrationResults = {};
let integrationReadinessLoading = false;
const marketplaceLabels = {trendyol:'Trendyol', hepsiburada:'Hepsiburada', n11:'n11', amazon:'Amazon',boyner:'Boyner',ticimax:'Ticimax'};
const orderStatuses = ['Created','Picking','Invoiced','Shipped','Cancelled','Delivered','UnDelivered','Returned','AtCollectionPoint','UnPacked','UnSupplied','Awaiting','Verified'];
const productStatuses = [['','Tüm durumlar'],['onSale','Satışta'],['notOnSale','Satışta değil'],['archived','Arşivlenmiş'],['blacklisted','Kara listede'],['locked','Kilitli']];

function safeExternalUrl(value) {
  try { const url=new URL(value); return url.protocol==='https:' ? url.href : ''; } catch { return ''; }
}
function integrationMoney(value,currency='TRY') { return value==null?'—':new Intl.NumberFormat('tr-TR',{style:'currency',currency:currency||'TRY'}).format(value); }
function integrationDate(value) { return value?new Date(value).toLocaleString('tr-TR'):'—'; }
function integrationPanel(marketplace) {
  integrationEnsureReadiness();
  const stores=(data['integrations/connections']||[]).filter(c=>!marketplace||c.settings.marketplace===marketplace);
  return `<section class="card integration-panel"><div class="panel-head"><div><div class="panel-title">İnternet Tekstil 5.1 · Gerçek pazaryeri okuma</div><p>Trendyol hesabını doğrulayın; mağaza, ürün, sipariş, kargo ve fatura verilerini salt okunur görüntüleyin.</p></div>${btn('Mağaza ekle','integrationForm()','btn-primary')}</div>
    <p class="integration-note">Hesap bağlamadan resmî Product V2 ve Order V2 cevap sözleşmelerine uygun yerel demoyu kullanabilirsiniz. Canlı Trendyol uçları Basic Auth istediği için gerçek veri yalnızca hesabınızı daha sonra bağladığınızda okunur. Hiçbir işlem Trendyol verisini değiştirmez.</p>
    ${integrationReadiness()}
    <div class="integration-store-list">${stores.map(integrationCard).join('')||'<div class="mp-empty"><strong>Bu pazaryeri için mağaza yok</strong><p>Demo veya gerçek API mağazası ekleyerek başlayın.</p></div>'}</div></section>`;
}
function integrationEnsureReadiness(){if(typeof fetch==='function'&&!Array.isArray(data['integrations/credential-profiles'])&&!integrationProfilesPromise)integrationLoadProfiles().then(()=>render()).catch(()=>{});if(data['integrations/readiness']||integrationReadinessLoading)return;integrationReadinessLoading=true;api('integrations/readiness').then(r=>{data['integrations/readiness']=r}).catch(()=>{data['integrations/readiness']={error:true}}).finally(()=>{integrationReadinessLoading=false;render()})}
function integrationReadiness(){const r=data['integrations/readiness'];if(!r)return '<div class="integration-readiness">API yapılandırması kontrol ediliyor…</div>';if(r.error)return '<div class="integration-readiness missing">API yapılandırma durumu okunamadı.</div>';const profiles=r.profiles||data['integrations/credential-profiles']||[],ready=e=>Boolean(r.trendyol?.[e]||profiles.some(p=>p.marketplace==='trendyol'&&p.environment===e&&p.configured));return `<div class="integration-readiness"><strong>Trendyol API profilleri</strong><span class="${ready('production')?'ready':'missing'}">Üretim: ${ready('production')?'Hazır profil var':'Profil / anahtarlar eksik'}</span><span class="${ready('stage')?'ready':'missing'}">Test: ${ready('stage')?'Hazır profil var':'Profil / anahtarlar eksik'}</span><button class="btn btn-small" onclick="integrationSetupHelp()">Kurulum adımları</button></div>`}

function integrationCard(c) {
  const s=c.settings,result=integrationResults[c.id],labels={demo:'Demo hazır',untested:'Test edilmedi',connected:'Bağlı',error:'Hata'};
  const active=result?.operation;
  return `<article class="card integration-store"><div class="integration-store-head"><div><div class="connection-name">${esc(s.name)}</div><p>${esc(marketplaceLabels[s.marketplace])} · <strong>${s.mode==='demo'?(s.marketplace==='trendyol'?'RESMÎ SÖZLEŞME DEMOSU':'DEMO'):'GERÇEK API'}</strong> · ${s.environment==='stage'?'Test ortamı':'Üretim ortamı'}</p></div><span class="integration-state state-${esc(c.status)}">${esc(labels[c.status]||c.status)}</span></div>
    <div class="integration-meta"><span>Kimlik profili: <strong>${esc(s.credentialProfile|| (s.mode==='demo'?'Demo':'Eski tek hesap ayarı'))}</strong></span><span>Satıcı ID: <strong>${esc(c.sellerId||'Bağlantı testinden sonra')}</strong></span><span>Son işlem: <strong>${c.lastOperation?esc(new Date(c.lastOperation).toLocaleString('tr-TR')):'Henüz yok'}</strong></span>${c.errorCode?`<span>Hata kodu: <strong>${esc(c.errorCode)}</strong></span>`:''}</div>
    <p class="integration-message">${esc(c.message)}</p>
    ${s.mode==='real'&&!['trendyol','hepsiburada','amazon','n11','boyner','ticimax'].includes(s.marketplace)?'<p class="integration-warning">Bu kanalın gerçek adaptörü sonraki sürüm için ayrıldı.</p>':''}
    <div class="integration-actions">${[['test','Bağlantıyı test et'],['account','Mağaza bilgileri'],['products','Ürünler'],['orders','Siparişler']].map(([op,label])=>`<button class="btn btn-small ${active===op?'active':''}" ${integrationBusy?'disabled':''} onclick="integrationOpen('${c.id}','${op}')">${integrationBusy===c.id+op?'İşleniyor…':label}</button>`).join('')}</div>
    ${active==='products'?integrationProductFilters(c.id,result):active==='orders'?integrationOrderFilters(c.id,result):''}
    ${result?.result?integrationResult(c.id,result):''}</article>`;
}
function integrationProductFilters(id,value) {
  const q=value.query||{};
  const approval=q.approval||'approved',statuses=approval==='unapproved'?[['','Tüm durumlar'],['pendingApproval','Onay bekliyor'],['rejected','Reddedildi']]:productStatuses;
  return `<form class="integration-filters" onsubmit="integrationFilter(event,'${id}','products')"><label>Ürün grubu<select name="approval" onchange="this.form.querySelector('[name=status]').value=''\"><option value="approved">Onaylı ürünler</option><option value="unapproved" ${approval==='unapproved'?'selected':''}>Onay bekleyen / reddedilen</option></select></label><label>Barkod<input name="barcode" maxlength="100" value="${esc(q.barcode||'')}" placeholder="Barkod"></label><label>SKU / stok kodu<input name="stockCode" maxlength="100" value="${esc(q.stockCode||'')}" placeholder="SKU"></label><label>Durum<select name="status">${statuses.map(([v,l])=>`<option value="${v}" ${q.status===v?'selected':''}>${l}</option>`).join('')}</select></label><label>Sayfa boyutu<select name="size">${[20,50,100].map(n=>`<option ${Number(q.size||50)===n?'selected':''}>${n}</option>`).join('')}</select></label><button class="btn btn-primary" type="submit">Filtrele</button></form>`;
}
function integrationOrderFilters(id,value) {
  const q=value.query||{};
  const stream=q.stream==='true';
  return `<form class="integration-filters order-filters" onsubmit="integrationFilter(event,'${id}','orders')"><label>Okuma yöntemi<select name="stream" onchange="integrationStreamMode(this.form,this.value)"><option value="false">Sayfalı sorgu</option><option value="true" ${stream?'selected':''}>Büyük veri akışı</option></select></label><label>Durum<select name="status"><option value="">Tüm durumlar</option>${orderStatuses.map(v=>`<option ${q.status===v?'selected':''}>${v}</option>`).join('')}</select></label><label>Sipariş no<input name="orderNumber" maxlength="100" value="${esc(q.orderNumber||'')}" ${stream?'disabled':''}></label><label>Barkod<input name="barcode" maxlength="100" value="${esc(q.barcode||'')}" ${stream?'disabled':''}></label><label>Başlangıç<input type="date" name="start" value="${esc(q.start||'')}"></label><label>Bitiş<input type="date" name="end" value="${esc(q.end||'')}"></label><label>Boyut<select name="size">${[20,50,100,200].map(n=>`<option ${Number(q.size||50)===n?'selected':''}>${n}</option>`).join('')}</select></label><label>Sıralama<select name="orderByDirection" ${stream?'disabled':''}><option value="DESC">Yeniden eskiye</option><option value="ASC" ${q.orderByDirection==='ASC'?'selected':''}>Eskiden yeniye</option></select></label><button class="btn btn-primary" type="submit">Filtrele</button></form>`;
}
function integrationStreamMode(form,value){const disabled=value==='true';for(const name of ['orderNumber','barcode','orderByDirection'])form.elements[name].disabled=disabled}
function integrationResult(id,value) {
  if(value.operation==='test') return integrationAccount(value.result,'Kimlik bilgileri doğrulandı. Mağaza adreslerini görmek için “Mağaza bilgileri”ni açın.');
  if(value.operation==='account') return integrationAccount(value.result,'Adres bilgisi Trendyol’dan okundu ve bir saat önbelleğe alındı.');
  const r=value.result;
  const cursorMode=Boolean(r.nextCursor||r.hasMore!=null||value.query?.nextPageToken);
  return `<div class="integration-result"><div class="integration-result-head"><strong>${value.operation==='products'?(value.query?.approval==='unapproved'?'Onay bekleyen / reddedilen ürünler':'Onaylı ürün varyantları'):(value.query?.stream==='true'?'Sipariş büyük veri akışı':'Sipariş paketleri')} · ${cursorMode?'Akış adımı':'Sayfa'} ${r.page+1}</strong><span>${r.items.length} satır${r.totalElements!=null?' · '+r.totalElements+' toplam içerik/paket':''}</span></div>
    ${value.operation==='products'?integrationProducts(id,r):integrationOrders(id,r)}
    <div class="integration-pagination"><button class="btn btn-small" ${integrationBusy||r.page===0?'disabled':''} onclick="integrationPage('${id}',${r.page-1})">← Önceki</button><span>${cursorMode?`Akış adımı ${r.page+1}`:r.totalPages!=null?`${r.page+1} / ${Math.max(r.totalPages,1)}`:`Sayfa ${r.page+1}`}</span><button class="btn btn-small" ${integrationBusy||!integrationHasNext(r)?'disabled':''} onclick="integrationPage('${id}',${r.page+1})">Sonraki →</button></div>
    <small>Sonuçlar yalnızca görüntülenir; İnternet Tekstil demo kayıtlarına veya Trendyol’a yazılmaz.</small></div>`;
}
function integrationAccount(account,note) {
  return `<div class="integration-result"><div class="account-summary"><div><small>PAZARYERİ</small><strong>${esc(marketplaceLabels[account.marketplace]||account.marketplace)}</strong></div><div><small>SELLER ID</small><strong>${esc(account.sellerId)}</strong></div><div><small>ORTAM</small><strong>${account.environment==='stage'?'Test':'Üretim'}</strong></div><div><small>MAĞAZA KODU</small><strong>${esc(account.storeFrontCode)}</strong></div></div><p>${esc(note)}</p>${account.addresses?.length?`<div class="address-grid">${account.addresses.map(a=>`<div><strong>${esc(a.type)}${a.isDefault?' · Varsayılan':''}</strong><span>${esc([a.city,a.district,a.postCode].filter(Boolean).join(' / '))}</span><p>${esc(a.fullAddress)}</p></div>`).join('')}</div>`:''}</div>`;
}
function integrationProducts(id,r) {
  return `<div class="table-wrap"><table class="data-table integration-table"><thead><tr><th>Ürün</th><th>SKU / Barkod</th><th>Kategori</th><th>Fiyat</th><th>Stok / Onay</th><th></th></tr></thead><tbody>${r.items.map((p,i)=>{const image=safeExternalUrl(p.images?.[0]);return `<tr><td><div class="product-cell">${image?`<img src="${esc(image)}" alt="" loading="lazy" referrerpolicy="no-referrer">`:'<span class="product-placeholder">📦</span>'}<div><strong>${esc(p.name)}</strong><small>${esc(p.brandName||'Marka bilgisi yok')}</small></div></div></td><td><strong>${esc(p.sku||'—')}</strong><small>${esc(p.barcode||'—')}</small></td><td>${esc(p.categoryName||'—')}<small>${esc(p.categoryId||'')}</small></td><td>${integrationMoney(p.salePrice,p.currency)}<small>Liste: ${integrationMoney(p.listPrice,p.currency)}</small></td><td>${p.stock??'—'}<small>${esc(p.approvalStatus||'approved')}</small></td><td><button class="op-detail" onclick="integrationProductDetail('${id}',${i})">Detay →</button></td></tr>`}).join('')||'<tr><td colspan="6">Filtreye uygun ürün bulunamadı.</td></tr>'}</tbody></table></div>`;
}
function integrationOrders(id,r) {
  return `<div class="table-wrap"><table class="data-table integration-table"><thead><tr><th>Sipariş / Paket</th><th>Müşteri</th><th>Tarih</th><th>Tutar</th><th>Durum</th><th>Kargo</th><th></th></tr></thead><tbody>${r.items.map((o,i)=>`<tr><td><strong>${esc(o.orderNumber)}</strong><small>Paket: ${esc(o.packageId)}</small></td><td>${esc(o.customerName||'—')}<small>${esc(o.customerEmail||'')}</small></td><td>${integrationDate(o.orderDate)}</td><td>${integrationMoney(o.total,o.currency)}</td><td><span class="op-pill">${esc(o.status)}</span></td><td>${esc(o.cargo.provider||'—')}<small>${esc(o.cargo.trackingNumber||'Takip no yok')}</small></td><td><button class="op-detail" onclick="integrationOrderDetail('${id}',${i})">Detay →</button></td></tr>`).join('')||'<tr><td colspan="7">Filtreye uygun sipariş paketi bulunamadı.</td></tr>'}</tbody></table></div>`;
}
function integrationHasNext(r) { if(r.hasMore!=null)return r.hasMore===true&&Boolean(r.nextCursor);if(r.totalPages!=null)return r.page+1<r.totalPages&&(Boolean(r.nextPageToken)||(r.page+1)*r.size<10000);return Boolean(r.nextPageToken)||r.items.length>=r.size; }
function integrationProductDetail(id,index) {
  const p=integrationResults[id]?.result?.items?.[index]; if(!p)return;
  const productUrl=safeExternalUrl(p.productUrl);
  showModal(`<div class="integration-detail"><div class="modal-head"><div><small>ÜRÜN VARYANTI</small><h2>${esc(p.name)}</h2></div><button class="top-icon" onclick="closeModal()">${ic('close')}</button></div><div class="detail-grid">${detailItem('Barkod',p.barcode)}${detailItem('SKU',p.sku)}${detailItem('Content ID',p.contentId)}${detailItem('Varyant ID',p.id)}${detailItem('Ürün ana ID',p.productMainId)}${detailItem('Kategori',`${p.categoryName||'—'} · ${p.categoryId||'—'}`)}${detailItem('Marka',p.brandName)}${detailItem('Satış fiyatı',integrationMoney(p.salePrice,p.currency))}${detailItem('Liste fiyatı',integrationMoney(p.listPrice,p.currency))}${detailItem('Müşteri fiyatı',integrationMoney(p.customerPrice,p.currency))}${detailItem('Stok',p.stock??'—')}${detailItem('Onay durumu',p.approvalStatus)}${detailItem('Satış durumu',p.onSale==null?'Bilinmiyor':p.onSale?'Satışta':'Satışta değil')}</div>${p.rejectReasons?.length?`<div class="integration-warning"><strong>Red nedenleri</strong><br>${esc(p.rejectReasons.join('\n'))}</div>`:''}${p.images?.length?`<div class="integration-images">${p.images.slice(0,8).map(x=>safeExternalUrl(x)).filter(Boolean).map(x=>`<img src="${esc(x)}" alt="${esc(p.name)}" loading="lazy" referrerpolicy="no-referrer">`).join('')}</div>`:''}${productUrl?`<a class="btn" href="${esc(productUrl)}" target="_blank" rel="noopener noreferrer">Trendyol ürününü aç</a>`:''}</div>`);
}
function integrationOrderDetail(id,index) {
  const o=integrationResults[id]?.result?.items?.[index]; if(!o)return;
  const tracking=safeExternalUrl(o.cargo.trackingLink),invoice=safeExternalUrl(o.invoice.link);
  showModal(`<div class="integration-detail"><div class="modal-head"><div><small>SİPARİŞ PAKETİ</small><h2>${esc(o.orderNumber)}</h2><p>Paket ${esc(o.packageId)} · ${integrationDate(o.orderDate)}</p></div><button class="top-icon" onclick="closeModal()">${ic('close')}</button></div>
    <h3>Müşteri ve sipariş</h3><div class="detail-grid">${detailItem('Müşteri',o.customerName)}${detailItem('E-posta',o.customerEmail)}${detailItem('Durum',o.status)}${detailItem('Ödeme',o.paymentMethod)}${detailItem('Net toplam',integrationMoney(o.total,o.currency))}${detailItem('Brüt toplam',integrationMoney(o.grossTotal,o.currency))}${detailItem('İndirim',integrationMoney(o.discount,o.currency))}${detailItem('Teslimat türü',o.deliveryAddressType)}</div>
    ${o.shipmentAddress?`<h3>Teslimat adresi</h3><p>${esc(o.shipmentAddress.fullAddress||[o.shipmentAddress.city,o.shipmentAddress.district].filter(Boolean).join(' / '))}</p>`:''}
    <h3>Ürünler</h3><div class="order-lines">${o.lines.map(l=>`<div><strong>${esc(l.name)}</strong><span>${esc(l.sku)} · ${esc(l.barcode)} · ${l.quantity} adet · ${integrationMoney(l.unitPrice,l.currency)}</span></div>`).join('')}</div>
    <h3>Kargo</h3><div class="detail-grid">${detailItem('Firma',o.cargo.provider)}${detailItem('Takip numarası',o.cargo.trackingNumber)}${detailItem('Gönderi numarası',o.cargo.shipmentNumber)}${detailItem('Paket',`${o.cargo.boxQuantity??'—'} koli · ${o.cargo.deci??'—'} desi`)}</div>${tracking?`<a class="btn" href="${esc(tracking)}" target="_blank" rel="noopener noreferrer">Kargoyu takip et</a>`:''}
    <h3>Fatura</h3><div class="detail-grid">${detailItem('Fatura no',o.invoice.number)}${detailItem('Durum',o.invoice.status)}${detailItem('Tür',o.invoice.commercial?'Kurumsal':'Bireysel')}${detailItem('Firma',o.invoice.company)}${detailItem('Vergi dairesi',o.invoice.taxOffice)}${detailItem('Vergi no',o.invoice.taxNumber)}${detailItem('e-Fatura',o.invoice.eInvoiceAvailable==null?'Bilinmiyor':o.invoice.eInvoiceAvailable?'Evet':'Hayır')}</div>${o.invoice.rejectedReasons?.length?`<p class="integration-warning">Red nedenleri: ${esc(o.invoice.rejectedReasons.join(', '))}</p>`:''}${invoice?`<a class="btn" href="${esc(invoice)}" target="_blank" rel="noopener noreferrer">Faturayı görüntüle</a>`:''}</div>`);
}
function detailItem(label,value) { return `<div><small>${esc(label)}</small><strong>${esc(value===null||value===undefined||value===''?'—':value)}</strong></div>`; }
function connections() { return head('Pazaryeri Bağlantıları','Trendyol bağlantısını doğrulayın ve gerçek verileri salt okunur inceleyin.')+integrationPanel()+legacyConnections(); }
function integrationForm(marketplace = ['trendyol','hepsiburada','amazon','n11','boyner','ticimax'].includes(view) ? view : '') {
  showModal(`<form class="store-form" aria-labelledby="store-form-title" onsubmit="integrationCreate(event)">
    <div class="store-form-header"><div class="store-form-icon">${ic('plug',24)}</div><button class="store-form-close" type="button" aria-label="Pencereyi kapat" onclick="closeModal()">${ic('close',18)}</button></div>
    <h2 id="store-form-title">Mağazanı ekle</h2><p class="store-form-subtitle">Satış kanalını seç, mağazana bir ad ver ve bağlantını oluştur.</p>
    <div class="store-form-grid">
      <label class="store-field store-field-wide"><span>Mağaza adı</span><input name="name" required maxlength="80" autocomplete="off" placeholder="Örn. İnternet Tekstil · ${esc(marketplaceLabels[marketplace]||'Trendyol')}"><small>Mağazan panelde bu adla görünecek.</small></label>
      <label class="store-field"><span>Pazaryeri</span><select name="marketplace" ${['trendyol','hepsiburada','amazon','n11','boyner','ticimax'].includes(marketplace)?'disabled':''} onchange="integrationProfileOptions()">${Object.entries(marketplaceLabels).map(([key,label])=>`<option value="${key}" ${key===marketplace?'selected':''}>${label}</option>`).join('')}</select></label>
      <label class="store-field"><span>Ortam</span><select name="environment" onchange="integrationProfileOptions()"><option value="production">Üretim · Canlı mağaza</option><option value="stage">Test · Stage</option></select></label>
      <label class="store-field store-field-wide"><span>Bağlantı türü</span><select name="mode" onchange="integrationModeHint(this.value);integrationProfileOptions()"><option value="demo">Demo · Hesap bağlamadan dene</option><option value="real">Gerçek mağaza bağlantısı</option></select></label>
    </div>
    <label class="store-field store-field-wide" data-credential-field hidden><span>Satıcı kimlik profili</span><select name="credentialProfile" disabled><option value="">Profiller yükleniyor…</option></select><small>Her satıcı için ayrı sunucu profili seçilir. API anahtarları bu forma yazılmaz.</small></label>
    <div class="store-form-note" role="status" aria-live="polite">${ic('shield',18)}<div id="store-mode-hint"><strong>Önce deneyebilirsin</strong><p>Demo mağaza örnek verilerle çalışır. Gerçek mağazanızda işlem yapmaz.</p></div></div>
    <div class="store-form-footer"><button class="btn" type="button" onclick="closeModal()">Vazgeç</button><button class="btn btn-primary" type="submit">${ic('plus',16)} Mağaza ekle</button></div>
  </form>`);
  document.querySelector('.store-form input[name=name]')?.focus();
  if(typeof fetch==="function")integrationLoadProfiles().then(integrationProfileOptions).catch(()=>notice("Kimlik profilleri okunamadı."));
}
function integrationModeHint(mode) {
  const hint=document.querySelector('#store-mode-hint');
  if(!hint)return;
  hint.innerHTML=mode==='real'
    ? '<strong>Bağlantın doğrulanacak</strong><p>Her satıcı için sunucuda ayrı bir kimlik profili tanımlayın ve aşağıdan seçin. Ekledikten sonra bağlantı test edilir.</p>'
    : '<strong>Önce deneyebilirsin</strong><p>Demo mağaza örnek verilerle çalışır. Gerçek mağazanızda işlem yapmaz.</p>';
}
function integrationSetupHelp(){showModal(`<div class="integration-detail"><div class="modal-head"><h2>Mağaza kimlik profili</h2><button class="top-icon" onclick="closeModal()">${ic('close')}</button></div><p>Her satıcı için ayrı bir sunucu profili tanımlayın. API anahtarlarını bu forma veya sohbete yazmayın.</p><pre class="setup-code">dotnet user-secrets set "MarketplaceAccounts:trendyol-ana:Marketplace" "trendyol" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-ana:Environment" "production" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-ana:Credentials:SellerId" "SATICI_ID" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-ana:Credentials:ApiKey" "API_KEY" --project src/MarketPilot.Web
dotnet user-secrets set "MarketplaceAccounts:trendyol-ana:Credentials:ApiSecret" "API_SECRET" --project src/MarketPilot.Web</pre><p>User-secrets Development ortamında yüklenir. Üretimde aynı anahtarları ortam değişkenleriyle tanımlayın. İkinci satıcı için farklı profil adı kullanın; mağaza eklerken o profili seçin. Diğer kanalların alanları appsettings.Accounts.example.json dosyasındadır.</p></div>`)}

async function integrationCreate(event) {
  event.preventDefault();const form=event.target,button=form.querySelector('[type=submit]');button.disabled=true;
  try{
    const settings=Object.fromEntries(new FormData(form));
    settings.marketplace=form.elements.marketplace.value;
    settings.name=settings.name.trim();
    if(!settings.name)throw new Error('Mağaza adını girin.');
    if(settings.mode==='real'&&!['trendyol','hepsiburada','amazon','n11','boyner','ticimax'].includes(settings.marketplace))throw new Error('Bu pazaryerinin gerçek bağlantısı henüz hazır değil.');
    if(settings.mode==='real'){
      const profiles=await integrationLoadProfiles(true);
      const profile=profiles.find(p=>p.id===settings.credentialProfile&&p.marketplace===settings.marketplace&&p.environment===settings.environment);
      if(!profile)throw new Error('Bu satıcının kimlik profilini seçin. Profil sunucuda tanımlanmalıdır.');
      if(!profile.configured)throw new Error('Seçilen profilin API bilgileri eksik. Bilgiler daha sonra sunucuda tamamlanabilir.');
      const existing=(data['integrations/connections']||[]).find(c=>c.settings.mode==='real'&&c.settings.credentialProfile===settings.credentialProfile&&c.settings.marketplace===settings.marketplace&&c.settings.environment===settings.environment);
      if(existing)throw new Error('Bu satıcı profiline bağlı mağaza zaten var: '+existing.settings.name);
    }
    const created=await api('integrations/connections',{method:'POST',body:JSON.stringify(settings)});
    data['integrations/connections']=await api('integrations/connections');
    if(settings.marketplace==='trendyol')tyStore=created.id;
    if(settings.marketplace==='ticimax'&&typeof tcStore!=='undefined')tcStore=created.id;
    if(settings.marketplace==='boyner'&&typeof bnStore!=='undefined')bnStore=created.id;
    if(settings.marketplace==='n11'&&typeof ncStore!=='undefined')ncStore=created.id;
    if(settings.marketplace==='amazon'&&typeof amStore!=='undefined')amStore=created.id;
    if(settings.marketplace==='hepsiburada'&&typeof hbStore!=='undefined')hbStore=created.id;
    modal=null;render();
    if(settings.mode==='real'){if(settings.marketplace==='ticimax'&&typeof tcTest==='function')await tcTest();else if(settings.marketplace==='boyner'&&typeof bnTest==='function')await bnTest();else if(settings.marketplace==='n11'&&typeof ncTest==='function')await ncTest();else if(settings.marketplace==='amazon'&&typeof amTest==='function')await amTest();else if(settings.marketplace==='hepsiburada'&&typeof hbTest==='function')await hbTest();else await integrationRun(created.id,'test',0,{},[]);}
    else notice('Demo mağaza eklendi; gerçek bağlantı kurulmadı.');
  }catch(e){button.disabled=false;notice(e.message);}
}
function integrationOpen(id,operation) { integrationRun(id,operation,0,operation==='products'?{size:'50',approval:'approved'}:operation==='orders'?{size:'50',stream:'false'}:{},[]); }
function integrationFilter(event,id,operation) {
  event.preventDefault();const raw=Object.fromEntries(new FormData(event.target)),query=Object.fromEntries(Object.entries(raw).filter(([,v])=>v!==''));
  if(operation==='orders'&&query.stream==='true'&&query.status==='Verified'){notice('Verified durumu büyük veri akışında desteklenmiyor.');return;}
  if(operation==='orders'&&(query.start||query.end)){if(!query.start||!query.end){notice('Başlangıç ve bitiş tarihini birlikte seçin.');return;}const start=new Date(query.start+'T00:00:00+03:00').getTime(),end=new Date(query.end+'T23:59:59.999+03:00').getTime();if(end-start>14*86400000){notice('Sipariş tarih aralığı en fazla 14 gün olabilir.');return;}query.startDate=String(start);query.endDate=String(end);}
  integrationRun(id,operation,0,query,[]);
}
function integrationPage(id,page) {
  const value=integrationResults[id],r=value.result,back=page<r.page,history=value.history||[];
  if(back&&history.length){const previous=history[history.length-1];integrationRun(id,value.operation,previous.page,previous.query,history.slice(0,-1));return;}
  if(!back&&value.operation==='products'&&r.nextPageToken){integrationRun(id,value.operation,page,{...value.query,nextPageToken:r.nextPageToken},[...history,{page:r.page,query:value.query}]);return;}
  if(!back&&value.operation==='orders'&&value.query?.stream==='true'&&r.nextCursor){integrationRun(id,value.operation,page,{...value.query,nextCursor:r.nextCursor},[...history,{page:r.page,query:value.query}]);return;}
  integrationRun(id,value.operation,page,value.query||{},history);
}
async function integrationRun(id,operation,page,query={},history=[]) {
  if(integrationBusy)return;integrationBusy=id+operation;integrationResults[id]={operation,query,history,result:integrationResults[id]?.operation===operation?integrationResults[id].result:null};render();
  try{const params=new URLSearchParams({...query,page:String(page)}),response=await fetch(`/api/integrations/connections/${encodeURIComponent(id)}/${encodeURIComponent(operation)}?${params}`,{method:'POST'}),body=await response.json().catch(()=>({}));if(body.connection)data['integrations/connections']=data['integrations/connections'].map(c=>c.id===id?body.connection:c);if(!response.ok)throw new Error(body.error||`İşlem başarısız (${response.status}).`);integrationResults[id]={operation,query,history,result:body.result};notice(body.connection.settings.mode==='demo'?'Demo okuması tamamlandı; dış API çağrısı yapılmadı.':'Trendyol salt okunur API isteği tamamlandı.');}catch(e){delete integrationResults[id];notice(e.message);}finally{integrationBusy=null;render();}
}

function copyProductIdentifier(value,label='SKU') {
  if(value===null||value===undefined||String(value)==='')return '<span class="code-copy-empty">—</span>';
  const text=String(value);
  return `<span class="code-copy-row"><span>${esc(text)}</span><button type="button" class="code-copy-button" data-copy-value="${esc(text)}" data-copy-label="${esc(label)}" onclick="event.stopPropagation();copyProductCode(this)" title="${esc(label)} kopyala" aria-label="${esc(label)} ${esc(text)} kopyala"><svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" aria-hidden="true"><rect x="8" y="8" width="12" height="13" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/></svg></button></span>`;
}
async function copyProductCode(button) {
  const value=button.dataset.copyValue;button.disabled=true;
  try {
    if(navigator.clipboard?.writeText)await navigator.clipboard.writeText(value);
    else {
      const previous=document.activeElement,input=document.createElement('textarea');input.value=value;input.style.cssText='position:fixed;left:-9999px;top:0';document.body.append(input);
      try{input.select();if(!document.execCommand('copy'))throw new Error('copy failed');}finally{input.remove();previous?.focus();}
    }
    notice((button.dataset.copyLabel||'Kod')+' kopyalandı.');
  }catch{notice('Kopyalanamadı. Numarayı seçip Ctrl+C ile kopyalayabilirsin.');}
  finally{button.disabled=false;}
}
let integrationProfilesPromise=null;
async function integrationLoadProfiles(force=false){
  if(!force&&Array.isArray(data['integrations/credential-profiles']))return data['integrations/credential-profiles'];
  if(!force&&integrationProfilesPromise)return integrationProfilesPromise;
  integrationProfilesPromise=api('integrations/credential-profiles').then(rows=>{
    data['integrations/credential-profiles']=Array.isArray(rows)?rows:[];return data['integrations/credential-profiles'];
  }).finally(()=>{integrationProfilesPromise=null});
  return integrationProfilesPromise;
}
function integrationProfileOptions(){
  const form=document.querySelector('.store-form'),field=form?.querySelector?.('[data-credential-field]');if(!field)return;
  const real=form.elements.mode.value==='real',select=form.elements.credentialProfile,old=select.value;field.hidden=!real;select.disabled=!real;select.required=real;
  const rows=(data['integrations/credential-profiles']||[]).filter(p=>p.marketplace===form.elements.marketplace.value&&p.environment===form.elements.environment.value);
  select.innerHTML='<option value="">'+(rows.length?'Satıcı profilini seçin':'Bu ortam için sunucuda profil tanımlanmamış')+'</option>'+rows.map(p=>'<option value="'+esc(p.id)+'">'+esc(p.label)+(p.configured?'':' · bilgiler eksik')+'</option>').join('');
  if(rows.some(p=>p.id===old))select.value=old;
}
function integrationStoreReady(store){
  if(!store||store.settings.mode==='demo')return true;
  if(store.settings.credentialProfile){
    const profile=(data['integrations/credential-profiles']||[]).find(p=>p.id===store.settings.credentialProfile);
    return Boolean(profile?.configured&&profile.marketplace===store.settings.marketplace&&profile.environment===store.settings.environment);
  }
  return Boolean(data['integrations/readiness']?.[store.settings.marketplace]?.[store.settings.environment]);
}
