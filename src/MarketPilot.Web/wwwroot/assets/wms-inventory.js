let wmsInventoryData = null;
let wmsInventoryError = '';
let wmsInventoryPromise = null;
let wmsInventoryBusy = false;
let wmsInventoryTab = 'stock';
let wmsInventoryWarehouse = '';

function wmsLoadInventory() {
  if (wmsInventoryPromise) return wmsInventoryPromise;
  wmsInventoryPromise = api('wms/inventory').then(result => {
    wmsInventoryData = result;
    wmsInventoryError = '';
  }).catch(error => {
    wmsInventoryError = error.message;
  }).finally(() => {
    wmsInventoryPromise = null;
    if (view === 'inventory') render();
  });
  return wmsInventoryPromise;
}

function wmsRefreshInventory() {
  wmsInventoryData = null;
  wmsLoadInventory();
}

function wmsSelectTab(tab) {
  wmsInventoryTab = tab;
  render();
}

function wmsSelectWarehouse(warehouseId) {
  wmsInventoryWarehouse = warehouseId;
  render();
}

function wmsNumber(value) {
  return value == null ? '—' : new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 }).format(value);
}

function wmsDate(value) {
  return value ? new Date(value).toLocaleString('tr-TR', { dateStyle: 'short', timeStyle: 'short' }) : '—';
}

function wmsEscape(value) {
  return esc(String(value ?? ''));
}

async function wmsSavePolicy(event) {
  event.preventDefault();
  if (wmsInventoryBusy) return;
  const form = event.currentTarget;
  const safetyStockByWarehouse = {};
  const warehouseAssignmentsByAccount = {};
  const channelStockLimits = {};

  form.querySelectorAll('[data-wms-safety]').forEach(input => {
    safetyStockByWarehouse[input.dataset.wmsSafety] = Number(input.value);
  });
  form.querySelectorAll('[data-wms-account]').forEach(select => {
    warehouseAssignmentsByAccount[select.dataset.wmsAccount] =
      Array.from(select.selectedOptions, option => option.value);
  });
  form.querySelectorAll('[data-wms-limit]').forEach(input => {
    if (input.value.trim() !== '') channelStockLimits[input.dataset.wmsLimit] = Number(input.value);
  });

  wmsInventoryBusy = true;
  render();
  try {
    await api('wms/inventory/policy', {
      method: 'PUT',
      body: JSON.stringify({ safetyStockByWarehouse, warehouseAssignmentsByAccount, channelStockLimits })
    });
    notice('Stok hesaplama kuralı onaylandı; depo ve kanal ayarları kaydedildi.');
    wmsInventoryData = null;
    await wmsLoadInventory();
  } catch (error) {
    wmsInventoryError = error.message;
    notice(error.message);
  } finally {
    wmsInventoryBusy = false;
    if (view === 'inventory') render();
  }
}

