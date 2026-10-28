const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
const project=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const probe=net.createServer();await new Promise((resolve,reject)=>probe.once('error',reject).listen(0,'127.0.0.1',resolve));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-hb-http-')),base='http://127.0.0.1:'+port;
 const child=spawn('dotnet',[path.join(project,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(project,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Marketplaces__Hepsiburada__stage__Password:'',Marketplaces__Hepsiburada__production__Password:''}});
 const api=async(url,body)=>{const r=await fetch(base+url,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',Origin:base},...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:r.status,body:await r.json()};};
 let browser;
 try{
  let ready=false;for(let i=0;i<80;i++){if(child.exitCode!==null)throw Error('Isolated server exited');try{if((await api('/api/health')).status===200){ready=true;break;}}catch{}await sleep(100);}assert(ready);
  assert.equal((await api('/api/integrations/readiness')).body.hepsiburada.production,false);
  const initial=(await api('/api/integrations/demo/connections/hepsiburada/workspace')).body;
  assert.equal(initial.products.length,8);assert.equal(initial.orders.length,6);assert(initial.orders.every(o=>o.cargo.provider==='HepsiJet · Demo'));
  const savedFirst=initial.products.find(p=>p.barcode==='8691000000001');assert.equal(savedFirst.stock,32);
  const real=await api('/api/integrations/connections',{name:'Unconfigured HB',marketplace:'hepsiburada',mode:'real',environment:'stage'});assert.equal(real.status,200);
  assert.equal((await api('/api/integrations/hepsiburada/connections/'+real.body.id+'/search/products?search=term')).status,400);
  assert.equal((await api('/api/integrations/hepsiburada/connections/'+real.body.id+'/search/products?size=25')).status,409);
  browser=await chromium.launch({channel:'chrome',headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/#hepsiburada');await page.getByRole('heading',{name:'Mağaza kontrolü'}).waitFor();
  for(const tab of ['Ürünler','Stok ve fiyat','Siparişler','Kargo','Etiketler','Fatura ve garanti','İadeler','Finans','İşlem geçmişi']){await page.getByRole('button',{name:tab,exact:true}).first().click();await page.locator('.ty-business-table').waitFor();assert((await page.locator('.ty-business-table tbody tr').count())>0);}
  await page.getByRole('button',{name:'Stok ve fiyat',exact:true}).first().click();await page.locator('input[name=barcode]').fill('8691000000001');await page.getByRole('button',{name:'Filtrele',exact:true}).click();await page.locator('.ty-business-table').waitFor();assert.equal(await page.locator('.ty-business-table tbody tr').count(),1);
  assert.equal(await page.locator('input[name=minStock],input[name=maxStock],input[name=minPrice],input[name=maxPrice],select[name=size]').count(),0);
  await page.getByRole('button',{name:'Detay',exact:true}).first().click();
  assert((await page.locator('.ty-record-dialog').innerText()).includes('Varyantlar'));assert((await page.locator('.ty-record-dialog').innerText()).includes('Kumaş'));assert((await page.locator('.ty-record-dialog .ty-detail-metrics').innerText()).includes('Rezerve'));
  await page.getByRole('button',{name:'Pencereyi kapat',exact:true}).click();
  await page.getByRole('button',{name:'Düzenle',exact:true}).first().click();await page.locator('.ty-record-dialog input[name=quantity]').fill('45');await page.locator('.ty-record-dialog input[name=price]').fill('499.50');await page.getByRole('button',{name:'Kaydet',exact:true}).click();
  async function waitState(predicate){for(let i=0;i<35;i++){const w=(await api('/api/integrations/demo/connections/hepsiburada/workspace')).body;if(predicate(w))return w;await sleep(150);}throw Error('Demo action did not complete');}
  let state=await waitState(w=>w.products.find(p=>p.barcode==='8691000000001').stock===45);assert.equal(state.products.find(p=>p.barcode==='8691000000001').salePrice,499.50);
  await page.getByRole('button',{name:'Siparişler',exact:true}).first().click();await page.locator('.ty-business-table').waitFor();await page.locator('.ty-business-table tbody tr').filter({hasText:'DEMO-1001'}).getByRole('button',{name:'Detay',exact:true}).click();assert.equal(await page.locator('.ty-order-progress>div').count(),5);assert((await page.locator('.ty-record-dialog').innerText()).includes('Kargo ve paket'));await page.getByRole('button',{name:'Hazırlamaya al',exact:true}).click();state=await waitState(w=>w.orders.find(o=>o.orderNumber==='DEMO-1001').status==='Picking');
  await page.getByRole('button',{name:'İadeler',exact:true}).first().click();await page.locator('.ty-business-table').waitFor();await page.getByRole('button',{name:'İncele',exact:true}).first().click();const claim=state.claims.find(c=>c.status==='İnceleniyor'),before=state.products.find(p=>p.barcode===claim.barcode).stock;
  await page.getByRole('button',{name:'Demo iadesini onayla',exact:true}).click();state=await waitState(w=>w.claims.find(c=>c.id===claim.id).status==='Onaylandı');assert.equal(state.products.find(p=>p.barcode===claim.barcode).stock,before+claim.quantity);
  await page.getByRole('button',{name:'Etiketler',exact:true}).first().click();await page.getByRole('button',{name:'Etiket önizle',exact:true}).first().click();assert((await page.locator('.ty-record-dialog').innerText()).includes('GEÇERSİZ ÖRNEK'));await page.getByRole('button',{name:'Pencereyi kapat',exact:true}).click();
  await page.getByRole('button',{name:'Marka ve kategori',exact:true}).first().click();await page.locator('.hb-category-card').waitFor();assert((await page.locator('.hb-category-card').innerText()).includes('Kumaşlar'));
  await page.getByRole('button',{name:'Adresler',exact:true}).first().click();assert.equal(await page.locator('.ty-demo-addresses article').count(),2);
  await page.getByRole('button',{name:'Finans',exact:true}).first().click();await page.locator('.hb-finance-summary').waitFor();assert.equal(await page.locator('.hb-finance-summary>div').count(),4);
  await page.getByRole('button',{name:'İşlem geçmişi',exact:true}).first().click();await page.getByRole('button',{name:'Ayrıntı',exact:true}).first().click();assert((await page.locator('.ty-record-dialog').innerText()).includes('İşlem ayrıntısı'));await page.getByRole('button',{name:'Pencereyi kapat',exact:true}).click();
  await page.getByRole('button',{name:'Mağaza ekle',exact:false}).first().click();await page.locator('.store-form input[name=name]').fill('Hepsiburada ikinci demo');await page.locator('.store-form button[type=submit]').click();await page.locator('.ty-shop-picker select option:checked').filter({hasText:'Hepsiburada ikinci demo'}).waitFor({state:'attached'});
  const stores=(await api('/api/integrations/connections')).body,second=stores.find(s=>s.settings.name==='Hepsiburada ikinci demo');assert(second&&second.settings.marketplace==='hepsiburada');assert.equal((await api('/api/integrations/demo/connections/'+second.id+'/workspace')).body.products[0].stock,32);
  const trendyol=(await api('/api/integrations/demo/connections/trendyol/workspace')).body;assert.equal(trendyol.products[0].stock,36,'Hepsiburada mutation leaked into Trendyol');
  await page.reload();await page.getByRole('heading',{name:'Mağaza kontrolü'}).waitFor();await page.setViewportSize({width:390,height:844});assert.equal(await page.evaluate(()=>document.body.scrollWidth>innerWidth),false);assert.deepEqual(errors,[]);
  const persisted=Object.values(JSON.parse(fs.readFileSync(path.join(temp,'Data/integration-demo.json'),'utf8'))).find(w=>w.products.some(p=>p.barcode==='8691000000001'&&p.stock===45));assert(persisted,'Demo changes were not persisted');
  console.log('PASS isolated Hepsiburada HTTP/browser: twelve sections with product variants, order progress, labels, category/address cards, financial summaries and job details, bounded lists, exact barcode filter, stock/price edit, order progression, return stock, store form, marketplace isolation, persistence and mobile width. No live account/data changed.');
 }finally{
  if(browser)await browser.close();child.kill();await new Promise(resolve=>{if(child.exitCode!==null)return resolve();child.once('exit',resolve);setTimeout(resolve,5000);});
  const absolute=path.resolve(temp),prefix=path.resolve(os.tmpdir())+path.sep;if(!absolute.startsWith(prefix)||!path.basename(absolute).startsWith('marketpilot-hb-http-'))throw Error('Unsafe cleanup path');fs.rmSync(absolute,{recursive:true,force:true});
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
