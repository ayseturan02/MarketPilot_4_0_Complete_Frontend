const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),net=require('node:net'),assert=require('node:assert/strict');
const{spawn}=require('node:child_process'),{chromium}=require('./browser-test-runtime.cjs');const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-pricing-http-'));let child,browser;
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+probe.address().port;await new Promise(r=>probe.close(r));
 const env={...process.env,Deployment__Mode:'local',ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Logging__EventLog__LogLevel__Default:'None'};for(const k of Object.keys(env))if(/^(Enterprise__|Marketplaces__|MarketplaceAccounts__|Businesses__|Webhooks__|Workspace__)/i.test(k))delete env[k];
 async function start(){child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('startup');}
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(r=>{if(running.exitCode!==null)return r();running.once('exit',r);});}
 async function api(route,body,method,cookie){const response=await fetch(base+'/api/'+route,{method:method||(body===undefined?'GET':'POST'),headers:{Origin:base,'Content-Type':'application/json',...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return{status:response.status,body:await response.json().catch(()=>null),cookie:response.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ')};}
 const ok=(r,status=200)=>{assert.equal(r.status,status,JSON.stringify(r.body));return r.body;};
 try{
  await start();
  const preview=ok(await api('catalog/erp/preview',{}));ok(await api('catalog/erp/apply',{token:preview.token}));
  const first=ok(await api('catalog/products?search=8691000000001')).items[0];
  ok(await api('pricing/source/refresh',{}));const source=ok(await api('pricing'));const list=source.lists[0];
  ok(await api('pricing/policy',{masterPriceListId:list.id},'PUT'));
  const listings=ok(await api('catalog/products/'+first.product.id)).listings.items;
  for(const l of listings){
   ok(await api('pricing/rules',{storeId:l.storeId,priceListId:list.id,currency:'TRY'},'PUT'));
   const p=ok(await api('pricing/preview',{productId:l.productId,storeId:l.storeId,listingKey:l.key}));assert.equal(p.state,'pending-approval');assert.equal(p.newPrice,449);
   ok(await api('pricing/proposals/'+p.id+'/publish',{}),409);ok(await api('pricing/proposals/'+p.id+'/approve',{}));const receipt=ok(await api('pricing/proposals/'+p.id+'/publish',{}));assert.equal(ok(await api('pricing/proposals/'+p.id+'/publish',{})).receiptId,receipt.receiptId);
  }
  browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];page.on('pageerror',e=>{errors.push(e.message);console.error(e.message)});await page.route('**/*',r=>new URL(r.request().url()).origin===base?r.continue():r.abort());
  await page.goto(base+'/#pricing');await page.getByRole('heading',{name:'ERP Fiyat Yönetimi',exact:true}).waitFor();await page.locator('select[name=listing]').waitFor();
  await page.evaluate(()=>{const i=pxListings.findIndex(r=>r.listing.barcode==='8691000000001');document.querySelector('select[name=listing]').value=String(i)});
  await page.getByRole('button',{name:'Fiyat farkını önizle',exact:true}).click();await page.getByRole('button',{name:'Onayla',exact:true}).first().waitFor();await page.getByRole('button',{name:'Onayla',exact:true}).first().click();await page.getByRole('button',{name:'Mock ilana uygula',exact:true}).first().waitFor();await page.getByRole('button',{name:'Mock ilana uygula',exact:true}).first().click();
  await page.setViewportSize({width:390,height:844});assert.deepEqual(errors,[]);await browser.close();browser=null;
  const admin=await api('access/setup',{name:'pricing-admin',password:'Pricing-tests-password-2026'});ok(admin);ok(await api('access/users',{name:'pricing-viewer',password:'Pricing-viewer-password-2026',role:'viewer'},undefined,admin.cookie));const viewer=await api('access/login',{name:'pricing-viewer',password:'Pricing-viewer-password-2026'});ok(viewer);
  ok(await api('pricing/source/refresh',{},undefined,viewer.cookie),403);ok(await api('pricing/policy',{masterPriceListId:list.id},'PUT',viewer.cookie),403);
  await stop();await start();const login=await api('access/login',{name:'pricing-admin',password:'Pricing-tests-password-2026'});assert(ok(await api('pricing',undefined,undefined,login.cookie)).proposals.some(p=>p.state==='published'));
  console.log('PASS ERP pricing HTTP/browser: six mock marketplaces, protected approval and publication, idempotency, pricing screen actions, viewer guards and restart.');
 }finally{if(browser)await browser.close();await stop();const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-pricing-http-'))throw Error('Unsafe cleanup');fs.rmSync(absolute,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
