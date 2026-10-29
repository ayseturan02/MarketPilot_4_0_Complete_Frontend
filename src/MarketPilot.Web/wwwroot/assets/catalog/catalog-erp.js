let chErpPreview=null,chErpLoading=false;
const chErpActions={added:'Yeni ERP kaydı',updated:'ERP değişikliği',unchanged:'Değişmedi',review:'İnceleme gerekli',missing:'ERP kaydı bulunamadı'};
function chApprovedImageUrl(url){return url==='/assets/mock-erp/ekru.svg'?url:safeExternalUrl(url);}
function chErpCanSync(){return typeof localAccess==='undefined'||!localAccess.configured||localAccess.role==='admin';}
async function chPreviewErp(){
 if(chErpLoading)return;chErpLoading=true;
 try{
  chErpPreview=await chApi('erp/preview',{method:'POST',body:'{}'});
  const counts=Object.entries(chErpActions).map(([key,label])=>`${label}: ${chErpPreview.changes.filter(c=>c.action===key).length}`).join(' · ');
  chDialog('Mock ERP katalog farkları',`<p>${esc(counts)}</p><p>Önizleme ana ürünleri değiştirmez. Eşleştirmeler korunur; kaybolan ve geçersiz kayıtlar incelemeye alınır.</p>${tyTable(['ERP varyantı / SKU','İşlem','Değişen alanlar / neden'],chErpPreview.changes.map(c=>{
   const p=c.before,r=c.incoming,changes=r?Object.entries({Ad:['name',r.name],SKU:['sku',r.sku],Barkod:['barcode',r.barcode],Marka:['brand',r.brand],Kategori:['category',r.category],Açıklama:['description',r.description]}).filter(([, [key,value]])=>!p||p[key]!==value).map(([label,[key,value]])=>`${label}: ${p?.[key]||'Bildirilmedi'} → ${value||'Bildirilmedi'}`):[];
   if(r&&(!p||p.attributes?.Renk!==r.color||p.attributes?.Beden!==r.size))changes.push(`Renk / beden: ${r.color} / ${r.size}`);
   if(r&&JSON.stringify(p?.images||[])!==JSON.stringify(r.images))changes.push(`Onaylı görseller: ${r.images.length}`);
   return `<tr><td>${esc(c.erpVariantId)}<small>${esc(r?.sku||p?.sku||'SKU bildirilmedi')}</small></td><td>${esc(chErpActions[c.action])}</td><td>${esc(c.reason||changes.join(' · ')||'Ana veri aynı.')}</td></tr>`;
  }))}<div class="store-form-footer"><button class="btn" onclick="closeModal()">Kapat</button>${chErpCanSync()?'<button class="btn btn-primary" onclick="chApplyErp()">Mock ERP verilerini eşitle</button>':''}</div>`,'NEBİM V3 · MOCK ERP');
 }catch(error){notice(error.message);}finally{chErpLoading=false;}
}
async function chApplyErp(){
 if(chErpLoading||!chErpPreview)return;chErpLoading=true;
 try{const result=await chApi('erp/apply',{method:'POST',body:JSON.stringify({token:chErpPreview.token})});chErpPreview=null;chInvalidate();closeModal();render();notice(`Mock ERP kataloğu okundu. ${result.review} kayıt inceleme bekliyor.`);}catch(error){notice(error.message);}finally{chErpLoading=false;}
}