function wmsInventoryPage() {
  if (!wmsInventoryData && !wmsInventoryPromise && !wmsInventoryError) wmsLoadInventory();
  if (!wmsInventoryData) {
    return head('Stok & Depolar', 'WMS kaynaklı çok depolu stok görünümü ve fark incelemesi.') +
      `<section class="card wms-card"><div class="wms-message ${wmsInventoryError ? 'wms-error' : ''}">${wmsEscape(wmsInventoryError || 'WMS stokları okunuyor…')}</div></section>`;
  }

  const data = wmsInventoryData;
  const selectedWarehouse = data.warehouses.find(warehouse => warehouse.id === wmsInventoryWarehouse);
  const rows = data.stock.filter(row => !selectedWarehouse || row.warehouse.id === selectedWarehouse.id);
  const sellableRows = rows.filter(row => !row.snapshot.providerManaged);
  const totalPhysical = sellableRows.every(row => row.snapshot.onHandQuantity != null)
    ? sellableRows.reduce((total, row) => total + row.snapshot.onHandQuantity, 0) : null;
  const totalReserved = sellableRows.every(row => row.snapshot.reservedQuantity != null)
    ? sellableRows.reduce((total, row) => total + row.snapshot.reservedQuantity, 0) : null;
  const totalSellable = sellableRows.every(row => row.sellableQuantity != null)
    ? sellableRows.reduce((total, row) => total + row.sellableQuantity, 0) : null;
  const visibleMovements = data.movements.filter(row =>
    !selectedWarehouse || row.warehouseId === selectedWarehouse.id);
  const visibleHistory = data.history.filter(row =>
    !selectedWarehouse || row.warehouseId === selectedWarehouse.id);

  const stockRows = rows.map(row => {
    const snapshot = row.snapshot;
    const managed = snapshot.providerManaged;
    const stateTag = managed
      ? `<span class="tag tag-orange">Sağlayıcı yönetiyor · ${wmsEscape(snapshot.providerName || 'harici stok')}</span>`
      : row.sellableState === 'known'
        ? `<span class="tag tag-green">Hesaplandı</span>`
        : row.sellableState === 'negative-stock-blocked'
          ? `<span class="tag tag-red">Negatif · satış bloke</span>`
          : `<span class="tag tag-orange">Bilinmiyor · satışa kapalı</span>`;
    return `<tr class="${managed ? 'wms-managed-row' : ''}">
      <td><strong>${wmsEscape(snapshot.sku)}</strong>${managed ? `<small>${wmsEscape(snapshot.providerName || 'Sağlayıcı yönetimli')}</small>` : ''}</td>
      <td>${wmsEscape(row.warehouse.name)}<small>${wmsEscape(row.warehouse.code)}</small></td>
      <td class="wms-number">${wmsNumber(snapshot.onHandQuantity)} <small>${wmsEscape(snapshot.unit)}</small></td>
      <td class="wms-number">${wmsNumber(snapshot.reservedQuantity)}</td>
      <td class="wms-number">${wmsNumber(snapshot.damagedQuantity)}</td>
      <td class="wms-number">${wmsNumber(snapshot.blockedQuantity)}</td>
      <td class="wms-number">${wmsNumber(snapshot.returnInspectionQuantity)}</td>
      <td class="wms-number">${wmsNumber(snapshot.allocatedQuantity)}</td>
      <td class="wms-number">${wmsNumber(snapshot.inTransferQuantity)}</td>
      <td class="wms-number">${wmsNumber(row.safetyStock)}</td>
      <td class="wms-number"><strong>${managed ? '—' : wmsNumber(row.sellableQuantity)}</strong><small>${stateTag}</small></td>
      <td>${wmsDate(snapshot.capturedAt)}<small>Sürüm ${wmsNumber(snapshot.sourceVersion)}</small></td>
    </tr>`;
  }).join('');

  const movements = visibleMovements.map(movement => `<tr>
    <td>${wmsDate(movement.occurredAt)}</td><td>${wmsEscape(movement.warehouseId)}</td>
    <td>${wmsEscape(movement.sku)}</td><td class="wms-number">${wmsNumber(movement.quantityDelta)} ${wmsEscape(movement.unit)}</td>
    <td>${wmsEscape(movement.reason)}</td><td>${wmsEscape(movement.id)}</td>
  </tr>`).join('');
  const snapshotHistory = visibleHistory.map(entry => `<tr>
    <td>${wmsDate(entry.receivedAt)}</td><td>${wmsEscape(entry.warehouseId)}</td>
    <td>${wmsEscape(entry.sku)}</td><td>${wmsEscape(entry.disposition)}</td>
    <td class="wms-number">${wmsNumber(entry.previousOnHand)} → ${wmsNumber(entry.incomingOnHand)}</td>
    <td>${wmsNumber(entry.sourceVersion)}<small>${wmsEscape(entry.reason || '')}</small></td>
  </tr>`).join('');
  const discrepancyRows = data.discrepancies.filter(item => !selectedWarehouse || item.warehouseCode === selectedWarehouse.code).map(item => `<tr>
    <td>${wmsEscape(item.warehouseCode)}</td><td>${wmsEscape(item.sku)}</td>
    <td class="wms-number">${wmsNumber(item.wmsQuantity)} ${wmsEscape(item.wmsUnit)}</td>
    <td class="wms-number">${wmsNumber(item.erpQuantity)} ${wmsEscape(item.erpUnit)}</td>
    <td><span class="tag ${item.state === 'conflict' ? 'tag-red' : 'tag-orange'}">${wmsEscape(item.state)}</span></td>
    <td>${wmsDate(item.wmsSourceTime)}</td>
  </tr>`).join('');

  const tabs = `<div class="wms-tabs">
    <button class="${wmsInventoryTab === 'stock' ? 'active' : ''}" onclick="wmsSelectTab('stock')">Depo stok detayları</button>
    <button class="${wmsInventoryTab === 'movements' ? 'active' : ''}" onclick="wmsSelectTab('movements')">Hareket geçmişi</button>
    <button class="${wmsInventoryTab === 'differences' ? 'active' : ''}" onclick="wmsSelectTab('differences')">Fark inceleme (${data.discrepancies.length})</button>
  </div>`;

  let detail;
  if (wmsInventoryTab === 'movements') {
    detail = `<section class="card wms-section"><div class="panel-title">WMS stok hareketleri</div>
      <div class="table-wrap"><table class="table wms-table"><thead><tr><th>Kaynak zamanı</th><th>Depo</th><th>SKU</th><th>Miktar</th><th>Neden</th><th>Kaynak kimliği</th></tr></thead>
      <tbody>${movements || '<tr><td colspan="6">Bu depo için stok hareketi bulunmuyor.</td></tr>'}</tbody></table></div>
      <div class="panel-title">Stok görüntüsü olay geçmişi</div>
      <div class="table-wrap"><table class="table wms-table"><thead><tr><th>Alınma zamanı</th><th>Depo</th><th>SKU</th><th>Sonuç</th><th>Fiziksel stok</th><th>Sürüm / not</th></tr></thead>
      <tbody>${snapshotHistory || '<tr><td colspan="6">Henüz stok görüntüsü değişikliği yok.</td></tr>'}</tbody></table></div>
    </section>`;
  } else if (wmsInventoryTab === 'differences') {
    detail = `<section class="card wms-section"><div class="panel-title">ERP / WMS fark incelemesi</div>
      <p class="wms-hint">Kaynaklar farklıysa stok otomatik seçilmez veya üzerine yazılmaz. Fark, eşleşmeyen birim ya da belirsiz ERP kaydı incelenmek üzere gösterilir.</p>
      <div class="table-wrap"><table class="table wms-table"><thead><tr><th>Depo</th><th>SKU</th><th>WMS fiziksel</th><th>ERP fiziksel</th><th>Durum</th><th>WMS kaynak zamanı</th></tr></thead>
      <tbody>${discrepancyRows || '<tr><td colspan="6">Eşleşen ERP/WMS farkı bulunmuyor.</td></tr>'}</tbody></table></div>
    </section>`;
  } else {
    detail = `<section class="card wms-section"><div class="panel-head"><div><div class="panel-title">Depo bazlı WMS stok detayları</div>
      <div class="panel-sub">Fiziksel stok ve her stok kategorisi ayrı gösterilir. Bilinmeyen bileşen satılabilir stoğu bilinmiyor yapar.</div></div>
      <select aria-label="Depo filtresi" onchange="wmsSelectWarehouse(this.value)"><option value="">Tüm depolar</option>
      ${data.warehouses.map(warehouse => `<option value="${wmsEscape(warehouse.id)}" ${selectedWarehouse?.id === warehouse.id ? 'selected' : ''}>${wmsEscape(warehouse.name)}</option>`).join('')}</select>
      </div>
      <div class="table-wrap"><table class="table wms-table"><thead><tr><th>SKU</th><th>Depo</th><th>Fiziksel</th><th>Rezervasyon</th><th>Hasarlı</th><th>Bloke</th><th>İade kontrol</th><th>Ayrılmış</th><th>Transferde</th><th>Güvenlik</th><th>Satılabilir</th><th>Kaynak zamanı</th></tr></thead>
      <tbody>${stockRows || '<tr><td colspan="12">Bu depoda stok görüntüsü yok.</td></tr>'}</tbody></table></div>
      <p class="wms-hint">FBA ve n11depom gibi sağlayıcı yönetimli satırlar korunur; MarketPilot satılabilir toplamına veya depo dağıtımına dahil edilmez. Ekran stok değişikliği göndermez.</p>
    </section>`;
  }

  const policyForm = `<section class="card wms-section"><div class="panel-head"><div><div class="panel-title">Stok kuralı ve kanal depo dağılımı</div>
    <div class="panel-sub">Onaylı kural: fiziksel − rezervasyon − hasarlı − bloke − iade kontrolü − ayrılmış − transferde − güvenlik stoğu. Sonuç eksiye düşerse sıfır olur.</div></div>
    <span class="tag ${data.ruleApprovedAt ? 'tag-green' : 'tag-orange'}">${data.ruleApprovedAt ? 'İşletme onaylı' : 'İşletme onayı bekleniyor'}</span></div>
    <form class="wms-policy-form" onsubmit="wmsSavePolicy(event)">
      <div class="wms-policy-grid">${data.warehouses.map(warehouse => `<label>${wmsEscape(warehouse.name)} güvenlik stoğu
        <input type="number" min="0" max="1000000" step="1" required data-wms-safety="${wmsEscape(warehouse.id)}" value="${data.policy.safetyStockByWarehouse[warehouse.id] ?? 0}"></label>`).join('')}</div>
      <div class="wms-account-list">${data.marketplaceAccounts.map(account => {
        const assigned = data.policy.warehouseAssignmentsByAccount[account.id] || [];
        return `<div class="wms-account-row"><div><strong>${wmsEscape(account.name)}</strong><small>${wmsEscape(account.marketplace)} · depo seçimi çoklu olabilir</small></div>
          <label>Atanmış depolar<select multiple size="${Math.min(Math.max(data.warehouses.length, 2), 5)}" data-wms-account="${wmsEscape(account.id)}">
          ${data.warehouses.map(warehouse => `<option value="${wmsEscape(warehouse.id)}" ${assigned.includes(warehouse.id) ? 'selected' : ''}>${wmsEscape(warehouse.name)}</option>`).join('')}</select></label>
          <label>Kanal stok üst limiti<input type="number" min="0" max="1000000" step="1" placeholder="Limitsiz" data-wms-limit="${wmsEscape(account.id)}" value="${data.policy.channelStockLimits[account.id] ?? ''}"></label>
        </div>`;
      }).join('')}</div>
      <div class="wms-actions"><button class="btn btn-primary" type="submit" ${wmsInventoryBusy ? 'disabled' : ''}>${wmsInventoryBusy ? 'Kaydediliyor…' : 'Kuralı onayla ve ayarları kaydet'}</button>
      <span class="wms-hint">Bu ayarlar yalnız hesaplamayı ve görünümü etkiler; pazaryerine stok göndermez.</span></div>
    </form>
  </section>`;

  const channelPreview = `<section class="card wms-section"><div class="panel-title">Hesap bazlı stok önizlemesi</div>
    <div class="table-wrap"><table class="table"><thead><tr><th>Pazaryeri hesabı</th><th>SKU</th><th>Depolar</th><th>Üst limit</th><th>Satılabilir</th></tr></thead>
    <tbody>${data.channelStock.map(row => `<tr><td>${wmsEscape(row.accountName)}</td><td>${wmsEscape(row.sku)}</td>
    <td>${row.warehouseIds.map(id => wmsEscape(data.warehouses.find(w => w.id === id)?.name || id)).join(', ') || 'Atanmamış'}</td>
    <td>${wmsNumber(row.channelStockLimit)}</td><td>${wmsNumber(row.sellableQuantity)}</td></tr>`).join('') || '<tr><td colspan="5">Kanal stok hesabı yok.</td></tr>'}</tbody></table></div></section>`;
  const sourceTime = data.stock.reduce((latest, row) =>
    !latest || new Date(row.snapshot.capturedAt) > new Date(latest) ? row.snapshot.capturedAt : latest, null);
  const summary = `<div class="wms-summary">
    <div><small>WMS fiziksel stok</small><strong>${wmsNumber(totalPhysical)}</strong><span>provider yönetimli hariç</span></div>
    <div><small>Rezervasyon</small><strong>${wmsNumber(totalReserved)}</strong><span>fiziksel stoktan ayrı</span></div>
    <div><small>Satılabilir stok</small><strong>${wmsNumber(totalSellable)}</strong><span>onaylı kural ile hesaplandı</span></div>
    <div><small>ERP/WMS incelenecek fark</small><strong>${data.discrepancies.length}</strong><span>otomatik seçim yapılmaz</span></div>
  </div>`;

  return head('Stok & Depolar', 'WMS kaynaklı çok depolu stok, satılabilir miktar ve stok farklarını inceleyin.',
    `<button class="btn" onclick="wmsRefreshInventory()" ${wmsInventoryBusy ? 'disabled' : ''}>Yenile</button>`) +
    `<section class="wms-card">${wmsInventoryError ? `<div class="wms-message wms-error">${wmsEscape(wmsInventoryError)}</div>` : ''}
      <div class="wms-source"><span class="tag tag-blue">${wmsEscape(data.profile.providerKey)} · ${data.capabilities.isMock ? 'MOCK WMS' : 'WMS'}</span>
      <span>Son okuma: <strong>${wmsDate(data.readAt)}</strong></span><span>Kaynak görüntüsü: <strong>${wmsDate(sourceTime)}</strong></span>
      <span>${data.warehouses.length} depo · ${data.stock.length} stok satırı</span></div>
      ${summary}${tabs}${detail}${policyForm}${channelPreview}</section>`;
}
