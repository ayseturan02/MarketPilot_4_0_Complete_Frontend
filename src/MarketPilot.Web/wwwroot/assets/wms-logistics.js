const wl = { rows: [], policies: null, loaded: false, busy: false, error: '' };
const wlLabels = {received:'Alındı',picking:'Toplanıyor',packed:'Paketlendi',shipped:'Sevk edildi','partially-shipped':'Kısmi sevk',delivered:'Teslim edildi','delivery-failed':'Teslimat başarısız','return-accepted':'İade kabul edildi','quality-check':'Kalite kontrolünde','quality-passed':'Kalite kontrolü uygun',quarantined:'Karantina',restocked:'WMS onaylı satılabilir iade'};
async function wlLoad() {
  if (wl.busy) return;
  wl.busy = true;
  try { [wl.rows, wl.policies] = await Promise.all([api('wms/logistics'), api('wms/logistics/policies')]); wl.loaded = true; wl.error = ''; }
  catch(e) { wl.error = e.message; }
  finally { wl.busy = false; if (['shipping','tracking','returns','labels'].includes(view)) render(); }
}
async function wlWrite(path, body, method='POST') {
  if (wl.busy) return;
  wl.busy = true;
  try { await api('wms/logistics/'+path, {method, body:JSON.stringify(body)}); wl.error = ''; }
  catch(e) { wl.error = e.message; notice(wl.error); }
  finally { wl.busy = false; await wlLoad(); }
}
function wlPolicy(event, type) {
  event.preventDefault();
  const body = Object.fromEntries(new FormData(event.target));
  body.approved = event.target.elements.approved.checked;
  return wlWrite(type, body, 'PUT');
}
function wlPage() {
  if (!wl.loaded && !wl.busy && !wl.error) wlLoad();
  const isReturn = view === 'returns';
  const rows = wl.rows.filter(r => !isReturn || r.history.some(e => e.state === 'return-accepted'));
  return head(isReturn?'WMS İadeler':view==='tracking'?'WMS Kargo Takip':view==='labels'?'Kargo Etiket Yetkisi':'WMS Gönderiler',
    'MOCK · Tamamen kurgusal senaryolar. Gerçek WMS, taşıyıcı veya pazaryeri işlemi yapılmaz.',
    `<button class="btn" onclick="wlLoad()" ${wl.busy?'disabled':''}>Yenile</button><button class="btn btn-primary" onclick="wlWrite('mock/scenarios',{})" ${wl.busy?'disabled':''}>Mock senaryoları yükle</button>`) +
    (wl.error?`<p role="alert">${esc(wl.error)}</p>`:'') +
    `<section class="card wf-card"><p>WMS sevki pazaryerinde otomatik olarak “Kargolandı” sayılmaz. Onaylı kanal eşlemesi olmayan durumlar bloke edilir. Etiket yalnız yetkili kaynaktan beklenir; bu ekran etiket üretmez.</p>
    <form onsubmit="wlPolicy(event,'mapping')"><div class="form-grid">
      <label>Kanal<input name="channel" required placeholder="trendyol"/></label>
      <label>WMS durumu<select name="wmsState">${Object.entries(wlLabels).map(([k,v])=>`<option value="${k}">${v}</option>`).join('')}</select></label>
      <label>Kanal hedef durumu<input name="channelState" required maxlength="100" placeholder="İşletmenin doğruladığı hedef"/></label>
      <label><input type="checkbox" name="approved"/> İşletme onayı</label><button class="btn" ${wl.busy?'disabled':''}>Durum eşlemesini kaydet</button></div></form>
    <form onsubmit="wlPolicy(event,'label-policy')"><div class="form-grid"><label>Kanal<input name="channel" required placeholder="trendyol"/></label>
      <label>Etiket yetkilisi<select name="authority"><option value="wms">WMS</option><option value="carrier">Taşıyıcı</option><option value="marketplace">Pazaryeri</option></select></label>
      <label><input type="checkbox" name="approved"/> İşletme onayı</label><button class="btn" ${wl.busy?'disabled':''}>Etiket yetkisini kaydet</button></div></form>
    <details><summary>Kayıtlı politikalar</summary><pre>${esc(JSON.stringify(wl.policies, null, 2))}</pre></details></section>` +
    rows.map(r=>`<section class="card wf-card"><h2>${esc(r.latest.shipmentId)} / ${esc(r.latest.packageId)}</h2>
      <p>${esc(r.latest.channel)} · Hesap ${esc(r.latest.externalAccountId || 'mock-default')} · Sipariş ${esc(r.latest.orderId)} · Depo ${esc(r.latest.warehouseId)}</p>
      <div class="table-wrap"><table class="table"><thead><tr><th>WMS durumu</th><th>Taşıyıcı / Takip</th><th>Toplama / Paketleme</th><th>Sevk zamanı</th><th>Miktar</th></tr></thead><tbody><tr>
      <td>${esc(wlLabels[r.latest.state]||r.latest.state)}</td><td>${esc(r.latest.carrier)} / ${esc(r.latest.trackingNumber||'Bekleniyor')}</td>
      <td>${esc(r.pickingStatus)} / ${esc(r.packingStatus)}</td><td>${esc(r.latest.shippedAt||'Bekleniyor')}</td><td>${esc(r.latest.quantity)} / ${esc(r.latest.orderQuantity)} ${r.partialShipment?'· Kısmi sevk':''}</td></tr></tbody></table></div>
      <p>Kanal karşılığı: ${esc(r.channelStatus||'Bloke — onaylı eşleme yok')} · Etiket: ${esc(r.labelAuthority)} / ${esc(r.labelStatus)}</p>
      <p>WMS onaylı satılabilir iade miktarı: <strong>${esc(r.resellableReturnQuantity)}</strong>. Bu bilgi fiziksel stok miktarını değiştirmez.</p>
      <details><summary>Durum geçmişi (${r.history.length})</summary><ol>${r.history.map(e=>`<li>${esc(e.at)} · ${esc(wlLabels[e.state])} · ${esc(e.eventId)}${e.wmsRestockApproved?' · WMS stok onayı':''}</li>`).join('')}</ol></details></section>`).join('') +
    (!rows.length?'<section class="card wf-card">Henüz WMS kaydı yok. Mock senaryoları yükleyebilirsiniz.</section>':'');
}
const wlOriginalRender = render;
render = function() {
  if (!['shipping','tracking','returns','labels'].includes(view) || typeof localAccess !== 'undefined' && localAccess.configured && !localAccess.authenticated) return wlOriginalRender();
  app.innerHTML = frame(wlPage());
};
