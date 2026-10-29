let wmsFulfillment = { policy: null, warehouses: [], workflows: [], selectedId: '', error: '', loading: false, busy: false };

function wfStatusLabel(status) {
  const labels = {
    received: 'Alındı', 'blocked-policy': 'Politika bekliyor', validated: 'Doğrulandı',
    'partially-allocated': 'Kısmi tahsis', 'blocked-stock': 'Stok yetersiz',
    'blocked-ambiguity': 'Belirsiz eşleşme', 'blocked-capability': 'Yetenek eksik',
    'blocked-write-disabled': 'Mock yazma kapalı', 'blocked-validation': 'Doğrulama hatası',
    'wms-submission-pending': 'WMS gönderiliyor', 'wms-outcome-unknown': 'WMS sonucu belirsiz',
    'wms-submitted': 'WMS talebi alındı', 'wms-acceptance-pending': 'Kabul bekleniyor',
    'wms-accepted': 'WMS kabul etti', 'wms-rejected': 'Depo reddetti',
    'erp-registration-pending': 'ERP kaydı gönderiliyor', 'erp-outcome-unknown': 'ERP sonucu belirsiz',
    'erp-registered': 'ERP kaydetti', 'reservation-pending': 'Rezervasyon bekleniyor',
    reserved: 'Rezerve edildi', picking: 'Toplanıyor', picked: 'Toplandı',
    packing: 'Paketleniyor', packed: 'Paketlendi', 'shipment-pending': 'Sevk bekleniyor',
    'blocked-shipment': 'Sevk doğrulanamadı', 'blocked-shipment-ambiguity': 'Sevk belirsiz',
    'updates-pending': 'Kanal/ERP güncellemesi bekliyor', 'partially-shipped': 'Kısmi sevk',
    processed: 'İşlendi', blocked: 'Bloke'
  };
  return labels[status] || status || '—';
}

function wfError(error) {
  return typeof error === 'string' ? error : error?.message || 'Fulfillment işlemi tamamlanamadı.';
}

async function wfLoad(force = false) {
  if (wmsFulfillment.loading) return;
  wmsFulfillment.loading = true;
  try {
    const [policy, warehouses, workflows] = await Promise.all([
      api('wms/fulfillment/policy'),
      api('wms/warehouses'),
      api('wms/fulfillment/workflows')
    ]);
    wmsFulfillment.policy = policy;
    wmsFulfillment.warehouses = warehouses.data || [];
    wmsFulfillment.workflows = workflows;
    if (wmsFulfillment.selectedId && !workflows.some(row => row.id === wmsFulfillment.selectedId))
      wmsFulfillment.selectedId = '';
    wmsFulfillment.error = '';
  } catch (error) {
    wmsFulfillment.error = wfError(error);
  } finally {
    wmsFulfillment.loading = false;
    if (view === 'wms-fulfillment') render();
  }
}

function wfRefresh() {
  wmsFulfillment.policy = null;
  wmsFulfillment.error = '';
  wfLoad(true);
}

function wfSelected() {
  return wmsFulfillment.workflows.find(row => row.id === wmsFulfillment.selectedId) || null;
}

function wfSelect(id) {
  wmsFulfillment.selectedId = id;
  render();
}

function wfWriteSequence(form) {
  const value = form.elements.writeSequence.value;
  return value === 'erp-first'
    ? ['erp-order-registration', 'wms-fulfillment-submission']
    : value === 'wms-first'
      ? ['wms-fulfillment-submission', 'erp-order-registration']
      : ['wms-fulfillment-submission'];
}

