const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),http=require('node:http'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{randomUUID}=require('node:crypto'),{chromium}=require('./browser-test-runtime.cjs');
const {sourceProduct,importSource,key}=require('./erp-source-fixtures.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const base='http://127.0.0.1:'+port,temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-enterprise-'));let child,browser;
 const received=[],receipts=new Map();let catalogCalls=0,uncertain=true,dispatches=[],shipments=[],wmsStock=19,wmsVersion=1,wmsUpdatedAt='2026-10-29T00:00:00Z',wmsReservations=[{barcode:'8691000000001',warehouseCode:'MAIN',reservedQuantity:3,sourceVersion:1,sourceUpdatedAt:'2026-10-29T00:00:00Z'}];
 const item={sku:'TSH-EKR-S',barcode:'8691000000001',name:'ERP sourced shirt',stock:23,price:510,currency:'TRY',vatIncluded:null,images:['https://images.example.test/erp-shirt.png'],warehouseCode:'MAIN',sourceVersion:10,sourceUpdatedAt:'2026-10-29T00:00:00Z'};
 const bridge=http.createServer(async(req,res)=>{
  if(req.headers.authorization!=='Bearer fixture-bridge-key'){res.writeHead(401).end();return;}
  const url=new URL(req.url,'http://fixture');res.setHeader('Content-Type','application/json');
  if(req.method==='GET'&&url.pathname==='/v1/catalog'){catalogCalls++;res.end(JSON.stringify({items:[item],nextCursor:null}));return;}
  if(req.method==='GET'&&url.pathname==='/v1/stock'){res.end(JSON.stringify({items:[{...item,stock:wmsStock,sourceVersion:wmsVersion,sourceUpdatedAt:wmsUpdatedAt}],nextCursor:null}));return;}
  if(req.method==='GET'&&url.pathname==='/v1/reservations'){res.end(JSON.stringify({items:wmsReservations,nextCursor:null}));return;}
  if(req.method==='GET'&&url.pathname==='/v1/dispatches'){res.end(JSON.stringify({items:dispatches,nextCursor:null}));return;}
  if(req.method==='GET'&&url.pathname==='/v1/shipments'){res.end(JSON.stringify({items:shipments,nextCursor:null}));return;}
  if(req.method==='GET'&&url.pathname.startsWith('/v1/receipts/')){const id=url.pathname.split('/').at(-1);res.end(JSON.stringify(receipts.get(id)||{idempotencyKey:id,state:'not-found',safeToRetry:true}));return;}
  if(req.method==='POST'){
   let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw);received.push({path:url.pathname,...body});
   const receipt={idempotencyKey:body.idempotencyKey,state:'completed',reference:'EXT-'+body.idempotencyKey};receipts.set(body.idempotencyKey,receipt);
   if(url.pathname==='/v1/orders'&&uncertain){uncertain=false;req.socket.destroy();return;}
   res.end(JSON.stringify(receipt));return;
  }
  res.writeHead(404).end('{}');
 });await new Promise(r=>bridge.listen(0,'127.0.0.1',r));const bridgeUrl='http://127.0.0.1:'+bridge.address().port;
 const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Error',Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Enterprise__AllowDemoExchange:'true'};
 for(const k of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__)/i.test(k))delete env[k];env.Marketplaces__StockSource__BaseUrl='';
 for(const connector of ['erp','wms','cargo','channel-trendyol']){env[`Enterprise__Connectors__${connector}__BaseUrl`]=bridgeUrl;env[`Enterprise__Connectors__${connector}__ApiKey`]='fixture-bridge-key';env[`Enterprise__Connectors__${connector}__AllowHttp`]='true';env[`Enterprise__Connectors__${connector}__AllowWrites`]='true';}
 async function start(){child=spawn('dotnet',[process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});child.stdout.on('data',data=>process.stderr.write(data));child.stderr.on('data',data=>process.stderr.write(data));for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('enterprise startup');}
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(r=>{if(running.exitCode!==null)return r();running.once('exit',r);});}
 async function api(route,body,method,cookie){const r=await fetch(base+'/api/'+route,{method:method||(body===undefined?'GET':'POST'),headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:r.status,body:await r.json().catch(()=>null),cookie:r.headers.getSetCookie().map(value=>value.split(';')[0]).join('; ')};}
 async function until(check,timeout=45000){for(let i=0;i<timeout/100;i++){if(await check())return;await sleep(100);}throw Error('enterprise state timeout');}
 try{
  await start();assert.equal((await api('products',{name:'No manual',sku:'NO',price:1,stock:1})).status,405);assert.equal((await api('catalog/products',{product:{}})).status,405);
  assert.equal((await importSource(base,[item],randomUUID(),'wrong-key')).status,401);
  const requestId=randomUUID();const first=await importSource(base,[item],requestId);assert.equal(first.status,200);const product=(await first.json()).products[0];assert.equal(product.sourceSystem,'nebim-v3');
  assert.equal((await importSource(base,[item],requestId)).status,200);assert.equal((await importSource(base,[{...item,price:511}],requestId)).status,409);
  assert.equal((await importSource(base,[item,{...item,name:'Duplicate'}])).status,400);
  assert.equal((await importSource(base,[item,{...item,sku:' '+item.sku+' ',barcode:' '+item.barcode+' '}])).status,400,'normalized duplicate identities must be rejected');
  const normalized=await importSource(base,[{...item,sku:' '+item.sku+' ',barcode:' '+item.barcode+' '}]);assert.equal(normalized.status,200);assert.equal((await normalized.json()).products[0].id,product.id,'whitespace must not create a second master product');
  assert.equal((await api('sync/enterprise/run',{task:null})).status,400);
  assert.equal((await api('products/'+product.id,{price:1},'PUT')).status,409);
  assert.equal((await api('products/'+product.id+'/barcode',{barcode:'NEW-BARCODE'},'PUT')).status,409);
  assert.equal((await api('stock/movements',{productId:product.id,delta:1,reason:'manual write'})).status,409);
  assert.equal((await api('catalog/products/'+product.id,{...product,price:1},'PUT')).status,409);
  let state=(await api('sync/enterprise')).body,plan=state.plan;assert.equal(plan.fulfillmentMode,'disabled');assert(plan.schedules.every(s=>!s.enabled),'sync defaults must stay fail-closed');assert.equal(state.ownershipPolicies.length,14);assert.equal(state.ownershipPolicies.find(policy=>policy.field==='PhysicalStock').owner,'NebimV3');assert.equal(state.ownershipPolicies.find(policy=>policy.field==='Price').conflictResolution,'AuthoritativeSourceWins');assert.equal((await api('sync/enterprise/run',{task:'wms-orders'})).status,409);const store=state.stores.find(s=>s.id==='trendyol');
  plan.fulfillmentMode='standalone';
  plan.targets=[{storeId:store.id,source:store.source,enabled:true,stock:true,price:true,images:true,orders:true}];plan.warehouseCode='MAIN';plan.safetyStock=2;plan.schedules=plan.schedules.map(s=>({...s,enabled:['catalog','stock','price','images','orders','wms-reservations'].includes(s.task)}));
  assert.equal((await api('sync/enterprise/plan',{...plan,fulfillmentMode:'nebim-integrated',schedules:plan.schedules.map(s=>s.task==='wms-orders'?{...s,enabled:true}:s)},'PUT')).status,400);
  assert.equal((await api('sync/enterprise/plan',{...plan,schedules:plan.schedules.map(s=>s.task==='wms-orders'?{...s,enabled:true}:s)},'PUT')).status,200,'ERP/WMS order is determined by the approved workflow policy');
  assert.equal((await api('sync/enterprise/plan',{...plan,schedules:plan.schedules.map(s=>({...s,intervalSeconds:1}))},'PUT')).status,400);
  assert.equal((await api('sync/enterprise/plan',{...plan,schedules:plan.schedules.map((s,i)=>i===0?{...s,task:null}:s)},'PUT')).status,400);
  assert.equal((await api('sync/enterprise/plan',{...plan,schedules:plan.schedules.map(s=>s.task==='wms-reservations'?{...s,enabled:false}:s)},'PUT')).status,400,'active WMS stock publishing must include reservation snapshots');
  assert.equal((await api('sync/enterprise/plan',plan,'PUT')).status,200);
  assert.equal((await api('sync/enterprise/plan',{...plan,fulfillmentMode:'disabled',stockAuthority:'wms'},'PUT')).status,400);
  assert.equal((await api('sync/enterprise/plan',{...plan,stockAuthority:'wms'},'PUT')).status,200);
  assert.equal((await api('sync/enterprise')).body.ownershipPolicies.find(policy=>policy.field==='PhysicalStock').owner,'Wms','the configured WMS authority must be visible to users');
  await api('sync/enterprise/run',{task:'stock'});
  await until(async()=>(await api('sync/enterprise')).body.tasks.some(t=>t.task==='stock'&&t.state==='blocked'));
  assert.equal((await api('sync/enterprise')).body.jobs.filter(j=>j.kind==='stock').length,0,'WMS stock must not be sent before its first successful source read');
  assert.equal((await api('sync/enterprise/plan',plan,'PUT')).status,200);
  const readiness=(await api('sync/enterprise/readiness')).body;assert.equal(readiness.mode,'offline-inspection');assert.equal(readiness.liveVerified,false);assert.equal(readiness.ready,false);assert(readiness.checks.some(c=>c.code==='store:'+store.id&&c.state==='demo'));assert(!JSON.stringify(readiness).includes('fixture-bridge-key'));
  assert.equal((await api('sync/enterprise/run',{task:'all'})).status,202);
  await until(async()=>{const d=(await api('sync/enterprise')).body;return catalogCalls>0&&d.jobs.some(j=>j.kind==='images'&&j.state==='completed')&&d.jobs.some(j=>j.kind==='stock'&&j.state==='completed');});
  await until(async()=>(await api('sync/enterprise')).body.tasks.find(t=>t.task==='orders').state==='completed');
  const externalOrder=(await api('orders')).body.find(o=>o.sourceMode==='demo');assert(externalOrder,'demo marketplace order should be imported');
  assert.equal((await api('orders/'+externalOrder.id+'/status',{status:'Tamamlandı'},'PUT')).status,409);
  assert.equal((await api('shipments',{orderId:externalOrder.id,carrier:'manual',trackingNumber:'MANUAL-1'})).status,409);
  await until(async()=>(await api('sync/enterprise')).body.tasks.some(t=>t.task==='price'&&t.state==='blocked'));assert.equal((await api('integrations/demo/connections/trendyol/workspace')).body.products.find(p=>p.barcode===item.barcode).salePrice,449,'Legacy task cannot bypass ERP price approval');
  const untouched=(await api('integrations/demo/connections/hepsiburada/workspace')).body;assert.equal(untouched.products.find(p=>p.barcode===item.barcode).stock,32,'unselected store must remain untouched');
  assert(received.some(r=>r.path==='/v1/product-images'&&r.payload.images[0]===item.images[0]));
  const erpPlan=(await api('sync/enterprise')).body.plan,wmsPlan={...erpPlan,stockAuthority:'wms',schedules:erpPlan.schedules.map(schedule=>({...schedule,enabled:['stock','wms-stock','wms-reservations'].includes(schedule.task)}))};
  assert.equal((await api('sync/enterprise/plan',wmsPlan,'PUT')).status,200);await api('sync/enterprise/run',{task:'wms-stock'});
  await until(async()=>{const task=(await api('sync/enterprise')).body.tasks.find(row=>row.task==='wms-stock');return task.state==='completed'&&task.lastRunAt;});
  let stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;
  assert.equal(stockProduct.stock,19);assert.equal(stockProduct.stockState,'Known');assert(stockProduct.externalReferences.some(reference=>reference.system==='Wms'&&reference.entityType==='physical-stock'));
  const wmsTask=()=>api('sync/enterprise').then(response=>response.body.tasks.find(row=>row.task==='wms-stock'));
  const priorWmsRun=(await wmsTask()).lastRunAt;wmsStock=18;await api('sync/enterprise/run',{task:'wms-stock'});
  await until(async()=>{const task=await wmsTask();return task.lastRunAt!==priorWmsRun&&task.state==='blocked';});
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.stock,19,'same WMS revision with different stock must be blocked');
  const blockedWmsRun=(await wmsTask()).lastRunAt;wmsVersion=2;wmsUpdatedAt='2026-10-29T00:01:00Z';await api('sync/enterprise/run',{task:'wms-stock'});
  await until(async()=>{const task=await wmsTask();return task.lastRunAt!==blockedWmsRun&&task.state==='completed';});
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.stock,18,'a newer authoritative WMS revision should apply');
  const reservationTask=()=>api('sync/enterprise').then(response=>response.body.tasks.find(row=>row.task==='wms-reservations'));
  async function runReservations(expectedState){const prior=(await reservationTask()).lastRunAt;await api('sync/enterprise/run',{task:'wms-reservations'});await until(async()=>{const task=await reservationTask();return task.lastRunAt!==prior&&task.state===expectedState;});}
  await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;
  assert.equal(stockProduct.reservationState,'Known');assert.equal(stockProduct.reservedQuantity,3);
  assert.equal(stockProduct.sellableStockState,'Known');assert.equal(stockProduct.sellableStock,13,'sellable stock must subtract WMS reservations and configured safety stock from physical stock');
  const duplicateVersion=stockProduct.version;await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.version,duplicateVersion,'identical reservation replay must be idempotent');
  wmsReservations=[{...wmsReservations[0],reservedQuantity:99,sourceVersion:0}];await runReservations('blocked');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.reservedQuantity,3,'stale reservation snapshot must not overwrite the current balance');
  wmsReservations=[{...wmsReservations[0],reservedQuantity:4}];await runReservations('blocked');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.reservedQuantity,3,'conflicting content at an equal source revision must be blocked');
  wmsReservations=[{...wmsReservations[0],reservedQuantity:4,sourceVersion:null,sourceUpdatedAt:null}];await runReservations('blocked');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.reservedQuantity,3,'unordered reservation change must not overwrite the current balance');
  wmsReservations=[{...wmsReservations[0],warehouseCode:'SECONDARY',reservedQuantity:0,sourceVersion:2,sourceUpdatedAt:'2026-10-29T00:02:00Z'}];await runReservations('blocked');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.reservedQuantity,3,'wrong-warehouse reservation must not be applied');
  wmsReservations=[{...wmsReservations[0],warehouseCode:'MAIN',reservedQuantity:0,sourceVersion:2}];await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;
  assert.equal(stockProduct.reservationState,'Known');assert.equal(stockProduct.reservedQuantity,0);assert.equal(stockProduct.sellableStock,16,'a known zero reservation is different from missing data');
  wmsReservations=[];await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.reservedQuantity,0,'an omitted reservation row must preserve the prior balance');
  wmsReservations=[{...wmsReservations[0],barcode:item.barcode,warehouseCode:'MAIN',reservedQuantity:null,sourceVersion:3,sourceUpdatedAt:'2026-10-29T00:03:00Z'}];await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;
  assert.equal(stockProduct.reservationState,'Unknown');assert.equal(stockProduct.sellableStockState,'Unknown');assert.equal(stockProduct.sellableStock,null,'unknown reservation must not be exposed as zero sellable stock');
  wmsReservations=[{barcode:item.barcode,warehouseCode:'MAIN',reservedQuantity:4,sourceVersion:4,sourceUpdatedAt:'2026-10-29T00:04:00Z'}];await runReservations('completed');
  stockProduct=(await api('catalog/products?search='+item.sku)).body.items[0].product;assert.equal(stockProduct.sellableStock,12);
  const priorStockRun=(await api('sync/enterprise')).body.tasks.find(row=>row.task==='stock').lastRunAt;
  await api('sync/enterprise/run',{task:'stock'});
  await until(async()=>{const task=(await api('sync/enterprise')).body.tasks.find(row=>row.task==='stock');return task.lastRunAt!==priorStockRun&&task.state==='completed';});
  const publishedJobs=(await api('sync/enterprise')).body.jobs.filter(job=>job.kind==='stock'&&job.reference===stockProduct.id);
  assert(publishedJobs.some(job=>job.state==='completed'),'known reservation snapshots should allow sellable stock publication');
  assert.equal((await api('sync/enterprise/plan',erpPlan,'PUT')).status,200);
  await api('sync/enterprise/run',{task:'erp-orders'});
  await until(async()=>(await api('sync/enterprise')).body.tasks.find(t=>t.task==='erp-orders').state==='completed');
  await until(async()=>(await api('sync/enterprise/integration/events')).body.some(j=>j.integrationEvent.targetSystem==='erp'&&j.state==='blocked'));
  const blockedJob=(await api('sync/enterprise/integration/events')).body.find(j=>j.integrationEvent.targetSystem==='erp'&&j.state==='blocked');
  assert.equal(received.filter(r=>r.path==='/v1/orders').length,0,'ERP preparation must never fall back to legacy HTTP writes');
  await api('sync/enterprise/run',{task:'wms-orders'});await until(async()=>(await api('sync/enterprise')).body.tasks.find(t=>t.task==='wms-orders').state==='completed');
  await until(async()=>(await api('sync/enterprise/integration/events')).body.some(j=>j.integrationEvent.targetSystem==='wms'&&j.state==='blocked'));
  await stop();await start();await api('sync/enterprise/jobs/'+blockedJob.id+'/retry',{});
  await until(async()=>(await api('sync/enterprise/integration/events')).body.find(j=>j.id===blockedJob.id)?.state==='blocked');
  assert.equal(received.filter(r=>r.path==='/v1/orders').length,0,'missing ERP/WMS policies must remain fail-closed after restart');
  const statusText=JSON.stringify((await api('sync/enterprise')).body);assert(!statusText.includes('fixture-bridge-key')&&!statusText.includes(key),'credentials must never appear in dashboard status');
  await api('sync/enterprise/run',{task:'cargo-create'});await until(async()=>(await api('sync/enterprise')).body.tasks.find(t=>t.task==='cargo-create').state==='completed');assert.equal(received.filter(r=>r.path==='/v1/shipments').length,0,'cargo creation must wait for WMS barcode confirmation');
  const cargoOrder=(await api('orders')).body.find(o=>o.sourceMode==='demo'&&o.storeId===store.id);
  const transferred={externalOrderId:cargoOrder.id,storeId:store.id,accountSource:store.source,order:cargoOrder};
  dispatches=[{externalOrderId:transferred.externalOrderId,storeId:transferred.storeId,accountSource:transferred.accountSource,packageId:'WMS-PACK-1',status:'packed',lines:transferred.order.lines.map(l=>({barcode:l.barcode,quantity:l.quantity})),updatedAt:new Date().toISOString()}];
  await api('sync/enterprise/run',{task:'wms-dispatches'});await until(async()=>(await api('sync/enterprise')).body.dispatches.length===1);
  await api('sync/enterprise/run',{task:'cargo-create'});await until(async()=>received.some(r=>r.path==='/v1/shipments'));
  assert.equal(received.find(r=>r.path==='/v1/shipments').payload.package.packageId,'WMS-PACK-1');
  shipments=[{externalOrderId:transferred.externalOrderId,storeId:transferred.storeId,accountSource:transferred.accountSource,shipmentId:'CARGO-1',carrier:'Fixture carrier',trackingNumber:'TRACK-ERP-1',status:'shipped',labelUrl:'https://labels.example.test/label-1.pdf',updatedAt:new Date().toISOString()}];
  await api('sync/enterprise/run',{task:'shipments'});await until(async()=>(await api('sync/enterprise')).body.jobs.some(j=>j.kind==='marketplace-shipment'&&j.state==='completed'));
  assert(received.some(r=>r.path==='/v1/shipment-updates'&&r.payload.trackingNumber==='TRACK-ERP-1'));
  browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',r=>new URL(r.request().url()).origin===base?r.continue():r.abort());await page.goto(base+'/#sync');await page.waitForFunction(()=>epData);
  assert.equal(await page.getByRole('button',{name:'Demo Eşitle',exact:true}).count(),0);assert.equal(await page.getByRole('button',{name:'Verileri eşitle',exact:true}).count(),1);
  for(const v of ['dashboard','products','inventory','barcodes','pricing','sync']){await page.evaluate(v=>nav(v),v);assert.equal(await page.getByRole('button',{name:/Yeni [üÜ]rün/}).count(),0);}
  await page.evaluate(()=>nav('sync'));await page.waitForFunction(()=>epData);await page.locator('input[name=warehouseCode]').fill('UNSAVED-WAREHOUSE');await page.locator('input[name=warehouseCode]').blur();await page.waitForTimeout(3500);assert.equal(await page.locator('input[name=warehouseCode]').inputValue(),'UNSAVED-WAREHOUSE');
  await page.setViewportSize({width:390,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));assert.deepEqual(errors,[]);
  const admin=await api('access/setup',{name:'erp-admin',password:'ERP-admin-password-2026'});assert.equal(admin.status,200);const created=await api('access/users',{name:'erp-viewer',password:'ERP-viewer-password-2026',role:'viewer'},undefined,admin.cookie);assert.equal(created.status,200);const viewer=await api('access/login',{name:'erp-viewer',password:'ERP-viewer-password-2026'});assert.equal(viewer.status,200);
  assert.equal((await api('sync/enterprise/plan',plan,'PUT',viewer.cookie)).status,403);assert.equal((await api('sync/enterprise/run',{task:'all'},undefined,viewer.cookie)).status,403);
  assert.equal((await api('sync/enterprise/readiness',undefined,undefined,viewer.cookie)).status,403);
  console.log('PASS ERP hub: manual product creation removed, signed source import, source ownership, scoped stock/price/images, independent schedules, persistent acknowledgements, restart/uncertain-write protection, WMS cargo gate, credentials hidden, form/mobile and viewer guards. Mock bridge only; no live vendor API used.');
 }finally{if(browser)await browser.close();await stop();await new Promise(r=>bridge.close(r));const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-enterprise-'))throw Error('Unsafe cleanup');fs.rmSync(absolute,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1});
