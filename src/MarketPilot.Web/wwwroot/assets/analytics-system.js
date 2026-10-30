// Analysis and system screens share the same persisted catalogue, orders and workers.
const axViews = ['reports','profit','alerts','activity','automation','settings','users'];
const axState = { workspace:null, reports:null, profit:null, errors:{}, loading:new Set(),
  query:{}, reportDraft:false, settingsDirty:false, costSaving:false, search:'', level:'all', kind:'all',
  profitSearch:'', profitCurrency:'all', page:1, activitySearch:'' };
const axMoney = (value, currency='TRY') => value == null ? '—' : /^[A-Z]{3}$/.test(currency) ? new Intl.NumberFormat('tr-TR', {style:'currency',currency,maximumFractionDigits:2}).format(value) : new Intl.NumberFormat('tr-TR',{maximumFractionDigits:2}).format(value)+' (para birimi bilinmiyor)';
const axTime = value => value ? new Date(value).toLocaleString('tr-TR') : 'Henüz yok';
const axManage = () => !localAccess.configured || localAccess.role === 'admin';
const axWritable = () => !localAccess.configured || localAccess.role !== 'viewer';
const axEditing = () => view==='reports'&&axState.reportDraft || view==='settings'&&axState.settingsDirty || view==='automation'&&(auDirty||auSaving);
const axFocused = () => ['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName);
const axEmpty = text => `<p class="ax-empty">${esc(text)}</p>`;
function axMetric(label,value,detail='') { return `<div class="card ax-metric"><span>${esc(label)}</span><strong>${esc(value)}</strong><small>${esc(detail)}</small></div>`; }
function axTable(headers,rows) { return `<div class="table-wrap"><table class="data-table"><thead><tr>${headers.map(h=>`<th>${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table>${rows.length?'':axEmpty('Filtrelere uygun kayıt yok.')}</div>`; }
function axEnsure(kind) { if (!axState[kind] && !axState.loading.has(kind) && !axState.errors[kind]) void axLoad(kind); }
async function axLoad(kind) {
  if (axState.loading.has(kind)) return;
  axState.loading.add(kind);
  const query = kind === 'reports' ? new URLSearchParams(axState.query).toString() : '';
  try {
    const result = await api('analytics/'+kind+(query?'?'+query:''));
    axState[kind] = result;
    delete axState.errors[kind];
    if (kind === 'workspace') {
      auData = {automation:result.automation,outbox:result.outbox};
      data.settings = {...data.settings,...result.settings};
      const bell=document.querySelector('[aria-label="Bildirimler"]');
      if(bell){bell.querySelector('.notify-dot')?.remove();if(axAlerts().length)bell.insertAdjacentHTML('beforeend',axNotificationDot());}
    }
  } catch (error) { axState.errors[kind] = error.message; }
  finally {
    axState.loading.delete(kind);
    if (axViews.includes(view) && !modal && !axEditing() && !axFocused() && !(typeof orderRevisionBusy!=='undefined'&&orderRevisionBusy)) render();
  }
}
async function axRefresh() {
  const kinds = ['workspace'];
  if (view === 'reports' || view === 'profit') kinds.push(view);
  await Promise.all(kinds.map(axLoad));
}
function axAlerts() {
  const w=axState.workspace;
  if (!w) return [];
  const rows=[...w.alerts],q=w.outbox,a=w.automation;
  for (const [state,title] of [['failed','Stok gönderimi başarısız'],['blocked','Stok gönderimi engellendi']])
    if(q[state]) rows.push({id:'outbox:'+state,level:state==='failed'?'critical':'warning',kind:'sync',title,detail:`${q[state]} işlem müdahale bekliyor.`,target:'automation'});
  const uncertain=q.recent.filter(j=>j.state==='uncertain').length;
  if(uncertain)rows.push({id:'outbox:uncertain',level:'warning',kind:'sync',title:'Gönderim sonucu belirsiz',detail:'Son işlemler arasında '+uncertain+' gönderimin sonucu doğrulanıyor.',target:'automation'});
  if(a.stockError)rows.push({id:'scheduler:stock',level:'critical',kind:'sync',title:'Otomatik stok taraması tamamlanamadı',detail:a.stockError,target:'automation'});
  for(const store of a.stores.filter(s=>s.error))rows.push({id:'scheduler:'+store.storeId,level:'warning',kind:'sync',title:'Sipariş okuma hatası',detail:store.storeName+' · '+store.error,target:'automation'});
  return rows.filter(r=>w.settings.syncNotifications || r.kind!=='sync').sort((a,b)=>({critical:0,warning:1,info:2}[a.level]??3)-({critical:0,warning:1,info:2}[b.level]??3));
}
function axNotificationDot(){return axAlerts().length?'<i class="notify-dot"></i>':'';}
function axPage(kind) {
  axEnsure('workspace');
  if(kind==='reports'||kind==='profit')axEnsure(kind);
  const meta={reports:['Raporlar ve Analizler','Siparişler ve satış kanallarıyla bağlantılı performans verileri.'],profit:['Kârlılık Analizi','Katalog fiyatları ve kayıtlı maliyetler üzerinden birim katkı hesabı.'],alerts:['Bildirimler','Stok, bağlantı ve arka plan işlemlerinden oluşan güncel uyarılar.'],activity:['İşlem Kayıtları','Ürün, sipariş, ayar ve sistem olaylarının ortak işlem geçmişi.'],automation:['Otomasyonlar','Sunucuda çalışan sipariş okuma, stok dağıtımı ve kalıcı gönderim kuyruğu.'],settings:['Genel Ayarlar','İşletme bilgileri, rapor tercihleri ve uygulamanın güncel çalışma durumu.']}[kind];
  const buttons=`<button class="btn" onclick="axRefresh()" ${axState.loading.size?'disabled':''}>${ic('sync',15)} Yenile</button>`+(['reports','profit','activity','alerts'].includes(kind)?`<button class="btn" onclick="axExport()">${ic('download',15)} CSV indir</button>`:'');
  const errors=[...new Set(['workspace',kind].map(k=>axState.errors[k]).filter(Boolean))];
  const body={reports:axReports,profit:axProfit,alerts:axNotifications,activity:axActivity,automation:()=>automationPanel(),settings:axSettings}[kind]();
  return head(meta[0],meta[1],buttons)+`<div class="ax-workspace" data-analysis="${kind}">${errors.map(e=>`<p class="ax-error" role="alert">${esc(e)} Son veriler gösteriliyor; Yenile ile tekrar deneyin.</p>`).join('')}<div class="ax-source"><span>${axState.workspace?`${axState.workspace.connections.demo} demo mağaza · ${axState.workspace.connections.connected} bağlı gerçek mağaza`:'Çalışma alanı yükleniyor…'}</span><small>Son okuma: ${esc(axTime(axState[kind]?.generatedAt||axState.workspace?.generatedAt))}</small></div>${body}</div>`;
}
function axReports() {
  const r=axState.reports;
  if(!r)return axEmpty('Rapor verileri yükleniyor…');
  const q=axState.query,toDate=ms=>ms?new Date(Number(ms)).toLocaleDateString('sv-SE'):'';
  const bars=r.channels.map(c=>`<div class="ax-bar"><div><span>${esc(c.name)}</span><strong>${axMoney(c.revenue,r.currency)}</strong></div><meter min="0" max="${Math.max(1,...r.channels.map(x=>x.revenue))}" value="${Math.max(0,c.revenue)}"></meter><small>${c.orders} sipariş</small></div>`).join('');
  const daily=r.daily.slice(-30),max=Math.max(1,...daily.map(d=>d.revenue));
  return `<form class="card ax-filters" oninput="axState.reportDraft=true" onsubmit="axApplyReport(event)"><label>Başlangıç<input type="date" name="start" value="${esc(toDate(q.startDate))}"></label><label>Bitiş<input type="date" name="end" value="${esc(toDate(q.endDate))}"></label><label>Kanal<select name="channel"><option value="">Tüm kanallar</option>${r.availableChannels.map(c=>`<option ${q.channel===c?'selected':''}>${esc(c)}</option>`).join('')}</select></label><label>Kaynak<select name="mode">${[['','Tüm kaynaklar'],['local','Yerel kayıt'],['demo','Demo mağaza'],['real','Gerçek mağaza']].map(([v,l])=>`<option value="${v}" ${q.mode===v?'selected':''}>${l}</option>`).join('')}</select></label><label>Para birimi<select name="currency">${r.availableCurrencies.map(c=>`<option ${r.currency===c?'selected':''}>${esc(c)}</option>`).join('')}</select></label><button class="btn btn-primary">Filtrele</button><button type="button" class="btn" onclick="axClearReport()">Temizle</button></form>`+
    `<div class="ax-metrics">${axMetric('Sipariş tutarı',axMoney(r.revenue,r.currency),'İptaller hariç · iadeler düşülmedi')}${axMetric('Sipariş sayısı',r.orderCount,`${r.cancelledCount} iptal · ${r.returnedCount} iade`)}${axMetric('Ortalama sepet',axMoney(r.averageBasket,r.currency),`${r.revenueOrderCount} tutarı bilinen, iptal olmayan sipariş`)}${axMetric('Tutarı bilinmeyen',r.unknownTotalCount,'Tutar ve ortalama hesabına dahil edilmez')}</div>`+
    `<div class="ax-grid"><section class="card panel"><h2>Kanala göre dağılım</h2>${bars||axEmpty('Bu filtrelerde satış yok.')}</section><section class="card panel"><h2>Günlük sipariş tutarı</h2><p class="ax-note">Seçilen aralıktaki son 30 kayıt günü · Türkiye saati</p><div class="ax-daily" role="img" aria-label="Günlük sipariş tutarı grafiği">${daily.map(d=>`<div title="${esc(d.date)} · ${axMoney(d.revenue,r.currency)}"><span style="height:${Math.max(0,d.revenue/max*100)}%"></span><small>${esc(d.date.slice(5))}</small></div>`).join('')||axEmpty('Görüntülenecek tarihli sipariş yok.')}</div></section></div>`+
    `<section class="card panel"><h2>Kanal özeti</h2>${axTable(['Kanal','Sipariş','Sipariş tutarı · '+r.currency],r.channels.map(c=>`<tr><td>${esc(c.name)}</td><td>${c.orders}</td><td>${axMoney(c.revenue,r.currency)}</td></tr>`))}<p class="ax-note">Tüm kayıtlar hesaplanır; sipariş listesinin sayfa sınırı raporu etkilemez. Para birimleri dönüştürülmez veya birbirine eklenmez.${r.undatedCount?' '+r.undatedCount+' kaydın tarihi bilinmiyor.':''}</p></section>`;
}
async function axApplyReport(event){
  event.preventDefault();if(axState.loading.has('reports'))return;
  const f=event.target.elements,q={};
  if(f.start.value)q.startDate=new Date(f.start.value+'T00:00:00').getTime();
  if(f.end.value)q.endDate=new Date(f.end.value+'T23:59:59.999').getTime();
  if(q.startDate&&q.endDate&&q.startDate>q.endDate){notice('Başlangıç tarihi bitişten sonra olamaz.');return;}
  for(const k of ['channel','mode','currency'])if(f[k].value)q[k]=f[k].value;
  axState.query=q;axState.reportDraft=false;await axLoad('reports');
}
async function axClearReport(){if(axState.loading.has('reports'))return;axState.query={};axState.reportDraft=false;await axLoad('reports');}
function axProfitRows(){return (axState.profit?.products||[]).filter(p=>(axState.profitCurrency==='all'||p.currency===axState.profitCurrency)&&(!axState.profitSearch||(p.name+' '+p.sku).toLocaleLowerCase('tr').includes(axState.profitSearch.toLocaleLowerCase('tr'))));}
function axProfit(){
  if(!axState.profit)return axEmpty('Katalog ve maliyetler yükleniyor…');
  const rows=axProfitRows(),pages=Math.max(1,Math.ceil(rows.length/25));axState.page=Math.min(axState.page,pages);
  const labels={missing_cost:'Maliyet girilmedi',missing_price:'Fiyat bilinmiyor',currency_mismatch:'Para birimi değişti',ready:'Hesaplandı'};
  return `<div class="ax-metrics">${axMetric('Katalog ürünü',rows.length)}${axMetric('Maliyeti tamamlanan',rows.filter(p=>p.state==='ready').length)}${axMetric('Eksik maliyet / fiyat',rows.filter(p=>p.state!=='ready').length)}${axMetric('Negatif birim katkı',rows.filter(p=>p.margin!=null&&p.margin<0).length)}</div><section class="card panel"><div class="ax-filters"><label>Ürün / SKU<input data-profit-search placeholder="Ürün veya SKU ara" value="${esc(axState.profitSearch)}" oninput="axProfitFilter(this.value)"></label><label>Para birimi<select onchange="axState.profitCurrency=this.value;axState.page=1;render()"><option value="all">Tüm para birimleri</option>${[...new Set(axState.profit.products.map(p=>p.currency))].map(c=>`<option ${axState.profitCurrency===c?'selected':''}>${esc(c)}</option>`).join('')}</select></label></div>`+
    axTable(['Ürün / SKU','Satış fiyatı','Toplam maliyet','Birim katkı','Katkı oranı','Durum','İşlem'],rows.slice((axState.page-1)*25,axState.page*25).map(p=>`<tr><td><strong>${esc(p.name)}</strong><small class="ax-cell-note">${esc(p.sku)} · ${esc(p.currency)}</small></td><td>${axMoney(p.price,p.currency)}</td><td>${axMoney(p.expenses,p.currency)}</td><td>${axMoney(p.margin,p.currency)}</td><td>${p.marginRate==null?'—':new Intl.NumberFormat('tr-TR',{maximumFractionDigits:2}).format(p.marginRate)+'%'}</td><td>${labels[p.state]}</td><td>${axManage()?`<button class="btn btn-small" onclick="axEditCost(${jsArg(p.id)})">Maliyeti düzenle</button>`:'Yönetici gerekli'}</td></tr>`))+
    `<div class="ax-pagination"><button class="btn" ${axState.page<=1?'disabled':''} onclick="axState.page--;render()">Önceki</button><span>${axState.page} / ${pages} · ${rows.length} ürün</span><button class="btn" ${axState.page>=pages?'disabled':''} onclick="axState.page++;render()">Sonraki</button></div><p class="ax-note">Birim katkı = güncel satış fiyatı − alış − komisyon − kargo − diğer maliyet. Maliyetler satış fiyatıyla aynı KDV esasına göre girilir; KDV ayrıştırılmaz. İade, hakediş ve dönem giderleri bu hesaba dahil değildir.</p></section>`;
}
function axProfitFilter(value){axState.profitSearch=value;axState.page=1;render();const input=document.querySelector('[data-profit-search]');input?.focus();input?.setSelectionRange(value.length,value.length);}
function axEditCost(id){
  const p=axState.profit?.products.find(p=>p.id===id);if(!p||!axManage())return;
  const c=p.cost?.currency===p.currency?p.cost:{};
  showModal(`<div class="modal-head"><h3>Ürün maliyeti</h3><button class="modal-close" onclick="closeModal()">×</button></div><p>${esc(p.name)} · ${esc(p.sku)} · ${esc(p.currency)}</p><form onsubmit="axSaveCost(event,${jsArg(id)},${jsArg(p.currency)})">${[['unitCost','Birim alış maliyeti',c.unitCost??''],['commissionRate','Komisyon oranı (%)',c.commissionRate??0],['shippingCost','Birim kargo maliyeti',c.shippingCost??0],['otherCost','Diğer birim maliyet',c.otherCost??0]].map(([name,label,value])=>`<div class="form-row"><label>${label}</label><input name="${name}" type="number" min="0" max="${name==='commissionRate'?100:10000000}" step="0.01" required value="${value}"></div>`).join('')}<p class="ax-note">Komisyon güncel satış fiyatından hesaplanır. Bilinen sıfır maliyet için 0 girilebilir.</p><p class="local-form-error" role="alert"></p><button class="btn btn-primary" type="submit">Maliyeti kaydet</button></form>`);
}
async function axSaveCost(event,id,currency){
  event.preventDefault();if(axState.costSaving)return;axState.costSaving=true;
  const form=event.target,button=form.querySelector('[type=submit]');button.disabled=true;
  try{
    const body={currency};for(const key of ['unitCost','commissionRate','shippingCost','otherCost'])body[key]=Number(form.elements[key].value);
    await api('analytics/costs/'+encodeURIComponent(id),{method:'PUT',body:JSON.stringify(body)});
    await Promise.all([axLoad('profit'),axLoad('workspace')]);modal=null;render();notice('Maliyet kaydedildi; kârlılık hesabı güncellendi.');
  }catch(error){form.querySelector('[role=alert]').textContent=error.message;}
  finally{axState.costSaving=false;button.disabled=false;}
}
function axNotifications(){
  const rows=axAlerts().filter(r=>(axState.level==='all'||r.level===axState.level)&&(!axState.search||(r.title+' '+r.detail).toLocaleLowerCase('tr').includes(axState.search.toLocaleLowerCase('tr'))));
  return `<div class="ax-metrics">${axMetric('Güncel uyarı',axAlerts().length)}${axMetric('Kritik',axAlerts().filter(r=>r.level==='critical').length)}${axMetric('Stok eşiği',axState.workspace?.settings.lowStockThreshold??'—','Genel ayarlardan yönetilir')}</div><section class="card panel"><div class="ax-filters"><label>Önem<select onchange="axState.level=this.value;render()">${[['all','Tümü'],['critical','Kritik'],['warning','Uyarı'],['info','Bilgi']].map(([v,l])=>`<option value="${v}" ${axState.level===v?'selected':''}>${l}</option>`).join('')}</select></label><button class="btn" onclick="nav('settings')">Bildirim tercihleri</button></div>${axTable(['Uyarı','Açıklama','Önem','İşlem'],rows.map(r=>`<tr><td>${esc(r.title)}</td><td>${esc(r.detail)}</td><td><span class="tag ${r.level==='critical'?'tag-red':'tag-orange'}">${r.level==='critical'?'Kritik':r.level==='warning'?'Uyarı':'Bilgi'}</span></td><td><button class="btn btn-small" onclick="nav(${jsArg(r.target)})">İlgili ekrana git</button></td></tr>`))}<p class="ax-note">Uyarılar kayıtların güncel durumundan üretilir; sorun giderildiğinde otomatik kalkar. Senkronizasyon uyarıları bildirim tercihinize göre gösterilir.</p></section>`;
}
function axActivityRows(){const w=axState.workspace;return !w?[]:[...w.activity,...w.events.map(e=>({date:e.date,level:e.level,category:e.category,title:e.level==='error'?'Sistem hatası':'Sistem olayı',detail:e.message}))].sort((a,b)=>new Date(b.date)-new Date(a.date)).filter(r=>!axState.activitySearch||(r.title+' '+r.detail+' '+r.category).toLocaleLowerCase('tr').includes(axState.activitySearch.toLocaleLowerCase('tr')));}
function axActivity(){return `<section class="card panel"><form class="ax-filters" onsubmit="event.preventDefault();axState.activitySearch=this.elements.search.value;render()"><label>Kayıtlarda ara<input name="search" value="${esc(axState.activitySearch)}" placeholder="İşlem veya açıklama"></label><button class="btn">Ara</button></form>${axTable(['Zaman','Kaynak','İşlem','Açıklama'],axActivityRows().map(a=>`<tr><td>${esc(axTime(a.date))}</td><td>${a.category==='operation'?'Operasyon':esc(a.category)}</td><td>${esc(a.title)}</td><td>${esc(a.detail)}</td></tr>`))}<p class="ax-note">Kaydedilen son operasyonlar ve ${axState.workspace?.canReadAudit?'yöneticiye açık sistem olayları':'yetkinize açık kayıtlar'} gösterilir.</p></section>`;}
function axSettings(){
  const w=axState.workspace,s=data.settings;if(!w||!s)return axEmpty('Ayarlar ve sistem durumu yükleniyor…');
  const sys=w.system,disabled=axWritable()?'':'disabled';
  return `<form class="card panel" oninput="axState.settingsDirty=true" onsubmit="axSaveSettings(event)"><h2>Çalışma alanı</h2><div class="ax-filters"><label>İşletme adı<input name="businessName" value="${esc(s.businessName)}" maxlength="200" required ${disabled}></label><label>Varsayılan rapor para birimi<select name="currency" ${disabled}>${[...new Set(['TRY','USD','EUR',s.currency])].map(c=>`<option ${s.currency===c?'selected':''}>${esc(c)}</option>`).join('')}</select></label><label>Kritik stok eşiği<input name="lowStockThreshold" type="number" min="0" max="10000000" value="${s.lowStockThreshold}" required ${disabled}></label></div><label class="switch-line">Senkronizasyon uyarılarını bildirimlerde göster<input type="checkbox" name="syncNotifications" ${s.syncNotifications?'checked':''} ${disabled}></label><p class="ax-note">Para birimi tercihi raporları filtreler; ürün fiyatlarını dönüştürmez. E-posta gönderim servisi henüz yapılandırılmadı.</p>${axWritable()?'<button class="btn btn-primary" type="submit">Ayarları kaydet</button>':'<p>Bu hesap ayarları görüntüleyebilir.</p>'}</form><div class="ax-metrics">${axMetric('Çalışma modu',sys.mode==='public'?'HTTPS yayını':'Yerel çalışma')}${axMetric('Mağazalar',w.connections.total,`${w.connections.demo} demo · ${w.connections.real} gerçek`)}${axMetric('Gerçek veri okuma',sys.realReadsEnabled?'Açık':'Kapalı')}${axMetric('Gerçek stok / fiyat yazma',sys.realWritesEnabled?'Genel izin açık':'Kapalı','Mağaza izinleri ayrıca denetlenir')}</div><section class="card panel"><h2>Sistem durumu</h2>${axTable(['Servis','Son durum','Ayrıntı'],[`<tr><td>Arka plan kuyruğu</td><td>${esc(axTime(sys.lastQueueTick))}</td><td>${esc(sys.lastQueueError||'Kayıtlı hata yok')}</td></tr>`,`<tr><td>Otomatik yedek</td><td>${esc(axTime(sys.lastBackupSuccess))}</td><td>${esc(sys.lastBackupError||sys.backupCount+' şifreli yedek')}</td></tr>`,`<tr><td>Zamanlanmış senkronizasyon</td><td>${w.automation.settings.enabled?'Açık':'Kapalı'}</td><td>${w.automation.settings.intervalSeconds} saniye</td></tr>`])}<div class="ax-filters"><button class="btn" onclick="nav('automation')">Otomasyonları yönet</button><button class="btn" onclick="nav('users')">Kullanıcılar ve yedekler</button><button class="btn" onclick="nav('connections')">Mağaza bağlantıları</button></div></section>`;
}
async function axSaveSettings(event){
  event.preventDefault();const f=event.target,button=f.querySelector('[type=submit]');button.disabled=true;
  try{
    const result=await api('settings',{method:'PUT',body:JSON.stringify({businessName:f.elements.businessName.value.trim(),currency:f.elements.currency.value,lowStockThreshold:Number(f.elements.lowStockThreshold.value),syncNotifications:f.elements.syncNotifications.checked,emailNotifications:data.settings.emailNotifications})});
    data.settings=result;axState.settingsDirty=false;axState.reports=null;await axLoad('workspace');render();notice('Ayarlar kaydedildi; rapor ve bildirimler güncellendi.');
  }catch(error){notice(error.message);}finally{button.disabled=false;}
}
function axExport(){
  let rows;
  if(view==='reports'){const r=axState.reports;if(!r)return;rows=[['Kanal','Sipariş','Sipariş tutarı','Para birimi'],...r.channels.map(c=>[c.name,c.orders,c.revenue,r.currency])];}
  if(view==='profit')rows=[['Ürün','SKU','Para birimi','Satış','Toplam maliyet','Birim katkı','Oran'],...axProfitRows().map(p=>[p.name,p.sku,p.currency,p.price??'',p.expenses??'',p.margin??'',p.marginRate??''])];
  if(view==='activity')rows=[['Zaman','Kaynak','İşlem','Açıklama'],...axActivityRows().map(a=>[a.date,a.category,a.title,a.detail])];
  if(view==='alerts')rows=[['Uyarı','Açıklama','Önem'],...axAlerts().filter(r=>axState.level==='all'||r.level===axState.level).map(r=>[r.title,r.detail,r.level])];
  if(!rows)return;const a=document.createElement('a'),url=URL.createObjectURL(new Blob(['\ufeff'+rows.map(r=>r.map(csvCell).join(';')).join('\r\n')],{type:'text/csv;charset=utf-8'}));a.href=url;a.download=view+'-kayitlar.csv';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
reports=()=>axPage('reports');activities=()=>axPage('activity');settings=()=>axPage('settings');
const axOriginalOperatingPage=operatingPage;
operatingPage=()=>['profit','alerts','automation'].includes(view)?axPage(view):axOriginalOperatingPage();
localRefreshSystem=async function(){
  if(localSystemLoading)return;localSystemLoading=true;
  try{
    const [status,backups,events,users]=await Promise.all([api('system/status'),...(localAccess.role==='admin'?[api('system/backups'),api('system/events'),api('access/users')]:[])]);
    localSystem=status;localBackupRows=backups||[];localEventRows=events||[];if(users)localUserRows=users;localSystemError='';localAccessError='';
  }catch(error){localSystemError=error.message;}
  finally{localSystemLoading=false;if(view==='users'&&!modal&&!axFocused()&&!(typeof workspaceEditing==='function'&&workspaceEditing()))render();}
};
setInterval(()=>{
  if(typeof refreshOrderRevision==='function'){if(view==='users'&&!document.hidden&&!modal&&!workspaceEditing())void localRefreshSystem();return;}
  if(document.hidden||modal||axEditing()||localAccess.configured&&!localAccess.authenticated||!data.overview||axFocused())return;
  if(view==='users')void localRefreshSystem();else void axRefresh();
},5000);
if(data.overview&&axViews.includes(view))render();
