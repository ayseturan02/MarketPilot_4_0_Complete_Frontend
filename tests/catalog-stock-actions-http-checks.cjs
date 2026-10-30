const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {sourceProduct}=require('./erp-source-fixtures.cjs'),{chromium}=require('./browser-test-runtime.cjs');
(async()=>{
 const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-stock-actions-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));const base='http://127.0.0.1:'+port;
 let child,browser;
 async function api(route,body){const response=await fetch(base+'/api/'+route,{method:body===undefined?'GET':'POST',headers:{Origin:base,'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body)});const value=await response.json();assert(response.ok,route+': '+JSON.stringify(value));return value;}
 async function start(){child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Marketplaces__StockSource__BaseUrl:''}});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await new Promise(r=>setTimeout(r,100));}throw Error('Server startup');}
 async function stop(){if(!child)return;const running=child;child=null;const stopped=new Promise(r=>running.once('exit',r));running.kill();await stopped;}
 try{
  await start();
  const master=await sourceProduct(base,{sku:'TSH-EKR-S',barcode:'8691000000001',name:'Mock ERP master clothing',category:'Giyim / Tişört',stock:50,warehouseCode:'MAIN',currency:'TRY',price:449,vatIncluded:true});assert.equal(master.status,200);
  const placeholder=await sourceProduct(base,{sku:'ERP-NOT-IN-STOCK-SOURCE',barcode:'ERP-PLACEHOLDER-BC',name:'Mock unlinked ERP clothing',category:'Giyim / Tişört',stock:10,warehouseCode:'MAIN',currency:'TRY',price:449,vatIncluded:true});assert.equal(placeholder.status,200);
  const initial=await api('integrations/central-stock/run',{});const target=initial.items.find(item=>item.sku==='TSH-EKR-S');assert(target);
  for(let i=0;i<30;i++){if((await api('integrations/central-stock')).pendingJobs===0)break;await new Promise(r=>setTimeout(r,200));}
  await stop();
  // Offline JSON fixture preparation; the ERP master is read-only through normal UI/API.
  const demoFile=path.join(temp,'Data/integration-demo.json'),demo=JSON.parse(fs.readFileSync(demoFile,'utf8'));
  const storeKey=Object.keys(demo).find(key=>key.startsWith('hepsiburada|'));assert(storeKey);const workspace=demo[storeKey];
  const native=workspace.products.find(product=>product.barcode===target.barcode);assert(native);native.sku='ALIAS-MAP';native.barcode='ALIAS-MAP-BC';
  const template=workspace.orders[0];
  for(const [id,sku,barcode] of [['ALIAS-ORDER','ALIAS-MAP','ALIAS-MAP-BC'],['IGNORE-ORDER','IGNORE-SKU','IGNORE-BC']])workspace.orders.push({...template,packageId:id,orderNumber:id,status:'Created',orderDate:new Date().toISOString(),lastModifiedDate:Date.now(),lines:[{...template.lines[0],id:id+'-LINE',sku,barcode,quantity:2}]});
  fs.writeFileSync(demoFile,JSON.stringify(demo));
  const catalogFile=path.join(temp,'Data/demo-store.json'),catalog=JSON.parse(fs.readFileSync(catalogFile,'utf8'));
  const listing=Object.values(catalog.catalog.listings).find(link=>link.storeId==='hepsiburada'&&link.barcode===target.barcode);assert(listing);
  listing.sku='ALIAS-MAP';listing.barcode='ALIAS-MAP-BC';listing.productId=placeholder.body.id;fs.writeFileSync(catalogFile,JSON.stringify(catalog));
  await start();const blocked=await api('integrations/central-stock/run',{});assert.equal(blocked.unmatchedSales,2);assert.equal(blocked.items.find(item=>item.sku===target.sku).quantity,target.quantity);
  browser=await chromium.launch({headless:true});const page=await browser.newPage(),errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.goto(base);await page.locator('.content').waitFor();await page.evaluate(()=>nav('mapping'));
  await page.waitForFunction(()=>view==='mapping'&&chState().result&&!chState().loading);
  const filter=page.locator('.lf-filter-form');await filter.locator('[name=search]').fill('ALIAS-MAP');
  const filtered=page.waitForResponse(response=>response.url().includes('/api/catalog/listings?')&&response.url().includes('search=ALIAS-MAP'));
  await filter.getByRole('button',{name:'Filtrele',exact:true}).click();await filtered;
  await page.locator('tr').filter({hasText:'ALIAS-MAP'}).getByRole('button',{name:'Eşleştirmeyi değiştir',exact:true}).click();
  const dialog=page.locator('.ch-dialog');await dialog.locator('input[name=search]').fill(target.sku);await dialog.getByRole('button',{name:'Merkezi ürün ara',exact:true}).click();
  const mapped=page.waitForResponse(response=>response.url().endsWith('/api/catalog/mapping')&&response.request().method()==='PUT');
  await dialog.getByRole('button',{name:'Bu ürüne bağla',exact:true}).click();assert.equal((await mapped).status(),200);
  assert.equal((await api('catalog/listings?search=ALIAS-MAP')).items[0].product.id,master.body.id);
  await page.locator('tr').filter({hasText:'ALIAS-MAP'}).getByText(master.body.name,{exact:true}).waitFor();
  await page.evaluate(()=>nav('connections'));const ignoredRow=page.locator('.cs-unmatched li').filter({hasText:'IGNORE-SKU'});
  await ignoredRow.getByRole('button',{name:'Yoksay',exact:true}).waitFor();
  page.once('dialog',dialog=>dialog.dismiss());await ignoredRow.getByRole('button',{name:'Yoksay',exact:true}).click();assert.equal((await api('integrations/central-stock')).unmatchedSales,2);
  page.once('dialog',dialog=>dialog.accept());const ignored=page.waitForResponse(response=>response.url().endsWith('/unmatched/ignore'));
  await ignoredRow.getByRole('button',{name:'Yoksay',exact:true}).click();assert.equal((await ignored).status(),200);
  await ignoredRow.getByRole('button',{name:'Yoksay',exact:true}).waitFor({state:'detached'});
  const afterIgnore=await api('integrations/central-stock');assert.equal(afterIgnore.unmatchedSales,1);assert.equal(afterIgnore.items.find(item=>item.sku===target.sku).quantity,target.quantity);
  const synced=page.waitForResponse(response=>response.url().endsWith('/central-stock/run'));
  await page.getByRole('button',{name:'Eşleşmeleri yeniden değerlendir',exact:true}).click();await synced;
  const result=await api('integrations/central-stock');assert.equal(result.unmatchedSales,0);assert.equal(result.items.find(item=>item.sku===target.sku).quantity,target.quantity-2);
  assert.equal(result.sales.filter(sale=>sale.orderNumber==='ALIAS-ORDER'&&sale.sku===target.sku&&sale.quantity===2).length,1);
  assert(!result.sales.some(sale=>sale.orderNumber==='IGNORE-ORDER'));
  const repeated=await api('integrations/central-stock/run',{});assert.equal(repeated.items.find(item=>item.sku===target.sku).quantity,target.quantity-2);assert.deepEqual(errors,[]);
  await browser.close();browser=null;await stop();await start();
  const restarted=await api('integrations/central-stock/run',{});assert.equal(restarted.unmatchedSales,0);assert.equal(restarted.items.find(item=>item.sku===target.sku).quantity,target.quantity-2);assert(!restarted.sales.some(sale=>sale.orderNumber==='IGNORE-ORDER'));
  console.log('PASS stock browser actions: real catalog remap form resolves an alias sale to the selected ERP master; Ignore cancel/confirm is exercised; ignored sale never deducts stock; replay never deducts twice. Offline JSON fixtures and mock stores only.');
 }finally{if(browser)await browser.close();await stop();const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-stock-actions-'));fs.rmSync(resolved,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
