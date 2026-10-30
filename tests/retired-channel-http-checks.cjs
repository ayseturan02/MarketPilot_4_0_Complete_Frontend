const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net');
const {spawn}=require('node:child_process'),{chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
const retired=/woo[\s_-]*commerce/i;
(async()=>{
  const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-retired-channel-')),base='http://127.0.0.1:'+port;
  const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'};
  for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__)/i.test(key))delete env[key];
  env.Marketplaces__StockSource__BaseUrl='';
  let child,browser;
  async function start(){child=spawn('dotnet',[process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('test server startup');}
  async function stop(){if(!child)return;const process=child;child=null;process.kill();await new Promise(r=>{if(process.exitCode!==null)return r();process.once('exit',r);});}
  const read=()=>JSON.parse(fs.readFileSync(path.join(temp,'Data/demo-store.json'),'utf8'));
  try{
    await start();assert(!retired.test(JSON.stringify(read())),'new workspaces must not seed the retired channel');await stop();
    const db=read(),originalOrders=JSON.stringify(db.orders),channels=[...db.products[0].channels],counts={products:db.products.length,orders:db.orders.length,connections:db.connections.length};
    db.orders.push({...db.orders[0],id:'retired-order',channel:'WooCommerce',channelOrderNumber:'retired-number',packageId:'retired-package'});
    db.connections.push({id:'old-store',name:' WoO Commerce ',enabled:true,available:true});
    db.products[0].channels.push('WooCommerce');
    db.productCosts[db.products[0].id]={unitCost:10,commissionRate:2,shippingCost:1,otherCost:0,currency:'TRY'};
    db.shipments.push({id:'retired-shipment',orderId:'retired-number',carrier:'Fixture',trackingNumber:'REMOVE',status:'Hazırlanıyor'});
    db.shipments.push({id:'retained-shipment',orderId:db.orders[0].id,carrier:'Fixture',trackingNumber:'KEEP',status:'Hazırlanıyor'});
    db.activity.push({title:'WooCommerce operation',detail:'Retired fixture',at:new Date().toISOString()});
    fs.writeFileSync(path.join(temp,'Data/demo-store.json'),JSON.stringify(db));await start();
    const cleaned=read();assert.equal(JSON.stringify(cleaned.orders),originalOrders,'other orders must remain unchanged');
    assert.equal(cleaned.products.length,counts.products);assert.equal(cleaned.connections.length,counts.connections);
    assert.deepEqual(cleaned.products[0].channels,channels);assert.equal(cleaned.productCosts[db.products[0].id].unitCost,10);
    assert.equal(cleaned.shipments.length,1);assert.equal(cleaned.shipments[0].id,'retained-shipment');
    assert(!retired.test(JSON.stringify(cleaned)),'persisted legacy channel references must be removed');
    for(const route of ['overview','products','orders','connections','shipments','activity','analytics/reports','analytics/profit','analytics/workspace','export/products.csv']){
      const response=await fetch(base+'/api/'+route);assert(response.ok,route);assert(!retired.test(await response.text()),route+' must not expose the retired channel');
    }
    const rejected=await fetch(base+'/api/products',{method:'POST',headers:{'Content-Type':'application/json',Origin:base},body:JSON.stringify({name:'Retired fixture',sku:'RETIRED-REJECT',price:1,stock:1,channels:['WooCommerce']})});
    assert.equal(rejected.status,405,'manual product creation is disabled, including retired channels');
    await stop();await start();assert.equal(read().orders.length,counts.orders,'cleanup must be idempotent');

    browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
    await page.addInitScript(()=>localStorage.setItem('marketpilot_v4_front_drafts',JSON.stringify({marketplace_rules:[{values:['Old rule','WooCommerce'],created:'2026-10-28T12:00:00Z'},{values:['Keep rule','Trendyol'],created:'2026-10-28T12:00:00Z'}]})));
    await page.goto(base);await page.waitForFunction(()=>data.overview);
    assert.equal(await page.evaluate(()=>mpDrafts.marketplace_rules.length),1,'old browser drafts must not surface a retired channel');
    assert(!retired.test(await page.evaluate(()=>JSON.stringify(MP_CHANNELS))));
    const views=await page.evaluate(()=>Object.keys(names));
    for(const view of views){await page.evaluate(view=>nav(view),view);await page.waitForTimeout(75);assert(!retired.test(await page.locator('.content').innerText()),view+' must not show the retired channel');}
    assert.deepEqual(errors,[]);
    console.log('PASS retired channel removal: fresh seeds, legacy/restart cleanup, preserved products/costs/unrelated orders, related shipments, API/CSV/report consistency, write refusal, browser drafts and all '+views.length+' menu pages.');
  }finally{
    if(browser)await browser.close();await stop();
    const resolved=path.resolve(temp);if(!resolved.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(resolved).startsWith('marketpilot-retired-channel-'))throw Error('Unsafe test cleanup');fs.rmSync(resolved,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1});
