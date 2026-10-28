const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-auto-http-')),base='http://127.0.0.1:'+port;
 let child,browser;
 async function start(){child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Marketplaces__StockSource__BaseUrl:'',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'}});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('startup');}
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(r=>{if(running.exitCode!==null)return r();running.once('exit',r);setTimeout(r,4000);});}
 async function api(url,body,cookie,origin=base){const r=await fetch(base+url,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',Origin:origin,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return{status:r.status,body:await r.json(),cookie:(r.headers.get('set-cookie')||'').split(';')[0]};}
 const read=name=>JSON.parse(fs.readFileSync(path.join(temp,'Data',name),'utf8'));
 try{
  await start();
  assert.equal((await api('/api/sync/automation',{enabled:true,intervalSeconds:5,stockEnabled:true,lookbackDays:2})).status,400);
  assert.equal((await api('/api/sync/automation')).body.automation.settings.enabled,false);
  assert.equal((await api('/api/sync/automation',{enabled:true,intervalSeconds:30,stockEnabled:true,lookbackDays:2},undefined,'https://other.invalid')).status,403);
  await api('/api/integrations/connections',{name:'Unconnected real fixture',marketplace:'trendyol',mode:'real',environment:'production'});
  browser=await chromium.launch({channel:'chrome',headless:true});
  const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(base+'/#sync',{waitUntil:'domcontentloaded'});
  await page.locator('[data-automation] form').waitFor();
  await page.locator('[data-automation] input[name=enabled]').check();
  await page.locator('[data-automation] input[name=intervalSeconds]').fill('30');
  const saved=page.waitForResponse(r=>r.url().endsWith('/api/sync/automation')&&r.request().method()==='POST');
  await page.locator('[data-automation]').getByRole('button',{name:'Otomasyonu kaydet',exact:true}).click();
  assert.equal((await saved).status(),200);
  await page.waitForFunction(()=>auData?.automation.stores.filter(s=>s.state==='completed').length>=7&&auData.outbox.queued===0&&auData.outbox.waiting===0,{},{timeout:30000});
  const first=read('synchronization.json'),registry=read('integration-connections.json');
  assert(first.settings.enabled&&first.settings.stockEnabled);
  assert.equal(Object.values(first.stores).length,7,'unconnected real store must not be polled');
  await page.setViewportSize({width:390,height:844});
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'automation panel must fit mobile');
  assert.deepEqual(errors,[]);
  await browser.close();browser=null;
  await stop();
  const all=read('integration-demo.json'),store=registry.find(s=>s.id==='trendyol'),key=[store.settings.marketplace,store.settings.environment,store.settings.name].join('|'),workspace=all[key];
  const product=workspace.products.find(p=>p.sku==='PNT-BEJ-38');
  const order=JSON.parse(JSON.stringify(workspace.orders[0]));
  order.packageId='auto-timer-order';order.orderNumber='AUTO-TIMER';order.status='Created';order.orderDate=new Date().toISOString();order.lastModifiedDate=Date.now();
  order.lines=[{...order.lines[0],id:'auto-timer-line',sku:product.sku,barcode:product.barcode,name:product.name,quantity:2}];
  order.cargo.trackingNumber='';product.stock-=2;workspace.orders.unshift(order);
  fs.writeFileSync(path.join(temp,'Data/integration-demo.json'),JSON.stringify(all));
  const previousSuccess=first.stores['demo|'+store.settings.marketplace+'|'+store.settings.environment+'|'+store.settings.name].lastSuccessAt;
  await start();
  // No browser or sync/stock HTTP calls after startup: only periodic server workers can perform the next run.
  let completed=false;
  for(let i=0;i<500;i++){
   try{
    const source=read('central-stock.json'),jobs=read('marketplace-outbox.json'),schedules=read('synchronization.json');
    const item=Object.values(source.items).find(i=>i.sku==='PNT-BEJ-38');
    const later=schedules.stores['demo|'+store.settings.marketplace+'|'+store.settings.environment+'|'+store.settings.name]?.lastSuccessAt;
    if(item?.quantity===7&&later>previousSuccess&&!jobs.some(j=>['queued','dispatching','waiting','uncertain'].includes(j.state))){
     const workspaces=read('integration-demo.json');
     const followers=registry.filter(s=>s.settings.mode==='demo'&&!['amazon','n11'].includes(s.settings.marketplace));
     if(followers.every(s=>workspaces[[s.settings.marketplace,s.settings.environment,s.settings.name].join('|')].products.find(p=>p.sku==='PNT-BEJ-38').stock===7)){completed=true;break;}
    }
   }catch{}
   await sleep(100);
  }
  if(!completed)console.error(JSON.stringify({source:read('central-stock.json'),schedules:read('synchronization.json'),jobs:read('marketplace-outbox.json').filter(j=>j.state!=='completed'),products:Object.fromEntries(Object.entries(read('integration-demo.json')).map(([k,w])=>[k,w.products.find(p=>p.sku==='PNT-BEJ-38')?.stock]))},null,2));
  assert(completed,'persisted schedule must run after restart and distribute a new sale without a browser/manual call');
  assert.equal(read('central-stock.json').sales.filter(s=>s.orderNumber==='AUTO-TIMER'&&s.quantity===2).length,1,'automatic sale is not duplicated');
  assert(read('marketplace-outbox.json').every(j=>j.mode==='demo'),'no real job was dispatched');
  const admin=await api('/api/access/setup',{name:'auto-admin',password:'Fixture-Admin-Password-2026'});
  await api('/api/access/users',{name:'auto-viewer',password:'Fixture-Viewer-Password-2026',role:'viewer'},admin.cookie);
  const viewer=await api('/api/access/login',{name:'auto-viewer',password:'Fixture-Viewer-Password-2026'});
  assert.equal((await api('/api/sync/automation',{enabled:false,intervalSeconds:30,stockEnabled:false,lookbackDays:2},viewer.cookie)).status,403);
  assert.equal((await api('/api/sync/automation',{enabled:false,intervalSeconds:30,stockEnabled:false,lookbackDays:2},admin.cookie)).status,200);
  assert.equal(read('synchronization.json').settings.enabled,false);
  console.log('PASS automation HTTP/browser: enable from UI, persisted schedule, genuine timer-driven polling and stock distribution after restart with browser closed, durable outbox completion, no duplicate sales, real-store exclusion, validation, roles and mobile.');
 }finally{
  if(browser)await browser.close();await stop();
  const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-auto-http-'))throw Error('Unsafe cleanup');
  fs.rmSync(absolute,{recursive:true,force:true});
 }
})().catch(error=>{console.error(error);process.exitCode=1;});
