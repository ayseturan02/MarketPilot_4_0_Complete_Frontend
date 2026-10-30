const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
(async()=>{
 const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-fulfillment-http-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));const base='http://127.0.0.1:'+port;
 const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Wms__Mode:'mock',Wms__AllowMockWrites:'true',Wms__Mock__PackageCount:'2',Enterprise__Nebim__Mode:'mock',Enterprise__Nebim__AllowMockWrites:'true'}});
 let browser;
 async function api(url,body,method='POST'){const r=await fetch(base+'/api/'+url,{method:body===undefined?'GET':method,headers:{'Content-Type':'application/json',Origin:base},body:body===undefined?undefined:JSON.stringify(body)});return{status:r.status,body:await r.json()};}
 try{
  let ready;for(let i=0;i<100;i++){try{ready=await api('wms');if(ready.status===200)break;}catch{}await new Promise(r=>setTimeout(r,100));}assert.equal(ready?.status,200);
  assert.equal((await api('erp/nebim/sync',{})).status,200);
  assert.equal((await api('wms/inventory/policy',{safetyStockByWarehouse:{},warehouseAssignmentsByAccount:{},channelStockLimits:{}},'PUT')).status,200);
  const order={marketplace:'trendyol',accountId:'seller-1',externalOrderId:'http-order',idempotencyKey:'http-order',status:'Created',orderedAt:'2026-01-15T10:00:00Z',currency:'TRY',lines:[{externalLineId:'line-1',sku:'TSH-EKR-S',quantity:35,unit:'EA',unitPrice:449,currency:'TRY'}]};
  let result=await api('wms/fulfillment/orders',order);assert.equal(result.status,200);const id=result.body.id;assert.equal(result.body.state,'blocked-policy');
  assert.equal((await api('wms/fulfillment/policy',{approved:true,writeSequence:['erp-order-registration','wms-fulfillment-submission'],warehousePriority:['mock-main','mock-outlet'],allowPartialShipments:false,updateTargets:['marketplace','erp']},'PUT')).status,200);
  const advance=()=>api('wms/fulfillment/workflows/'+id+'/advance',{});
  result=await advance();assert.equal(result.body.state,'validated');assert.equal(result.body.warehouses.length,2);
  const received=await Promise.all([api('wms/fulfillment/orders',order),api('wms/fulfillment/orders',order)]);assert(received.every(r=>r.body.id===id));
  assert.equal((await api('wms/fulfillment/orders',{...order,lines:[{...order.lines[0],quantity:34}]})).status,409);
  for(let i=0;i<15&&result.body.state!=='processed';i++)result=await advance();
  assert.equal(result.body.state,'processed');assert.equal(result.body.packages.length,4);assert.equal(result.body.publications.length,8);
  const history=result.body.history.map(e=>e.state);assert(history.indexOf('erp-registered')<history.indexOf('wms-accepted'));assert(history.includes('reserved')&&history.includes('picked')&&history.includes('packed'));
  assert.equal((await advance()).body.history.length,result.body.history.length);
  assert.equal((await api('wms/fulfillment/workflows')).body.length,1);
  browser=await chromium.launch({headless:true});const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base);await page.locator('.content').waitFor();await page.evaluate(()=>nav('wms-fulfillment'));
  await page.getByRole('button',{name:'Durumu aç',exact:true}).click();await page.getByRole('heading',{name:'Durum geçmişi',exact:true}).waitFor();assert((await page.locator('.content').innerText()).includes('http-order'));assert.deepEqual(errors,[]);
  console.log('PASS fulfillment HTTP/browser: unapproved policy blocks, ERP-first sequence, duplicate/concurrent receipt, conflict rejection, multi-depot/multi-package full workflow, reservations, tracking/publications, terminal replay and visible history. Mock only.');
 }finally{
  if(browser)await browser.close();const stopped=new Promise(r=>child.once('exit',r));child.kill();await stopped;
  const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-fulfillment-http-'));fs.rmSync(resolved,{recursive:true,force:true});
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