async function wfSavePolicy(event) {
  event.preventDefault();
  if (wmsFulfillment.busy) return;
  const form = event.currentTarget;
  const policy = {
    approved: form.elements.approved.checked,
    writeSequence: wfWriteSequence(form),
    warehousePriority: form.elements.warehousePriority.value.split(',').map(id => id.trim()).filter(Boolean),
    allowPartialShipments: form.elements.allowPartialShipments.checked,
    updateTargets: ['marketplace', 'erp']
  };
  wmsFulfillment.busy = true;
  try {
    await api('wms/fulfillment/policy', { method: 'PUT', body: JSON.stringify(policy) });
    notice(policy.approved
      ? 'Fulfillment politikası işletme onayıyla kaydedildi. Dış yazmalar yalnızca mock sağlayıcılaradır.'
      : 'Politika onaysız kaydedildi; dış işlem başlatılmaz.');
    await wfLoad(true);
  } catch (error) {
    wmsFulfillment.error = wfError(error);
    notice(wmsFulfillment.error);
  } finally {
    wmsFulfillment.busy = false;
    if (view === 'wms-fulfillment') render();
  }
}

async function wfReceive(event) {
  event.preventDefault();
  if (wmsFulfillment.busy) return;
  const form = event.currentTarget;
  const currency = form.elements.currency.value.trim().toUpperCase();
  const lines = Array.from(form.querySelectorAll('.wf-order-line'), row => ({
    externalLineId: row.querySelector('[name="externalLineId"]').value.trim(),
    sku: row.querySelector('[name="sku"]').value.trim(),
    quantity: Number(row.querySelector('[name="quantity"]').value),
    unit: row.querySelector('[name="unit"]').value.trim(),
    unitPrice: Number(row.querySelector('[name="unitPrice"]').value),
    currency
  }));
  const order = {
    marketplace: form.elements.marketplace.value.trim(),
    accountId: form.elements.accountId.value.trim(),
    externalOrderId: form.elements.externalOrderId.value.trim(),
    idempotencyKey: form.elements.idempotencyKey.value.trim(),
    status: form.elements.status.value,
    orderedAt: new Date(form.elements.orderedAt.value).toISOString(),
    currency,
    lines
  };
  wmsFulfillment.busy = true;
  try {
    const created = await api('wms/fulfillment/orders', { method: 'POST', body: JSON.stringify(order) });
    wmsFulfillment.selectedId = created.id;
    await wfLoad(true);
    notice(`Sipariş fulfillment kuyruğuna alındı: ${wfStatusLabel(created.state)}.`);
  } catch (error) {
    wmsFulfillment.error = wfError(error);
    notice(wmsFulfillment.error);
  } finally {
    wmsFulfillment.busy = false;
    if (view === 'wms-fulfillment') render();
  }
}

function wfOrderLineMarkup(index) {
  return `<fieldset class="wf-order-line"><legend>Sipariş satırı ${index + 1}</legend>
    <label>Satır ID<input name="externalLineId" required maxlength="120" value="line-${index + 1}"></label>
    <label>ERP/WMS SKU<input name="sku" required maxlength="120" placeholder="TSH-EKR-S"></label>
    <label>Miktar<input name="quantity" type="number" min="1" max="1000000" step="1" required value="1"></label>
    <label>Birim<input name="unit" required maxlength="30" value="EA"></label>
    <label>Birim fiyat<input name="unitPrice" type="number" min="0" max="10000000" step="0.01" required value="0"></label>
    <button class="btn btn-small" type="button" onclick="wfRemoveLine(this)">Satırı kaldır</button></fieldset>`;
}

function wfAddLine() {
  const container = document.getElementById('wf-order-lines');
  if (!container) throw new Error('Sipariş satır alanı bulunamadı.');
  const rows = container.querySelectorAll('.wf-order-line');
  if (rows.length >= 1000) {
    notice('Bir siparişe en fazla 1000 satır eklenebilir.');
    return;
  }
  container.insertAdjacentHTML('beforeend', wfOrderLineMarkup(rows.length));
}

function wfRemoveLine(button) {
  const container = button.closest('#wf-order-lines');
  if (!container) throw new Error('Sipariş satırı listesi bulunamadı.');
  const rows = container.querySelectorAll('.wf-order-line');
  if (rows.length > 1) button.closest('.wf-order-line').remove();
}

async function wfAdvance(id, retryValidation = false) {
  if (wmsFulfillment.busy) return;
  wmsFulfillment.busy = true;
  try {
    await api(`wms/fulfillment/workflows/${encodeURIComponent(id)}/${retryValidation ? 'retry-validation' : 'advance'}`, {
      method: 'POST',
      body: '{}'
    });
    await wfLoad(true);
  } catch (error) {
    wmsFulfillment.error = wfError(error);
    notice(wmsFulfillment.error);
  } finally {
    wmsFulfillment.busy = false;
    if (view === 'wms-fulfillment') render();
  }
}

