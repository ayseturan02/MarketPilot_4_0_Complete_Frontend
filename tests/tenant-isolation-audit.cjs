// Business isolation audit: signed membership, independent data, workers and backups.
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net');
const {spawn}=require('node:child_process');
const {createHmac}=require('node:crypto'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-tenant-audit-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const base='http://127.0.0.1:'+port,env={...process.env};
 let browser;
 for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__|Enterprise__|Webhooks__|Deployment__|Businesses__)/i.test(key))delete env[key];
 Object.assign(env,{ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Critical',Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'});
 for(const business of ['business-a','business-b']){env[`Businesses__${business}__Enterprise__Erp__InboundKey`]='fixture-'+business;env[`Businesses__${business}__Enterprise__Erp__DataMode`]='demo';}
 let child;
 async function start(){child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});for(let i=0;i<150;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('Audit server did not start');}
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(r=>{if(running.exitCode!==null)return r();running.once('exit',r);});}
 async function api(route,body,cookie){const r=await fetch(base+'/api/'+route,{method:body===undefined?'GET':'POST',headers:{Origin:base,'Content-Type':'application/json',...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return {status:r.status,body:await r.json().catch(()=>null),cookie:r.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ')};}
 function requireStatus(value,status,label){if(value.status!==status)throw Error(label+': HTTP '+value.status);return value;}
 async function erp(business,sku,key='fixture-'+business){const body=JSON.stringify({requestId:'import-'+sku,items:[{sku,barcode:'BC-'+sku,name:sku,stock:50,price:449,currency:'TRY'}]}),stamp=String(Math.floor(Date.now()/1000));const r=await fetch(base+'/hooks/businesses/'+business+'/erp/catalog',{method:'POST',headers:{'Content-Type':'application/json','X-ERP-Timestamp':stamp,'X-ERP-Signature':createHmac('sha256',key).update(stamp+'.'+body).digest('hex')},body});return {status:r.status,body:await r.json().catch(()=>null)};}
 try{
  await start();
  const admin=requireStatus(await api('access/setup',{name:'audit-admin',password:'Isolated-audit-admin-2026'}),200,'setup');
  for(const name of ['business-a','business-b'])requireStatus(await api('access/users',{name,password:'Isolated-audit-password-2026',role:'operator',businessId:name},admin.cookie),200,'create user');
  const a=requireStatus(await api('access/login',{name:'business-a',password:'Isolated-audit-password-2026'}),200,'login A');
  const b=requireStatus(await api('access/login',{name:'business-b',password:'Isolated-audit-password-2026'}),200,'login B');
  requireStatus(await api('access/login',{name:'business-a',password:'wrong-password'}),401,'wrong password');
  requireStatus(await api('integrations/connections'),401,'anonymous access');
  const storeA=requireStatus(await api('integrations/connections',{name:'Same store name',marketplace:'trendyol',mode:'demo',environment:'stage'},a.cookie),200,'store A');
  const storeB=requireStatus(await api('integrations/connections',{name:'Same store name',marketplace:'trendyol',mode:'demo',environment:'stage'},b.cookie),200,'store B');
  const [aReadsB,bReadsA]=await Promise.all([api('integrations/connections/'+storeB.body.id+'/products',{},a.cookie),api('integrations/connections/'+storeA.body.id+'/products',{},b.cookie)]);
  const list=await api('integrations/connections',undefined,a.cookie);
  assert([403,404].includes(aReadsB.status)&&[403,404].includes(bReadsA.status));assert(!list.body.some(s=>s.id===storeB.body.id));
  requireStatus(await api('integrations/connections/'+storeA.body.id+'/products',{},a.cookie),200,'own products A');
  requireStatus(await api('integrations/connections/'+storeB.body.id+'/products',{},b.cookie),200,'own products B');
  requireStatus(await api('integrations/demo/connections/'+storeB.body.id+'/workspace',undefined,a.cookie),404,'foreign demo');
  requireStatus(await api('integrations/demo/connections/'+storeB.body.id+'/actions',{type:'stock',requestId:'foreign',barcode:'8691000000001',quantity:1,price:449},a.cookie),404,'foreign write');
  const spoof=await fetch(base+'/api/integrations/connections?businessId=business-b',{headers:{Cookie:a.cookie,'X-Business-Id':'business-b'}});assert(!(await spoof.json()).some(s=>s.id===storeB.body.id));
  const pA=requireStatus(await erp('business-a','ONLY-A'),200,'source A').body.products[0];const pB=requireStatus(await erp('business-b','ONLY-B'),200,'source B').body.products[0];
  requireStatus(await erp('business-b','WRONG-KEY','fixture-business-a'),401,'foreign source key');
  requireStatus(await api('catalog/products/'+pA.id,undefined,b.cookie),404,'foreign master A');requireStatus(await api('catalog/products/'+pB.id,undefined,a.cookie),404,'foreign master B');
  assert(!(await api('products',undefined,a.cookie)).body.some(p=>p.sku==='ONLY-B'));
  const csv=await(await fetch(base+'/api/export/products.csv',{headers:{Cookie:a.cookie}})).text();assert(csv.includes('ONLY-A')&&!csv.includes('ONLY-B'));
  await Promise.all([[a,storeA,17],[b,storeB,29]].map(async([user,store,quantity])=>requireStatus(await api('integrations/demo/connections/'+store.body.id+'/actions',{type:'stock',requestId:'same-request-id',barcode:'8691000000001',quantity,price:449},user.cookie),200,'own queued write')));
  let independent=false;for(let i=0;i<100;i++){const [wa,wb]=await Promise.all([api('integrations/demo/connections/'+storeA.body.id+'/workspace',undefined,a.cookie),api('integrations/demo/connections/'+storeB.body.id+'/workspace',undefined,b.cookie)]);if(wa.body.products[0].stock===17&&wb.body.products[0].stock===29){independent=true;break;}await sleep(100);}assert(independent,'independent workers');
  const bUser=requireStatus(await api('access/users',{name:'business-b-viewer',password:'Isolated-audit-password-2026',role:'viewer',businessId:'business-b'},admin.cookie),200,'B user');
  requireStatus(await api('access/users',{name:'business-a-admin',password:'Isolated-audit-password-2026',role:'admin',businessId:'business-a'},admin.cookie),200,'A admin');
  const adminA=requireStatus(await api('access/login',{name:'business-a-admin',password:'Isolated-audit-password-2026'}),200,'admin A login');
  requireStatus(await api('access/users',{name:'escape-user',password:'Isolated-audit-password-2026',role:'admin',businessId:'business-b'},adminA.cookie),403,'foreign user creation');
  requireStatus(await api('access/users/'+bUser.body.id,{role:'admin',enabled:true},adminA.cookie),403,'foreign role update');
  assert((await api('access/users',undefined,adminA.cookie)).body.every(u=>u.businessId==='business-a'));
  const {chromium}=require('./browser-test-runtime.cjs');browser=await chromium.launch({headless:true});
  const context=await browser.newContext({viewport:{width:1440,height:1000}}),errors=[];
  const split=a.cookie.indexOf('=');await context.addCookies([{name:a.cookie.slice(0,split),value:a.cookie.slice(split+1),url:base}]);
  await context.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
  await page.goto(base+'/#products');await page.waitForFunction(()=>chState().result?.items.some(r=>r.product.sku==='ONLY-A'));
  assert.equal(await page.evaluate(()=>localAccess.businessId),'business-a');
  assert(!(await page.locator('.content').innerText()).includes('ONLY-B'));
  await page.evaluate(()=>nav('sync'));await page.waitForFunction(()=>epData);assert(!(await page.evaluate(()=>epData.stores)).some(s=>s.id===storeB.body.id));
  await page.setViewportSize({width:390,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
  const rootContext=await browser.newContext(),at=admin.cookie.indexOf('=');await rootContext.addCookies([{name:admin.cookie.slice(0,at),value:admin.cookie.slice(at+1),url:base}]);
  const rootPage=await rootContext.newPage();await rootPage.goto(base+'/#users');await rootPage.waitForFunction(()=>localAccess.canManageBusinesses);await rootPage.getByRole('button',{name:'Kullanıcı ekle',exact:true}).click();assert.equal(await rootPage.locator('input[name=businessId]').inputValue(),'default');
  assert.deepEqual(errors,[]);await browser.close();browser=null;
  const backup=requireStatus(await api('system/backups',{},adminA.cookie),200,'tenant backup').body;
  assert(fs.existsSync(path.join(temp,'Data/businesses/business-a/Data/Backups',backup.name)));assert(!fs.existsSync(path.join(temp,'Data/businesses/business-b/Data/Backups',backup.name)));
  await stop();await start();
  requireStatus(await api('integrations/connections/'+storeB.body.id+'/products',{},a.cookie),404,'foreign after restart');
  assert.equal((await api('integrations/demo/connections/'+storeA.body.id+'/workspace',undefined,a.cookie)).body.products[0].stock,17);
  assert.equal((await api('integrations/demo/connections/'+storeB.body.id+'/workspace',undefined,b.cookie)).body.products[0].stock,29);
  console.log(JSON.stringify({login:{correctPassword:200,wrongPassword:401,anonymousAccess:401},businessIsolation:{supported:true,expected:[403,404],userAReadsStoreB:aReadsB.status,userBReadsStoreA:bReadsA.status,userASeesStoreB:false,ownAccess:true,concurrentWorkers:true,independentBackups:true,restartPersistence:true,sourceKeysIsolated:true,aggregateAndExportIsolation:true,meetsRequirement:true}}));
 }finally{
  if(browser)await browser.close();await stop();
  const target=path.resolve(temp);if(!target.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(target).startsWith('marketpilot-tenant-audit-'))throw Error('Unsafe audit cleanup');fs.rmSync(target,{recursive:true,force:true});
 }
})().catch(error=>{console.error(error);process.exitCode=1;});
