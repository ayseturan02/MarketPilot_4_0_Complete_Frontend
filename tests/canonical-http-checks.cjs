const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),net=require('node:net'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{randomUUID}=require('node:crypto');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-canonical-http-'));let child;
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+probe.address().port;await new Promise(r=>probe.close(r));
 const env={...process.env,Deployment__Mode:'local',ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Logging__EventLog__LogLevel__Default:'None'};
 for(const k of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__|Enterprise__|Businesses__|Webhooks__|Workspace__)/i.test(k))delete env[k];
 async function start(){child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('startup');}
 async function stop(){if(!child)return;const process=child;child=null;process.kill();await new Promise(r=>{if(process.exitCode!==null)return r();process.once('exit',r);});}
 async function api(route,body,cookie){const response=await fetch(base+'/api/'+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return{status:response.status,body:await response.json().catch(()=>null),cookie:response.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ')};}
 const check=(r,status=200)=>{assert.equal(r.status,status,JSON.stringify(r.body));return r.body;};
 const stamp=(sourceRecordId,tenantId='default')=>({tenantId,sourceSystem:'nebim-v3',sourceRecordId,externalAccountId:null,correlationId:randomUUID(),idempotencyKey:randomUUID(),sourceVersion:1,occurredAt:'2026-01-01T00:00:00Z',receivedAt:'2026-01-01T00:00:01Z'});
 const product=(tenantId='default')=>({id:'shared-id',context:stamp('native-product',tenantId),name:'Ekru tişört',category:'Giyim / Üst Giyim',brand:'Tekstil',status:'active'});
 try{
  await start();assert.equal(check(await api('contracts/schema')).resources.length,15);
  const p=product();assert.equal(check(await api('contracts/products',p)).state,'applied');assert.equal(check(await api('contracts/products',p)).state,'duplicate');
  check(await api('contracts/products',{...p,name:'Changed same request'}),409);
  const missing={...p,id:'review',context:stamp('')};const queued=check(await api('contracts/products',missing));assert.equal(queued.state,'manual-mapping');assert.equal(check(await api('contracts/manual-mappings')).length,1);
  assert.equal(check(await api('contracts/products')).totalElements,1);
  check(await api('contracts/products',{id:'bad',context:null}),400);
  check(await api('contracts/products',{...p,id:'foreign',context:stamp('foreign','tenant-b')}),403);
  const admin=await api('access/setup',{name:'contract-admin',password:'Contract-tests-password-2026'});check(admin);check(await api('contracts/products'),401);
  check(await api('access/users',{name:'contract-viewer',password:'Contract-viewer-password-2026',role:'viewer'},admin.cookie));const viewer=await api('access/login',{name:'contract-viewer',password:'Contract-viewer-password-2026'});check(viewer);
  check(await api('contracts/products',undefined,viewer.cookie));check(await api('contracts/products',p,viewer.cookie),403);check(await api('contracts/manual-mappings/'+queued.queueId+'/resolve',{variantId:'anything'},viewer.cookie),403);
  check(await api('access/users',{name:'contract-other',password:'Contract-other-password-2026',role:'admin',businessId:'contract-tenant'},admin.cookie));const other=await api('access/login',{name:'contract-other',password:'Contract-other-password-2026'});check(other);
  assert.equal(check(await api('contracts/products',undefined,other.cookie)).totalElements,0);
  assert.equal(check(await api('contracts/products',product('contract-tenant'),other.cookie)).state,'applied');
  check(await api('contracts/products',p,other.cookie),403);assert.equal(check(await api('contracts/products',undefined,admin.cookie)).totalElements,1);
  assert.equal(check(await api('contracts/manual-mappings',undefined,other.cookie)).length,0);
  await stop();await start();const logged=await api('access/login',{name:'contract-admin',password:'Contract-tests-password-2026'});check(logged);assert.equal(check(await api('contracts/products',p,logged.cookie)).state,'duplicate');assert.equal(check(await api('contracts/manual-mappings',undefined,logged.cookie)).length,1);
  assert(fs.existsSync(path.join(temp,'Data/demo-store.json')));assert(fs.existsSync(path.join(temp,'Data/businesses/contract-tenant/Data/demo-store.json')));
  console.log('PASS canonical HTTP: schema, durable JSON replay/manual queues, invalid inputs, admin/viewer/auth guards, same record IDs in isolated tenants, source scope and restart. No database or remote API.');
 }finally{await stop();const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-canonical-http-'))throw Error('Unsafe cleanup');fs.rmSync(absolute,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
