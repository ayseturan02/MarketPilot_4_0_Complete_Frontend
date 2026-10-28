const fs=require('fs'),os=require('os'),path=require('path'),net=require('net'),assert=require('assert/strict'),{spawn}=require('child_process');
const {chromium}=require('./browser-test-runtime.cjs'),root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
  const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-accounts-http-')),base='http://127.0.0.1:'+port;
  const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'};
  for(const [id,seller,label]of[['accountA','101','Birinci satıcı'],['accountB','102','İkinci satıcı']]){
    for(const[k,v]of Object.entries({Marketplace:'trendyol',Environment:'production',Label:label,AllowWrites:'false','Credentials__SellerId':seller,'Credentials__ApiKey':'fixture-key-'+id,'Credentials__ApiSecret':'fixture-secret-'+id}))env['MarketplaceAccounts__'+id+'__'+k]=v;
  }
  const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env});let browser;
  async function api(url,body,cookie,origin=base){const r=await fetch(base+url,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',Origin:origin,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return{status:r.status,body:await r.json(),cookie:(r.headers.get('set-cookie')||'').split(';')[0]};}
  const create=(profile,extra={})=>api('/api/integrations/connections',{name:'Test '+profile,marketplace:'trendyol',mode:'real',environment:'production',credentialProfile:profile,...extra});
  try{
    let ready=false;for(let i=0;i<120;i++){try{if((await api('/api/health')).status===200){ready=true;break;}}catch{}await sleep(100);}assert(ready);
    const profiles=await api('/api/integrations/credential-profiles');assert.equal(profiles.status,200);assert.equal(profiles.body.length,2);assert(profiles.body.every(x=>x.configured&&!x.writes));assert(!JSON.stringify(profiles.body).includes('fixture-secret'));
    assert.equal((await create('missing')).status,409);assert.equal((await create('accountA',{environment:'stage'})).status,409);assert.equal((await create('accountA',{marketplace:'amazon'})).status,409);
    browser=await chromium.launch({headless:true});const page=await browser.newPage(),errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    // The creation UI tests the new account automatically: intercept that check so synthetic keys cannot reach a marketplace.
    await page.route('**/api/integrations/connections/*/test*',async route=>{
      const stores=(await api('/api/integrations/connections')).body,id=route.request().url().split('/connections/')[1].split('/')[0];
      const connection=stores.find(s=>s.id===id);await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({connection,result:{marketplace:'trendyol',sellerId:connection.settings.credentialProfile==='accountA'?'101':'102',environment:'production',storeFrontCode:'TR',addresses:[],readAt:new Date().toISOString()}})});
    });
    await page.goto(base+'/#connections');await page.locator('.connection-card').first().waitFor();
    for(const [profile,name]of[['accountA','İlk hesap'],['accountB','İkinci hesap']]){
      await page.getByRole('button',{name:'Mağaza ekle',exact:true}).click();
      await page.locator('.store-form input[name=name]').fill(name);
      await page.locator('.store-form select[name=marketplace]').selectOption('trendyol');
      await page.locator('.store-form select[name=mode]').selectOption('real');
      await page.locator('.store-form select[name=credentialProfile] option[value="'+profile+'"]').waitFor({state:'attached'});
      await page.locator('.store-form select[name=credentialProfile]').selectOption(profile);
      await page.locator('.store-form button[type=submit]').click();
      await page.locator('.store-form').waitFor({state:'detached'});
      await page.waitForFunction(()=>!integrationBusy);
    }
    const stores=(await api('/api/integrations/connections')).body,real=stores.filter(s=>s.settings.mode==='real');
    assert.equal(real.length,2);assert.notEqual(real[0].settings.credentialProfile,real[1].settings.credentialProfile);
    assert.equal((await create('accountA')).status,409);
    const write={requestId:'fixture-write',quantity:9,sku:'SKU',confirm:true};
    assert.equal((await api('/api/integrations/connections/'+real[0].id+'/stock-price',write)).status,409);
    assert.equal((await api('/api/integrations/connections/'+real[0].id+'/stock-price',{...write,confirm:false})).status,400);
    assert.equal((await api('/api/integrations/connections/'+real[0].id+'/stock-price',{...write,quantity:-1})).status,400);
    assert.equal((await api('/api/integrations/connections/'+real[0].id+'/stock-price',write,undefined,'https://foreign.example')).status,403);
    const missing=await api('/api/integrations/connections',{name:'No profile',marketplace:'trendyol',mode:'real',environment:'production'});
    assert.equal(missing.body.settings.credentialProfile,'_unassigned');
    assert.equal((await api('/api/integrations/connections/'+missing.body.id+'/test',{})).status,409);
    // The shared action queues a demo request and never needs external credentials.
    await page.evaluate(()=>nav('connections'));
    await page.locator('.connection-card').first().getByRole('button',{name:'Stok / fiyat gönder',exact:true}).click();
    await page.locator('.store-form input[name=barcode]').fill('8691000000001');
    await page.locator('.store-form input[name=quantity]').fill('19');
    await page.locator('.store-form input[name=confirm]').check();
    await page.locator('.store-form button[type=submit]').click();await page.locator('.store-form').waitFor({state:'detached'});
    assert((await api('/api/sync/automation')).body.outbox.recent.some(j=>j.quantity===19&&j.mode==='demo'));
    await page.setViewportSize({width:390,height:844});await page.evaluate(()=>nav('connections'));
    assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'mobile sales channels');
    const persisted=fs.readFileSync(path.join(temp,'Data/integration-connections.json'),'utf8');assert(!persisted.includes('fixture-key')&&!persisted.includes('fixture-secret'));
    const admin=await api('/api/access/setup',{name:'account-admin',password:'Fixture-Admin-Password-2026'});
    await api('/api/access/users',{name:'account-viewer',password:'Fixture-Viewer-Password-2026',role:'viewer'},admin.cookie);
    const viewer=await api('/api/access/login',{name:'account-viewer',password:'Fixture-Viewer-Password-2026'});
    assert.equal((await api('/api/integrations/credential-profiles',undefined,viewer.cookie)).status,200);
    assert.equal((await api('/api/integrations/connections/'+real[0].id+'/stock-price',write,viewer.cookie)).status,403);
    assert.deepEqual(errors,[]);
    console.log('PASS account HTTP/browser: two sellers on one channel/environment, profile picker, invalid/duplicate profiles, no fallback, secret-free metadata/persistence, real-write guard, input/Origin validation, shared demo outbox action, mobile and viewer protection. No outbound marketplace calls.');
  }finally{
    if(browser)await browser.close();child.kill();await new Promise(r=>{if(child.exitCode!==null)return r();child.once('exit',r);setTimeout(r,5000)});
    const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-accounts-http-'))throw Error('Unsafe cleanup');
    fs.rmSync(absolute,{recursive:true,force:true});
  }
})().catch(e=>{console.error(e);process.exitCode=1});
