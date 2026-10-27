// Trendyol API Merkezi — yalnızca Trendyol ekranında kullanılır. Diğer pazaryerleri kendi assets/<pazaryeri>/ klasörlerine eklenir.
let tyCatalog = null, tyCatalogLoading = false, tyCatalogError = '';
let tyPanel = 'products', tyStore = '', tyOpenKey = '', tyBusy = '';
const tyResults = {}, tyDrafts = {};

function tyEnsureCatalog() {
  if (tyCatalog || tyCatalogLoading || tyCatalogError) return;
  tyCatalogLoading = true;
  fetch('/api/integrations/trendyol/operations').then(r => r.ok ? r.json() : r.json().then(b => Promise.reject(new Error(b.error || `Katalog okunamadı (${r.status}).`))))
    .then(body => { tyCatalog = body; }).catch(e => { tyCatalogError = e.message; }).finally(() => { tyCatalogLoading = false; render(); });
}
function tyStores() { return (data['integrations/connections'] || []).filter(c => c.settings.marketplace === 'trendyol'); }
function tyCurrentStore() { const stores = tyStores(); return stores.find(c => c.id === tyStore) || stores[0] || null; }
function tyOperation(key) { for (const p of tyCatalog?.panels || []) { const op = p.operations.find(o => o.key === key); if (op) return op; } return null; }
function tyMethodBadge(method) { return `<span class="ty-method ty-${esc(method.toLowerCase())}">${esc(method)}</span>`; }
function tyDocUrl(op) { return op.doc ? 'https://developers.trendyol.com/docs/' + encodeURIComponent(op.doc) : ''; }

function trendyolApiPanel() {
  tyEnsureCatalog();
  const head = `<div class="panel-head"><div><div class="panel-title">Trendyol API Merkezi</div><p>Tüm Trendyol entegrasyon servisleri panel panel ayrılmıştır. Okuma servisleri doğrudan çalışır; yazma servisleri sunucuda açıkça etkinleştirilip her seferinde onaylanmadan gönderilmez.</p></div></div>`;
  if (tyCatalogError) return `<section class="card integration-panel ty-panel">${head}<p class="integration-warning">${esc(tyCatalogError)}</p></section>`;
  if (!tyCatalog) return `<section class="card integration-panel ty-panel">${head}<p class="integration-note">Servis kataloğu yükleniyor…</p></section>`;
  const panels = tyCatalog.panels, panel = panels.find(p => p.key === tyPanel) || panels[0], store = tyCurrentStore();
  const total = panels.reduce((n, p) => n + p.operations.length, 0), env = store ? tyCatalog.environments[store.settings.environment] : null;
  const storeSelect = tyStores().length ? `<label>Mağaza<select onchange="tyStore=this.value;render()">${tyStores().map(c => `<option value="${esc(c.id)}" ${store?.id === c.id ? 'selected' : ''}>${esc(c.settings.name)} · ${c.settings.mode === 'demo' ? 'Demo' : 'Gerçek'} · ${esc(c.settings.environment)}</option>`).join('')}</select></label>` : '';
  const status = !store ? '<p class="integration-warning">Önce yukarıdan bir Trendyol mağazası ekleyin.</p>'
    : store.settings.mode === 'demo' ? '<p class="integration-note">Demo mağaza seçili: istekler Trendyol’a gönderilmez, hazırlanan URL ve gövde önizlenir.</p>'
    : `<p class="integration-note">Ortam: <strong>${esc(store.settings.environment)}</strong> · Kimlik bilgileri: <strong>${env?.configured ? 'hazır' : 'eksik'}</strong> · Yazma servisleri: <strong>${env?.writes ? 'açık' : 'kapalı (yalnızca önizleme)'}</strong></p>`;
  return `<section class="card integration-panel ty-panel">${head}
    <div class="ty-toolbar">${storeSelect}<span class="ty-count">${panels.length} panel · ${total} servis</span></div>${status}
    <div class="op-tab-scroll"><div class="op-tabs">${panels.map(p => `<button class="op-tab ${p.key === panel.key ? 'active' : ''}" onclick="tyPanel='${esc(p.key)}';tyOpenKey='';render()">${esc(p.title)} <small>${p.operations.length}</small></button>`).join('')}</div></div>
    <p class="ty-panel-desc">${esc(panel.description)}</p>
    <div class="ty-ops">${panel.operations.map(op => tyOperationRow(op, store)).join('')}</div></section>`;
}

