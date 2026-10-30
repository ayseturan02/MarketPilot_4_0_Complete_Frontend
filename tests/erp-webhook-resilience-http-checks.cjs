const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),http=require('node:http'),net=require('node:net'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-resilience-')),sockets=new Set();let child,browser,mode='valid';
 const item={sku:'RESILIENCE-SKU',barcode:'RESILIENCE-BC',name:'Resilience source product',stock:23,price:449,currency:'TRY',warehouseCode:'MAIN',vatIncluded:true,sourceVersion:1,sourceUpdatedAt:'2026-10-29T00:00:00Z'};
 const erp=http.createServer((req,res)=>{
  if(req.headers.authorization!=='Bearer fixture-erp-key'){res.writeHead(401).end();return;}
  res.setHeader('Content-Type','application/json');
  if(mode==='timeout'){res.writeHead(200);res.write('{"items":[');return;} // Header succeeds; the response BODY stalls.
  res.end(JSON.stringify({items:mode==='duplicate'?[item,{...item,barcode:'SECOND-BC'}]:[item],nextCursor:null}));
 });erp.on('connection',socket=>{sockets.add(socket);socket.on('close',()=>sockets.delete(socket));});
 await new Promise(r=>erp.listen(0,'127.0.0.1',r));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));const base='http://127.0.0.1:'+port;
 fs.mkdirSync(path.join(temp,'Data'));fs.writeFileSync(path.join(temp,'Data/integration-connections.json'),JSON.stringify([{id:'resilience-store',settings:{name:'Resilience fixture',marketplace:'trendyol',mode:'real',environment:'production'},status:'untested'}]));
 const env={...process.env};for(const k of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__|Enterprise__|Webhooks__|Businesses__|Deployment__)/i.test(k))delete env[k];
 Object.assign(env,{ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Critical',Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Enterprise__Connectors__erp__BaseUrl:'http://127.0.0.1:'+erp.address().port,Enterprise__Connectors__erp__ApiKey:'fixture-erp-key',Enterprise__Connectors__erp__AllowHttp:'true',Enterprise__Connectors__erp__AllowedPrivateAddresses__0:'127.0.0.1',Enterprise__Connectors__erp__TimeoutSeconds:'1',Marketplaces__Trendyol__production__SellerId:'2738',Webhooks__Trendyol__AllowInsecureLoopback:'true',Webhooks__Trendyol__Endpoints__receiver01__Enabled:'true',Webhooks__Trendyol__Endpoints__receiver01__ConnectionId:'resilience-store',Webhooks__Trendyol__Endpoints__receiver01__ApiKey:'fixture-webhook-key-32-characters-2026'});
 async function api(route,body){const r=await fetch(base+'/api/'+route,{method:body===undefined?'GET':'POST',headers:{Origin:base,'Content-Type':'application/json'},...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:r.status,body:await r.json().catch(()=>null)};}
 async function until(check,label,timeout=20000){for(let i=0;i<timeout/100;i++){if(await check())return;await sleep(100);}throw Error('Timeout: '+label);}
 const catalog=async()=>{const r=await api('sync/enterprise/run',{task:'catalog'});assert.equal(r.status,202);};
 const task=async()=>(await api('sync/enterprise')).body.tasks.find(t=>t.task==='catalog');
 function packet(id){return{content:[{shipmentPackageId:id,supplierId:2738,orderNumber:'RES-'+id,shipmentPackageStatus:'Created',lastModifiedDate:Date.now(),orderDate:Date.now()-1000,currencyCode:'TRY',packageTotalPrice:300,lines:[{lineId:id+'-1',stockCode:'RESILIENCE-SKU',barcode:'RESILIENCE-BC',quantity:2,productName:'Fixture',lineUnitPrice:100,currencyCode:'TRY',sellerId:2738},{lineId:id+'-2',stockCode:'RESILIENCE-SKU',barcode:'RESILIENCE-BC',quantity:1,productName:'Fixture',lineUnitPrice:100,currencyCode:'TRY',sellerId:2738}]}]};}
 async function hook(body){const r=await fetch(base+'/hooks/orders/receiver01',{method:'POST',headers:{'Content-Type':'application/json','x-api-key':env.Webhooks__Trendyol__Endpoints__receiver01__ApiKey},body:JSON.stringify(body)});return {status:r.status,body:await r.json()};}
 const orders=async id=>(await api('orders/search?search='+encodeURIComponent('RES-'+id))).body.items.filter(o=>o.channelOrderNumber==='RES-'+id);
 try{
  child=spawn('dotnet',[process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});
  await until(async()=>{try{return (await api('health')).status===200;}catch{return false;}},'startup');
  fs.mkdirSync(path.join(root,'artifacts/resilience'),{recursive:true});browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.route('**/*',r=>new URL(r.request().url()).origin===base?r.continue():r.abort());
  await catalog();await until(async()=>(await task()).state==='completed','initial ERP pull');
  const initial=(await api('products/search?sku=RESILIENCE-SKU')).body.items;assert.equal(initial.length,1);const product=initial[0];
  mode='duplicate';const duplicateAt=(await task()).lastRunAt;await catalog();await until(async()=>{const t=await task();return t.lastRunAt!==duplicateAt&&t.state==='blocked'&&t.error.includes('tekrarlayan SKU');},'duplicate SKU rejected');
  assert.deepEqual((await api('products/search?sku=RESILIENCE-SKU')).body.items,initial);assert.equal((await api('health')).status,200);
  await page.goto(base+'/#sync');await page.getByText(/Kaynak listesinde tekrarlayan SKU veya barkod var/).waitFor();await page.screenshot({path:path.join(root,'artifacts/resilience/erp-duplicate-sku.png'),fullPage:true});
  mode='timeout';const timeoutAt=(await task()).lastRunAt;await catalog();await until(async()=>{const t=await task();return t.lastRunAt!==timeoutAt&&t.state==='running';},'ERP request running');
  const liveStart=Date.now();assert.equal((await api('health')).status,200);assert(Date.now()-liveStart<2000,'health must respond while ERP body is stalled');
  await until(async()=>(await task()).error?.includes('zaman aşımına uğradı'),'ERP timeout reason');assert.deepEqual((await api('products/search?sku=RESILIENCE-SKU')).body.items,initial);
  await page.reload();await page.getByText(/Harici servis zaman aşımına uğradı/).waitFor();await page.screenshot({path:path.join(root,'artifacts/resilience/erp-timeout.png'),fullPage:true});
  mode='valid';item.price=459;item.sourceVersion=2;await catalog();await until(async()=>(await task()).state==='completed'&&(await task()).error===null,'ERP recovers');
  assert.equal((await api('products/search?sku=RESILIENCE-SKU')).body.items[0].price,null,'No authorized price list means unknown, not fallback');assert.equal(JSON.parse(fs.readFileSync(path.join(temp,'Data/demo-store.json'))).products.find(p=>p.sku==='RESILIENCE-SKU').price,459,'Transport snapshot still records explicit ERP value');
  const repeated=packet('duplicate-lines');assert.equal((await hook(repeated)).status,200);await until(async()=>(await orders('duplicate-lines')).length===1,'webhook saved');assert.equal((await hook(repeated)).body.duplicates,1);assert.equal((await orders('duplicate-lines'))[0].lines.length,2,'repeated SKU on distinct order lines is valid');
  // Fault the order persistence AFTER the webhook has its own independent durable inbox.
  const blockedFile=path.join(temp,'Data/demo-store.json.tmp');fs.mkdirSync(blockedFile);assert.equal((await hook(packet('write-fault'))).status,200);
  await until(async()=>(await api('system/webhooks')).body.inbox.recent.some(e=>e.error?.includes('kalıcı depoya yazılamadı')),'worker storage reason');assert.equal((await api('health')).status,200);
  await page.reload();await page.locator('[data-webhook-receiver]').getByText(/Sipariş kalıcı depoya yazılamadı/).first().waitFor();await page.screenshot({path:path.join(root,'artifacts/resilience/worker-error.png'),fullPage:true});
  fs.rmdirSync(blockedFile);assert.equal((await hook(packet('healthy-after-fault'))).status,200);await until(async()=>(await orders('healthy-after-fault')).length===1,'healthy job after failed job');await until(async()=>(await orders('write-fault')).length===1,'failed job retried');
  // The webhook worker is local, with no ERP HTTP dependency. Exercise an interrupted incoming HTTP request separately.
  await new Promise(resolve=>{const request=http.request(base+'/hooks/orders/receiver01',{method:'POST',headers:{'Content-Type':'application/json','Content-Length':'100000','x-api-key':env.Webhooks__Trendyol__Endpoints__receiver01__ApiKey}});request.on('error',()=>resolve());request.on('close',resolve);request.write('{"content":[');setTimeout(()=>request.destroy(new Error('fixture client timeout')),150);});
  assert.equal((await api('health')).status,200);assert.equal((await hook(packet('after-aborted-http'))).status,200);await until(async()=>(await orders('after-aborted-http')).length===1,'worker after disconnected callback');
  assert.equal((await api('products/search?sku=RESILIENCE-SKU')).body.items.filter(p=>p.id===product.id).length,1);assert.deepEqual(errors,[]);
  console.log('PASS ERP/webhook HTTP resilience: duplicate ERP SKU atomically rejected and visible; stalled response body times out with visible reason; health stays responsive, data preserved and next pull succeeds. Webhook repeated SKU lines and duplicate delivery are safe; durable-write failure visible, healthy jobs and retry recover; interrupted HTTP callback leaves worker alive. Fake ERP only.');
 }finally{
  if(browser)await browser.close();if(child){child.kill();await new Promise(r=>{if(child.exitCode!==null)return r();child.once('exit',r);});}
  for(const socket of sockets)socket.destroy();await new Promise(r=>erp.close(r));const target=path.resolve(temp);if(!target.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(target).startsWith('marketpilot-resilience-'))throw Error('Unsafe cleanup');fs.rmSync(target,{recursive:true,force:true});
 }
})().catch(error=>{console.error(error);process.exitCode=1});
