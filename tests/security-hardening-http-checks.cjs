const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process'),{createHmac}=require('node:crypto');
(async()=>{
 const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-security-http-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));const base='http://127.0.0.1:'+port;
 const secret='mock-secret-never-render-or-log-2026',key='mock-hmac-inbound-key-at-least-32-characters';let browser;
 const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Enterprise__Erp__InboundKey:key,MarketplaceAccounts__fixture__Marketplace:'trendyol',MarketplaceAccounts__fixture__Environment:'production',MarketplaceAccounts__fixture__Credentials__ApiKey:secret,MarketplaceAccounts__fixture__Credentials__ApiSecret:secret,MarketplaceAccounts__fixture__Credentials__SellerId:'12345'}});
 async function request(route,body,cookie,extra={}){const response=await fetch(base+route,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{}),...extra},body:body===undefined?undefined:JSON.stringify(body)});const text=await response.text();return{response,text,cookie:(response.headers.get('set-cookie')||'').split(';')[0]};}
 function headers(r){assert.equal(r.response.headers.get('x-content-type-options'),'nosniff');assert.equal(r.response.headers.get('x-frame-options'),'DENY');assert.equal(r.response.headers.get('referrer-policy'),'no-referrer');assert.equal(r.response.headers.get('cross-origin-resource-policy'),'same-origin');assert(r.response.headers.get('content-security-policy').includes("connect-src 'self'"));assert(!r.response.headers.get('access-control-allow-origin'));assert(!r.response.headers.get('server'));}
 try{
  let ready;for(let i=0;i<100;i++){try{ready=await request('/api/health');if(ready.response.ok)break;}catch{}await new Promise(r=>setTimeout(r,100));}assert.equal(ready?.response.status,200);headers(ready);
  const setup=await request('/api/access/setup',{name:'security-admin',password:'Mock-Admin-Password-2026'});assert.equal(setup.response.status,200);assert(setup.response.headers.get('set-cookie').includes('httponly'));assert(/samesite=strict/i.test(setup.response.headers.get('set-cookie')));
  const cookie=setup.cookie;
  for(const route of ['/api/integrations/credential-profiles','/api/integrations/readiness','/api/access/users','/api/system/events']){const r=await request(route,undefined,cookie);assert.equal(r.response.status,200);assert(!r.text.includes(secret)&&!r.text.includes(key));headers(r);assert.equal(r.response.headers.get('cache-control'),'no-store');}
  assert.equal((await request('/api/settings',{businessName:'denied'},cookie,{Origin:'https://foreign.invalid'})).response.status,403);
  assert.equal((await request('/api/settings',{businessName:'denied'},cookie,{'Sec-Fetch-Site':'cross-site'})).response.status,403);
  const preflight=await fetch(base+'/api/settings',{method:'OPTIONS',headers:{Origin:'https://foreign.invalid','Access-Control-Request-Method':'PUT'}});assert(!preflight.headers.get('access-control-allow-origin'));
  for(const route of ['/Data/local-accounts.json','/Data/keys','/../Data/local-accounts.json','/appsettings.json','/.git/config']){const r=await request(route,undefined,cookie);assert(!r.text.includes('PasswordHash')&&!r.text.includes('passwordHash')&&!r.text.includes(secret)&&!r.text.includes('Mock-Admin-Password-2026'),'Private file exposed: '+route);}
  // A well-behaved HTTP client asks before uploading an oversized body.
  const tooLarge=await new Promise((resolve,reject)=>{const req=require('node:http').request(base+'/api/settings',{method:'POST',headers:{Origin:base,Cookie:cookie,'Content-Type':'application/json','Content-Length':9*1024*1024,Expect:'100-continue'}},res=>{let text='';res.on('data',chunk=>text+=chunk);res.on('end',()=>resolve({response:new Response(text,{status:res.statusCode,headers:res.headers})}));});req.on('error',reject);req.on('continue',()=>{req.destroy();reject(Error('Oversized request body was invited'));});req.flushHeaders();});
  assert.equal(tooLarge.response.status,413);headers(tooLarge);
  const blockedFile=path.join(temp,'Data','credential-references.json.tmp');fs.mkdirSync(blockedFile);
  const failed=await fetch(base+'/api/security/connections/credentials',{method:'PUT',headers:{'Content-Type':'application/json',Origin:base,Cookie:cookie},body:JSON.stringify({id:'fixture-ref',tenantId:'default',system:'erp',provider:'mock',keyName:'MOCK_FIXTURE'})});
  const text=await failed.text();assert.equal(failed.status,500);assert(!text.includes('Exception')&&!text.includes(temp));headers({response:failed});fs.rmdirSync(blockedFile);
  async function hook(stamp,signature,body){return fetch(base+'/hooks/erp/catalog',{method:'POST',headers:{'Content-Type':'application/json','X-ERP-Timestamp':String(stamp),'X-ERP-Signature':signature},body});}
  const body=JSON.stringify({requestId:'security-fixture',items:[]}),stamp=Math.floor(Date.now()/1000),signature=createHmac('sha256',key).update(stamp+'.'+body).digest('hex');
  assert.equal((await hook(stamp,'0'.repeat(64),body)).status,401);
  assert.equal((await hook(stamp-301,signature,body)).status,401);
  assert.equal((await hook(stamp,signature,body+' ')).status,401);
  assert.equal((await hook(stamp,signature,body)).status,200);
  const audit=await request('/api/system/events',undefined,cookie);assert(!audit.text.includes(secret)&&!audit.text.includes(key));
  const {chromium}=require('./browser-test-runtime.cjs');browser=await chromium.launch({headless:true});const context=await browser.newContext();
  const separator=cookie.indexOf('=');await context.addCookies([{name:cookie.slice(0,separator),value:cookie.slice(separator+1),url:base}]);const page=await context.newPage();
  await page.goto(base+'/#users');await page.getByRole('button',{name:'Kullanıcı ekle',exact:true}).click();
  await page.locator('input[name=businessId]').waitFor();assert.equal(await page.locator('select[name=role] option').count(),6);
  await page.evaluate(()=>{const script=document.createElement('script');script.textContent='window.injectedInlineScript=true';document.head.appendChild(script);});
  assert.equal(await page.evaluate(()=>window.injectedInlineScript),undefined,'CSP allowed an inline script element');
  await browser.close();browser=null;
  console.log('PASS security HTTP: protected sessions/CSRF/CORS, security and error headers, request size limit, private file denial, profile secret masking, sanitized server errors and signed/timestamped ERP webhook verification. Mock credentials only; no outbound API.');
 }finally{if(browser)await browser.close();const stopped=new Promise(r=>child.once('exit',r));child.kill();await stopped;const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-security-http-'));fs.rmSync(resolved,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
