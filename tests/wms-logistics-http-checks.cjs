const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
(async()=>{
  const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-logistics-http-'));
  const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const base='http://127.0.0.1:'+port;
  const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'}});
  let browser;
  async function api(url,body,method='POST'){const r=await fetch(base+'/api/wms/logistics'+url,{method:body===undefined?'GET':method,headers:{'Content-Type':'application/json',Origin:base},body:body===undefined?undefined:JSON.stringify(body)});return{status:r.status,body:await r.json()};}
  try{
    let ready=false;for(let i=0;i<100;i++){try{if((await api('')).status===200){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,100));}assert(ready,'server startup');
    browser=await chromium.launch({headless:true});const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base+'/#shipping');await page.getByRole('button',{name:'Mock senaryoları yükle'}).waitFor();
    await page.getByRole('button',{name:'Mock senaryoları yükle'}).click();await page.getByRole('heading',{name:'MOCK-B / MOCK-B-P1',exact:true}).waitFor();
    assert.equal((await api('')).body.length,3);
    await page.locator('form').filter({has:page.locator('[name=wmsState]')}).locator('[name=channel]').fill('amazon');
    const mapping=page.locator('form').filter({has:page.locator('[name=wmsState]')});
    await mapping.locator('[name=wmsState]').selectOption('restocked');await mapping.locator('[name=channelState]').fill('ReturnReceived');await mapping.locator('[name=approved]').check();await mapping.getByRole('button').click();
    await page.waitForFunction(()=>document.querySelector('.content')?.textContent.includes('Kanal karşılığı: ReturnReceived'));
    for(const view of ['tracking','returns','labels']){
      await page.evaluate(v=>nav(v),view);await page.getByRole('button',{name:'Mock senaryoları yükle'}).waitFor();
      assert((await page.locator('.content').innerText()).includes('MOCK-B'));
    }
    const rows=(await api('')).body;assert.equal(rows.find(x=>x.latest.shipmentId==='MOCK-C').resellableReturnQuantity,0);
    assert.equal(rows.find(x=>x.latest.shipmentId==='MOCK-B').resellableReturnQuantity,1);
    const invalid={...rows[0].latest,eventId:'invalid-sequence',sequence:100,state:'restocked',wmsRestockApproved:true};
    assert.equal((await api('/mock/events',invalid)).status,400);
    assert.equal((await api('/mock/scenarios',{})).body.length,3);
    assert.deepEqual(errors,[]);console.log('PASS WMS logistics HTTP + browser: mock intake, shipment/cargo/returns/labels screens, policy approval, invalid transitions, quarantine, restock and seed replay. Isolated data; no external service calls.');
  }finally{
    if(browser)await browser.close();
    const stopped=new Promise(r=>child.once('exit',r));child.kill();await stopped;
    const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-logistics-http-'));fs.rmSync(resolved,{recursive:true,force:true});
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
