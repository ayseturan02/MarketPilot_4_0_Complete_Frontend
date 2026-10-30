const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const project=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const probe=net.createServer();await new Promise((resolve,reject)=>probe.once('error',reject).listen(0,'127.0.0.1',resolve));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-access-http-')),base='http://127.0.0.1:'+port;
 const child=spawn('dotnet',[path.join(project,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(project,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Logging__EventLog__LogLevel__Default:'None'}});
 const api=(url,body,cookie,origin=base)=>fetch(base+url,{method:body===undefined?'GET':'POST',headers:{...(body===undefined?{}:{'Content-Type':'application/json',Origin:origin}),...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
 const cookie=r=>(r.headers.get('set-cookie')||'').split(';')[0];
 try{
  let ready=false;for(let i=0;i<80;i++){if(child.exitCode!==null)throw Error('Isolated server exited');try{if((await api('/api/health')).ok){ready=true;break;}}catch{}await sleep(100);}assert(ready);
  await api('/api/integrations/demo/connections/trendyol/workspace');
  let r=await api('/api/access/setup',{name:'test-admin',password:'Test-Only-Password-2026'});assert.equal(r.status,200);const admin=cookie(r);
  assert.equal((await api('/api/products')).status,401);
  r=await api('/api/access/users',{name:'test-viewer',password:'Viewer-Only-Password-2026',role:'viewer'},admin);assert.equal(r.status,200);const viewer=await r.json();
  r=await api('/api/access/login',{name:'test-viewer',password:'Viewer-Only-Password-2026'});const viewerCookie=cookie(r);
  assert.equal((await api('/api/products/search?size=25',undefined,viewerCookie)).status,200);
  assert.equal((await api('/api/integrations/connections/trendyol/products?page=0&size=1',{},viewerCookie)).status,200);
  assert.equal((await api('/api/integrations/demo/connections/trendyol/actions',{type:'stock',requestId:'denied',barcode:'8691000000001',quantity:44,price:499},viewerCookie)).status,403);
  assert.equal((await api('/api/access/users',undefined,viewerCookie)).status,403);
  assert.equal((await api('/api/access/users',{name:'blocked',password:'Test-Only-Password-2026',role:'viewer'},admin,'https://other.invalid')).status,403);
  assert.equal((await api('/api/access/users/'+viewer.id,{role:'operator',enabled:true},admin)).status,200);
  assert.equal((await api('/api/products',undefined,viewerCookie)).status,401);
  r=await api('/api/access/login',{name:'test-viewer',password:'Viewer-Only-Password-2026'});const operator=cookie(r);
  const action={type:'stock',requestId:'worker-http',barcode:'8691000000001',quantity:51,price:449};
  assert.equal((await api('/api/integrations/demo/connections/trendyol/actions',action,operator)).status,403);
  assert.equal((await api('/api/integrations/demo/connections/trendyol/actions',action,admin)).status,200);
  assert.equal((await api('/api/integrations/demo/connections/trendyol/actions',action,admin)).status,200);
  assert.equal((await api('/api/integrations/demo/connections/trendyol/actions',{...action,quantity:52},admin)).status,400);
  await sleep(3400);
  const state=Object.values(JSON.parse(fs.readFileSync(path.join(temp,'Data/integration-demo.json'),'utf8')))[0];assert.equal(state.products[0].stock,51);assert.equal(state.jobs.find(j=>j.requestId==='worker-http').state,'completed');
  r=await api('/api/system/backups',{},admin);assert.equal(r.status,200);const backup=await r.json();assert.equal((await api('/api/system/backups/verify',{name:backup.name},admin)).status,200);assert.equal((await api('/api/system/backups',{},operator)).status,403);
  r=await api('/api/access/password',{currentPassword:'Viewer-Only-Password-2026',newPassword:'Changed-Only-Password-2026'},operator);assert.equal(r.status,200);const renewed=cookie(r);assert.equal((await api('/api/products',undefined,operator)).status,401);assert.equal((await api('/api/products',undefined,renewed)).status,200);
  console.log('PASS isolated HTTP: authentication/roles, CSRF rejection, session revocation, queue without browser/read requests, idempotency and admin encrypted backups. Main data untouched.');
 }finally{
  child.kill();await new Promise(resolve=>{if(child.exitCode!==null)return resolve();child.once('exit',resolve);setTimeout(resolve,5000);});
  const absolute=path.resolve(temp),prefix=path.resolve(os.tmpdir())+path.sep;if(!absolute.startsWith(prefix)||!path.basename(absolute).startsWith('marketpilot-access-http-'))throw Error('Unsafe cleanup path');fs.rmSync(absolute,{recursive:true,force:true});
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