function tyOperationRow(op, store) {
  const open = tyOpenKey === op.key;
  return `<article class="ty-op ${open ? 'open' : ''}"><button class="ty-op-head" onclick="tyOpenKey=tyOpenKey==='${esc(op.key)}'?'':'${esc(op.key)}';render()">${tyMethodBadge(op.method)}<span class="ty-op-title"><strong>${esc(op.title)}</strong><code>${esc(op.path)}</code></span>${op.write ? '<span class="ty-tag ty-write">Yazma</span>' : '<span class="ty-tag">Okuma</span>'}${op.stageOnly ? '<span class="ty-tag ty-stage">Yalnızca stage</span>' : ''}</button>
    ${open ? tyOperationForm(op, store) : ''}</article>`;
}

function tyOperationForm(op, store) {
  const d = tyDrafts[op.key] || {}, doc = tyDocUrl(op);
  const input = (group, name, value, hint = '') => `<label>${esc(name)}${hint}<input name="${group}:${esc(name)}" value="${esc(value ?? '')}" maxlength="2000"/></label>`;
  const pathInputs = op.pathParameters.map(n => input('path', n, d.path?.[n], ' *')).join('');
  const queryInputs = op.query.map(n => input('query', n, d.query?.[n])).join('');
  const formInputs = op.formFields.map(n => input('form', n, d.form?.[n])).join('');
  const fileInputs = op.fileFields.map(n => `<label>${esc(n)} (dosya)<input type="file" name="file:${esc(n)}" accept="application/pdf,image/jpeg,image/png,image/webp" ${n === 'images' || n === 'files' ? 'multiple' : ''}/></label>`).join('');
  const body = op.body === 'Json' ? `<label class="ty-wide">JSON gövdesi<textarea name="body" rows="8" spellcheck="false">${esc(d.body ?? op.sampleBody ?? '')}</textarea></label>` : '';
  const result = tyResults[op.key];
  return `<form class="ty-form" onsubmit="tyRun(event,'${esc(op.key)}')">
    ${op.note ? `<p class="integration-note">${esc(op.note)}</p>` : ''}
    ${pathInputs || queryInputs || formInputs || fileInputs ? `<div class="integration-filters ty-fields">${pathInputs}${queryInputs}${formInputs}${fileInputs}</div>` : ''}
    <label class="ty-wide">Ek sorgu parametreleri <small>(her satıra ad=değer)</small><textarea name="extra" rows="2" spellcheck="false">${esc(d.extra || '')}</textarea></label>
    ${body}
    <div class="ty-actions"><button class="btn btn-primary" ${tyBusy || !store ? 'disabled' : ''}>${tyBusy === op.key ? 'Çalışıyor…' : op.write ? 'Önizle / Gönder' : 'Çalıştır'}</button>${doc ? `<a class="btn" href="${esc(doc)}" target="_blank" rel="noopener noreferrer">Dokümantasyon</a>` : ''}</div>
    </form>${result ? tyResultView(op, result) : ''}`;
}

