const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms)),dll=process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll');
(async()=>{
 const probe=net.createServer();await new Promise((resolve,reject)=>probe.once('error',reject).listen(0,'127.0.0.1',resolve));
 const port=probe.address().port;await new Promise(r=>probe.close(r));
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-sync-http-')),base='http://127.0.0.1:'+port;
 let child,browser;
 async function start(){
  child=spawn('dotnet',[dll,'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Marketplaces__StockSource__BaseUrl:'',Marketplaces__StockSource__AllowWrites:'false'}});
  for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('Server did not start');
 }
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(resolve=>{if(running.exitCode!==null)return resolve();running.once('exit',resolve);setTimeout(resolve,4000);});}
 async function api(url,body,method,cookie){
  const response=await fetch(base+url,{method:method||(body===undefined?'GET':'POST'),headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
  return {status:response.status,body:await response.json(),cookie:(response.headers.get('set-cookie')||'').split(';')[0]};
 }
 try{
  await start();
  const products=(await api('/api/products')).body,legacy=products.find(p=>p.id==='p001');
  let result=await api('/api/products/'+legacy.id,{stock:legacy.stock+20,price:-1},'PUT');
  assert.equal(result.status,400);
  assert.equal((await api('/api/products')).body.find(p=>p.id===legacy.id).stock,legacy.stock,'invalid request must not partially update stock');
  assert.equal((await api('/api/products',{name:'Duplicate',sku:' '+legacy.sku+' ',price:10,stock:1})).status,405);
  const settings=(await api('/api/settings')).body;
  assert.equal((await api('/api/settings',{...settings,lowStockThreshold:-1},'PUT')).status,400);
  assert.deepEqual((await api('/api/settings')).body,settings);

  browser=await chromium.launch({channel:'chrome',headless:true});
  let context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage();
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(base+'/#sync');
  await page.locator('[data-enterprise-sync]').waitFor();
  assert.equal(await page.getByRole('button',{name:'Verileri e\u015fitle',exact:true}).count(),1);
  // Legacy demo stock behavior remains server-side; the central UI has its own enterprise contract test.
  assert.equal((await api('/api/sync/demo',{})).status,200);
  for(let i=0;i<150;i++){const status=(await api('/api/sync/status')).body;if(status.stock.lastRunStatus==='completed'&&status.demo.pending===0&&status.outbox.queued===0&&status.outbox.waiting===0)break;await sleep(100);}
  assert(fs.existsSync(path.join(temp,'Data/central-stock.json')));
  const saleResult=await api('/api/integrations/central-stock/demo-sale',{connectionId:'trendyol',barcode:'8691000000005',quantity:2});
  assert.equal(saleResult.status,200);const sale=saleResult.body;
  assert.equal(sale.view.items.find(i=>i.sku==='PNT-BEJ-38').quantity,7);
  await context.close();
  // No browser and no read request: only the server worker can finish these jobs.
  for(let i=0;i<150;i++){const jobs=JSON.parse(fs.readFileSync(path.join(temp,'Data/marketplace-outbox.json'),'utf8'));if(jobs.every(j=>!['queued','dispatching','waiting','uncertain'].includes(j.state)))break;await sleep(100);}

  const registry=JSON.parse(fs.readFileSync(path.join(temp,'Data/integration-connections.json'),'utf8'));
  for(const store of registry.filter(s=>s.settings.mode==='demo'&&!['amazon','n11'].includes(s.settings.marketplace))){
   const key=[store.settings.marketplace,store.settings.environment,store.settings.name].join('|');
   const data=JSON.parse(fs.readFileSync(path.join(temp,'Data/integration-demo.json'),'utf8'))[key];
   assert.equal(data.products.find(p=>p.sku==='PNT-BEJ-38').stock,7,'worker must update '+store.id+' without a browser');
  }
  await stop();await start();
  assert.equal((await api('/api/integrations/central-stock')).body.items.find(i=>i.sku==='PNT-BEJ-38').quantity,7,'stock survives restart');
  context=await browser.newContext({viewport:{width:1440,height:1000}});page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
  await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(base+'/#hepsiburada');
  await page.waitForFunction(()=>typeof hbTabSelect==='function'&&data['integrations/connections']?.length);
  await page.evaluate(()=>hbTabSelect('products'));
  await page.waitForFunction(()=>hbList(hbCurrent(),'products').result?.items.some(p=>p.sku==='PNT-BEJ-38'&&p.stock===7));
  result=await api('/api/integrations/demo/connections/hepsiburada/actions',{type:'stock',requestId:'sync-http-background',barcode:'8691000000005',quantity:63,price:1099});
  assert.equal(result.status,200);
  await page.waitForFunction(()=>hbList(hbCurrent(),'products').result?.items.some(p=>p.sku==='PNT-BEJ-38'&&p.stock===63),{},{timeout:15000});
  assert((await page.locator('.content').innerText()).includes('63'),'updated backend stock must appear on the open page without refresh');
  assert.equal(await page.evaluate(()=>csvCell('=1+1')), '"\'=1+1"');
  assert.equal(await page.evaluate(()=>csvCell('  +1')), '"\'  +1"');

  const admin=await api('/api/access/setup',{name:'sync-admin',password:'Fixture-Only-Admin-2026'});
  assert.equal(admin.status,200);
  await api('/api/access/users',{name:'sync-viewer',password:'Fixture-Only-Viewer-2026',role:'viewer'},undefined,admin.cookie);
  async function useAccount(cookie){
   await context.clearCookies();const at=cookie.indexOf('=');await context.addCookies([{name:cookie.slice(0,at),value:cookie.slice(at+1),url:base,httpOnly:true,sameSite:'Strict'}]);
   await page.goto(base+'/#invoice_drafts');await page.reload();await page.waitForFunction(()=>localAccess.authenticated);
  }
  await useAccount(admin.cookie);
  await page.evaluate(()=>{mpDrafts.invoice_drafts=[{values:['PRIVATE_ADMIN_DRAFT'],created:new Date().toISOString(),tab:'Taslaklar'}];mpSave();});
  const viewer=await api('/api/access/login',{name:'sync-viewer',password:'Fixture-Only-Viewer-2026'});
  assert.equal(viewer.status,200);
  await useAccount(viewer.cookie);
  assert(!(await page.evaluate(()=>JSON.stringify(mpDrafts))).includes('PRIVATE_ADMIN_DRAFT'),'another login must not inherit drafts');
  assert.equal((await api('/api/sync/demo',{},undefined,viewer.cookie)).status,403,'viewer cannot start sync');
  await useAccount(admin.cookie);
  assert((await page.evaluate(()=>JSON.stringify(mpDrafts))).includes('PRIVATE_ADMIN_DRAFT'),'owner keeps their drafts');
  await page.setViewportSize({width:390,height:844});await page.evaluate(()=>nav('sync'));await page.locator('[data-enterprise-sync]').waitFor();
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'sync screen must fit mobile');
  assert.deepEqual(errors,[],'no browser JavaScript errors');
  console.log('PASS synchronization HTTP/browser: central sync UI, legacy demo API, persisted queue, browser-closed processing, restart recovery, open-page stock refresh, atomic validation, CSV protection, per-account drafts, viewer guards and mobile. No live APIs.');
 }finally{
  if(browser)await browser.close();await stop();
  const resolved=path.resolve(temp),prefix=path.resolve(os.tmpdir())+path.sep;
  if(!resolved.startsWith(prefix)||!path.basename(resolved).startsWith('marketpilot-sync-http-'))throw Error('Unsafe cleanup path');
  fs.rmSync(resolved,{recursive:true,force:true});
 }
})().catch(error=>{console.error(error);process.exitCode=1;});
