let mpStockRequest=null;
function mpStockForm(id){
  const store=(data['integrations/connections']||[]).find(s=>s.id===id);if(!store)return;
  mpStockRequest=crypto.randomUUID();
  showModal(`<form class="store-form" onsubmit="mpStockSubmit(event,'${esc(id)}')">
    <h2>Stok / fiyat gönder</h2><p>${esc(store.settings.name)} · ${store.settings.mode==='demo'?'Demo mağaza':'Gerçek mağaza'}</p>
    <div class="store-form-grid">
      <label class="store-field"><span>Satıcı SKU / stok kodu</span><input name="sku" maxlength="100" autocomplete="off"><small>Hepsiburada, n11 ve Amazon için gerekli.</small></label>
      <label class="store-field"><span>Barkod</span><input name="barcode" maxlength="100" autocomplete="off"></label>
      <label class="store-field"><span>Hedef stok</span><input name="quantity" type="number" required min="0" max="1000000" step="1"></label>
      <label class="store-field"><span>Yeni satış fiyatı (isteğe bağlı)</span><input name="price" type="number" min="0.01" max="10000000" step="0.01"><small>Boş bırakırsan mevcut fiyat korunur.</small></label>
    </div>
    <label class="store-field"><span><input name="confirm" type="checkbox" required> Bu mağazanın stok/fiyatının güncellenmesini onaylıyorum.</span></label>
    <p role="status" data-write-status>Gönderim kalıcı kuyruğa alınır. Sonuç mağazadan doğrulanır; Senkronizasyon ekranından izlenir.</p>
    <div class="store-form-footer"><button type="button" class="btn" onclick="closeModal()">Vazgeç</button><button class="btn btn-primary" type="submit">Kuyruğa al</button></div>
  </form>`);
}
async function mpStockSubmit(event,id){
  event.preventDefault();const form=event.target,button=form.querySelector('[type=submit]'),status=form.querySelector('[data-write-status]');
  const raw=Object.fromEntries(new FormData(form));if(!raw.sku.trim()&&!raw.barcode.trim()){status.textContent='SKU veya barkod girin.';return;}
  button.disabled=true;
  try{
    const response=await fetch('/api/integrations/connections/'+encodeURIComponent(id)+'/stock-price',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({requestId:mpStockRequest,sku:raw.sku.trim()||null,barcode:raw.barcode.trim()||null,quantity:Number(raw.quantity),price:raw.price?Number(raw.price):null,confirm:raw.confirm==='on'})});
    const result=await response.json();if(!response.ok)throw Error(result.error||'Gönderim kuyruğa alınamadı.');
    modal=null;render();notice(result.message);
  }catch(e){status.textContent=e.message;button.disabled=false;}
}