function tyResultView(op, r) {
  const payload = r.data !== undefined && r.data !== null ? JSON.stringify(r.data, null, 2) : r.text ?? '';
  const shown = payload.length > 200000 ? payload.slice(0, 200000) + '\n… (kısaltıldı, tamamı için indirin)' : payload;
  const preview = r.requestBody != null ? `<details><summary>Gönderilen / hazırlanan gövde</summary><pre class="ty-pre">${esc(JSON.stringify(r.requestBody, null, 2))}</pre></details>` : '';
  return `<div class="integration-result ty-result"><div class="integration-result-head"><strong>${r.sent ? `Trendyol HTTP ${esc(r.status)}` : 'Önizleme · gönderilmedi'}</strong><small>${tyMethodBadge(r.method)} <code>${esc(r.url)}</code></small></div>
    ${r.reason ? `<p class="integration-warning">${esc(r.reason)}</p>` : ''}${preview}
    ${shown ? `<pre class="ty-pre">${esc(shown)}</pre>` : r.sent && !r.base64 ? '<p class="integration-note">Trendyol boş yanıt döndürdü.</p>' : ''}
    ${r.base64 || payload ? `<button class="btn btn-small" type="button" onclick="tyDownload('${esc(op.key)}')">Yanıtı indir</button>` : ''}</div>`;
}

function tyReadFile(file) {
  return new Promise((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(String(reader.result).split(',')[1] || ''); reader.onerror = () => reject(new Error('Dosya okunamadı.')); reader.readAsDataURL(file); });
}

async function tyRun(event, key) {
  event.preventDefault();
  const op = tyOperation(key), store = tyCurrentStore(), form = event.target;
  if (!op || !store || tyBusy) return;
  const draft = { path: {}, query: {}, form: {}, extra: form.elements.extra?.value || '', body: form.elements.body?.value };
  const request = { path: {}, query: {}, form: {}, files: [], confirm: false };
  for (const el of form.elements) {
    const [group, name] = (el.name || '').split(':');
    if (!name) continue;
    if (group === 'file') { for (const f of el.files || []) request.files.push({ field: name, fileName: f.name, contentType: f.type, base64: await tyReadFile(f) }); continue; }
    draft[group][name] = el.value; if (el.value.trim()) request[group][name] = el.value.trim();
  }
  for (const line of draft.extra.split(/\r?\n/)) { const i = line.indexOf('='); if (i > 0 && line.slice(i + 1).trim()) request.query[line.slice(0, i).trim()] = line.slice(i + 1).trim(); }
  tyDrafts[key] = draft;
  if (op.body === 'Json' && draft.body?.trim()) { try { request.body = JSON.parse(draft.body); } catch { notice('JSON gövdesi geçersiz.'); return; } }
  const env = tyCatalog.environments[store.settings.environment];
  if (op.write && store.settings.mode === 'real' && env?.writes)
    request.confirm = confirm(`${op.title}\n\nBu işlem ${store.settings.environment} ortamındaki Trendyol hesabınızda veri değiştirir. Göndermek istiyor musunuz?`);
  tyBusy = key; render();
  try {
    const response = await fetch(`/api/integrations/trendyol/connections/${encodeURIComponent(store.id)}/operations/${encodeURIComponent(key)}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
    const body = await response.json().catch(() => ({}));
    if (body.connection) data['integrations/connections'] = data['integrations/connections'].map(c => c.id === body.connection.id ? body.connection : c);
    if (!response.ok) throw new Error(body.error || `İşlem başarısız (${response.status}).`);
    tyResults[key] = body.result;
    notice(body.result.sent ? `${op.title}: Trendyol HTTP ${body.result.status}` : `${op.title}: önizleme hazırlandı, gönderilmedi.`);
  } catch (e) { delete tyResults[key]; notice(e.message); }
  finally { tyBusy = ''; render(); }
}

function tyDownload(key) {
  const r = tyResults[key]; if (!r) return;
  let blob;
  if (r.base64) { const bytes = Uint8Array.from(atob(r.base64), c => c.charCodeAt(0)); blob = new Blob([bytes], { type: r.contentType || 'application/octet-stream' }); }
  else blob = new Blob([r.data != null ? JSON.stringify(r.data, null, 2) : r.text || ''], { type: r.data != null ? 'application/json' : 'text/plain' });
  const ext = r.base64 ? ((r.contentType || '').includes('pdf') ? 'pdf' : 'bin') : r.data != null ? 'json' : 'txt';
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `trendyol-${key}.${ext}`; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}