function wfDate(value) {
  return value ? new Date(value).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' }) : '—';
}

function wfPage() {
  if (!wmsFulfillment.policy && !wmsFulfillment.loading && !wmsFulfillment.error) wfLoad();
  const policy = wmsFulfillment.policy;
  if (!policy) {
    return head('WMS Fulfillment', 'Pazaryeri siparişlerini işletme onaylı sırayla ERP ve WMS akışına yönetin.') +
      `<section class="card wf-card"><p class="${wmsFulfillment.error ? 'wf-error' : ''}">${esc(wmsFulfillment.error || 'WMS fulfillment bilgileri yükleniyor…')}</p></section>`;
  }

  const selected = wfSelected();
  const steps = policy.writeSequence || [];
  const sequenceValue = steps[0] === 'erp-order-registration'
    ? 'erp-first'
    : steps.includes('erp-order-registration') ? 'wms-first' : 'wms-only';
  const warehouseList = wmsFulfillment.warehouses.map(warehouse => `${warehouse.id} (${warehouse.code})`).join(', ');
  const actions = `<button class="btn" onclick="wfRefresh()" ${wmsFulfillment.busy ? 'disabled' : ''}>Yenile</button>`;
  const policyCard = `<section class="card wf-card"><div class="wf-section-head"><div><h2>İşletme fulfillment politikası</h2><p>ERP/WMS yazma sırasını işletme belirler. Onay yoksa siparişler bloke edilir.</p></div><span class="tag ${policy.approved ? 'tag-green' : 'tag-orange'}">${policy.approved ? 'Onaylı' : 'Onay bekliyor'}</span></div>
    <form class="wf-policy-form" onsubmit="wfSavePolicy(event)">
      <label>ERP ve WMS yazma sırası<select name="writeSequence">
        <option value="erp-first" ${sequenceValue === 'erp-first' ? 'selected' : ''}>ERP sipariş kaydı → WMS talebi</option>
        <option value="wms-first" ${sequenceValue === 'wms-first' ? 'selected' : ''}>WMS talebi → ERP sipariş kaydı</option>
        <option value="wms-only" ${sequenceValue === 'wms-only' ? 'selected' : ''}>ERP sipariş kaydı yok → WMS talebi</option>
      </select></label>
      <label>Depo önceliği<input name="warehousePriority" required maxlength="2500" value="${esc((policy.warehousePriority || []).join(', '))}" placeholder="mock-main, mock-outlet"><small>Depo ID'lerini virgülle yazın; sıra tahsis önceliğidir. Mevcut depolar: ${esc(warehouseList || '—')}</small></label>
      <label class="wf-check"><input name="allowPartialShipments" type="checkbox" ${policy.allowPartialShipments ? 'checked' : ''}> Kısmi tahsise/sevke izin ver</label>
      <label class="wf-check wf-approval"><input name="approved" type="checkbox" ${policy.approved ? 'checked' : ''}> Bu sırayı işletme adına onaylıyorum</label>
      <p class="wf-note">Durum güncellemeleri mock pazaryeri ve mock ERP uçlarına idempotent gönderilir. Gerçek bağlantı veya stok yazımı yapılmaz.</p>
      <button class="btn btn-primary" type="submit" ${wmsFulfillment.busy ? 'disabled' : ''}>Politikayı kaydet</button>
    </form></section>`;

  const intake = `<section class="card wf-card"><div class="wf-section-head"><div><h2>Pazaryeri siparişini içeri al</h2><p>Normalize edilmiş sipariş ve satırları alınır; dış talep kimliği ve idempotency key zorunludur.</p></div></div>
    <form class="wf-order-form" onsubmit="wfReceive(event)">
      <label>Pazaryeri<input name="marketplace" required maxlength="50" value="trendyol"></label>
      <label>Satıcı hesabı<input name="accountId" required maxlength="120" value="seller-demo"></label>
      <label>Dış sipariş/paket ID<input name="externalOrderId" required maxlength="120" placeholder="Paket ID"></label>
      <label>Idempotency key<input name="idempotencyKey" required maxlength="160" placeholder="Benzersiz istek kimliği"></label>
      <label>Sipariş durumu<select name="status"><option>Created</option><option>Picking</option><option>Invoiced</option><option>Unshipped</option><option>Pending</option></select></label>
      <label>Sipariş zamanı<input name="orderedAt" type="datetime-local" required value="${new Date().toISOString().slice(0, 16)}"></label>
      <label>Para birimi<input name="currency" required minlength="3" maxlength="3" value="TRY"></label>
      <div id="wf-order-lines" class="wf-order-lines">${wfOrderLineMarkup(0)}</div>
      <button class="btn wf-add-line" type="button" onclick="wfAddLine()">+ Sipariş satırı ekle</button>
      <button class="btn btn-primary" type="submit" ${wmsFulfillment.busy ? 'disabled' : ''}>Siparişi doğrula ve al</button>
    </form></section>`;

  const rows = wmsFulfillment.workflows.map(row => `<tr class="${row.id === wmsFulfillment.selectedId ? 'wf-selected' : ''}">
    <td><strong>${esc(row.order.externalOrderId)}</strong><small>${esc(row.order.marketplace)} · ${esc(row.order.accountId)}</small></td>
    <td>${esc(wfStatusLabel(row.state))}${row.blockReason ? `<small class="wf-error">${esc(row.blockReason)}</small>` : ''}</td>
    <td>${row.warehouses?.length || 0}</td><td>${row.packages?.length || 0}</td><td>${wfDate(row.updatedAt)}</td>
    <td><button class="btn btn-small" onclick="wfSelect(${jsArg(row.id)})">Durumu aç</button></td></tr>`).join('');
  const workflowTable = `<section class="card wf-card"><div class="wf-section-head"><div><h2>Sipariş akışları</h2><p>WMS kabulü, işlenme ve dış sistem güncellemeleri ayrı aşamalardır.</p></div></div>
    <div class="table-wrap"><table class="table"><thead><tr><th>Sipariş</th><th>Durum</th><th>Depo</th><th>Paket</th><th>Güncelleme</th><th></th></tr></thead><tbody>${rows || '<tr><td colspan="6">Henüz fulfillment siparişi yok.</td></tr>'}</tbody></table></div></section>`;

  let detail = '';
  if (selected) {
    const retryable = ['blocked-policy', 'blocked-stock', 'blocked-validation', 'blocked', 'blocked-capability', 'blocked-write-disabled'].includes(selected.state);
    const taskRows = (selected.warehouses || []).map(task => `<tr><td>${esc(task.warehouseId)}<small>${(task.lines || []).map(line => `${esc(line.sku)} × ${esc(line.quantity)} ${esc(line.unit)}`).join('<br>')}</small></td><td>${esc(task.externalRequestId)}</td><td>${esc(task.idempotencyKey)}</td>
      <td>${esc(task.submissionState)}</td><td>${esc(task.acceptanceState)}</td><td>${esc(task.reservationState)}</td><td>${esc(task.pickingState)} / ${esc(task.packingState)}</td></tr>`).join('');
    const packageRows = (selected.packages || []).map(packageInfo => `<tr><td>${esc(packageInfo.packageId)}</td><td>${packageInfo.lines.map(line => `${esc(line.sku)} × ${esc(line.quantity)}`).join('<br>')}</td>
      <td>${esc(packageInfo.carrier || '—')}</td><td>${esc(packageInfo.trackingNumber || '—')}</td><td>${wfDate(packageInfo.shippedAt)}</td></tr>`).join('');
    const historyRows = (selected.history || []).slice().reverse().map(entry => `<tr><td>${wfDate(entry.at)}</td><td>${esc(wfStatusLabel(entry.state))}</td><td>${esc(entry.message)}</td><td>${esc(entry.externalRequestId || entry.correlationId || '—')}</td></tr>`).join('');
    const publications = (selected.publications || []).map(item => `<tr><td>${esc(item.target)}</td><td>${esc(item.packageId)}</td><td>${esc(item.state)}</td><td>${esc(item.idempotencyKey)}</td></tr>`).join('');
    detail = `<section class="card wf-card"><div class="wf-section-head"><div><h2>${esc(selected.order.externalOrderId)} · ${esc(wfStatusLabel(selected.state))}</h2><p>İstek ${esc(selected.externalRequestId)} · Idempotency ${esc(selected.idempotencyKey)}</p></div>
      <div class="wf-actions">${selected.state !== 'processed' ? `<button class="btn btn-primary" onclick="wfAdvance(${jsArg(selected.id)})" ${wmsFulfillment.busy ? 'disabled' : ''}>${selected.state === 'wms-outcome-unknown' || selected.state === 'erp-outcome-unknown' ? 'Sonucu güvenle sorgula' : 'Sonraki güvenli adım'}</button>` : ''}
      ${retryable ? `<button class="btn" onclick="wfAdvance(${jsArg(selected.id)},true)" ${wmsFulfillment.busy ? 'disabled' : ''}>Yeniden doğrula</button>` : ''}</div></div>
      ${selected.blockReason ? `<p class="wf-error" role="alert">${esc(selected.blockReason)}</p>` : ''}
      ${Object.keys(selected.unallocatedByLine || {}).length ? `<p class="wf-warning">Tahsis edilemeyen miktarlar: ${esc(JSON.stringify(selected.unallocatedByLine))}</p>` : ''}
      <h3>Depo talepleri · kabul ve işlenme durumları</h3><div class="table-wrap"><table class="table"><thead><tr><th>Depo</th><th>External request ID</th><th>Idempotency key</th><th>Gönderim</th><th>Kabul</th><th>Rezervasyon</th><th>Toplama / paketleme</th></tr></thead><tbody>${taskRows || '<tr><td colspan="7">Henüz WMS talebi oluşturulmadı.</td></tr>'}</tbody></table></div>
      <h3>Paketler ve takip</h3><div class="table-wrap"><table class="table"><thead><tr><th>Paket</th><th>Satırlar</th><th>Taşıyıcı</th><th>Takip numarası</th><th>Sevk zamanı</th></tr></thead><tbody>${packageRows || '<tr><td colspan="5">Henüz sevk bilgisi yok.</td></tr>'}</tbody></table></div>
      <h3>Pazaryeri / ERP güncellemeleri</h3><div class="table-wrap"><table class="table"><thead><tr><th>Hedef</th><th>Paket</th><th>Durum</th><th>Idempotency key</th></tr></thead><tbody>${publications || '<tr><td colspan="4">Henüz gönderim yok.</td></tr>'}</tbody></table></div>
      <h3>Durum geçmişi</h3><div class="table-wrap"><table class="table"><thead><tr><th>Zaman</th><th>Durum</th><th>Açıklama</th><th>Dış kimlik / korelasyon</th></tr></thead><tbody>${historyRows}</tbody></table></div></section>`;
  }

  return head('WMS Fulfillment', 'Pazaryeri siparişlerini onaylı ERP/WMS sırasına göre doğrulayın, bölün ve takip edin.', actions) +
    (wmsFulfillment.error ? `<p class="wf-error" role="alert">${esc(wmsFulfillment.error)}</p>` : '') +
    `<div class="wf-layout">${policyCard}${intake}${workflowTable}${detail}</div>`;
}

if (typeof navGroups !== 'undefined' && !names['wms-fulfillment']) {
  navGroups[2][1].splice(1, 0, ['wms-fulfillment', 'WMS Fulfillment', 'box']);
  names['wms-fulfillment'] = 'WMS Fulfillment';
}
if (typeof render === 'function') {
  const wmsFulfillmentOriginalRender = render;
  render = function () {
    if (view !== 'wms-fulfillment' ||
        typeof localAccess !== 'undefined' && localAccess.configured && !localAccess.authenticated)
      return wmsFulfillmentOriginalRender();
    app.innerHTML = frame(wfPage());
  };
}
