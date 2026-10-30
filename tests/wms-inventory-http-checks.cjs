const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
(async()=>{
  const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-inventory-http-'));
  const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const base='http://127.0.0.1:'+port;
  const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,Logging__EventLog__LogLevel__Default:'None',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Wms__Mode:'mock',Wms__AllowMockWrites:'false'}});
  let browser;
  async function api(url,body){const r=await fetch(base+'/api/wms/'+url,{method:body?'PUT':'GET',headers:{'Content-Type':'application/json',Origin:base},body:body?JSON.stringify(body):undefined});return{status:r.status,body:await r.json()};}
  try{
    let result;
    for(let i=0;i<100;i++){try{result=await api('inventory');if(result.status===200)break;}catch{}await new Promise(r=>setTimeout(r,100));}
    assert.equal(result?.status,200);
    assert.equal(result.body.ruleApprovedAt,null);
    assert(result.body.stock.every(r=>r.sellableQuantity===null));
    const accounts=result.body.marketplaceAccounts;assert(accounts.length>=2);
    const policy={safetyStockByWarehouse:{'mock-main':5,'mock-outlet':0},warehouseAssignmentsByAccount:{[accounts[0].id]:['mock-main','mock-outlet'],[accounts[1].id]:['mock-outlet']},channelStockLimits:{[accounts[0].id]:20}};
    assert.equal((await api('inventory/policy',policy)).status,200);
    result=await api('inventory');
    const row=(warehouse,sku)=>result.body.stock.find(r=>r.snapshot.warehouseId===warehouse&&r.snapshot.sku===sku);
    assert.equal(row('mock-main','TSH-EKR-S').snapshot.onHandQuantity,36);
    assert.equal(row('mock-main','TSH-EKR-S').snapshot.reservedQuantity,4);
    assert.equal(row('mock-main','TSH-EKR-S').sellableQuantity,25);
    assert.equal(row('mock-outlet','TSH-EKR-S').sellableQuantity,7);
    assert(result.body.stock.filter(r=>r.snapshot.providerManaged).every(r=>r.sellableQuantity===null&&!r.canAllocateToChannels));
    assert.equal(result.body.channelStock.find(r=>r.accountId===accounts[0].id&&r.sku==='TSH-EKR-S').sellableQuantity,20);
    assert.equal(result.body.channelStock.find(r=>r.accountId===accounts[1].id&&r.sku==='TSH-EKR-S').sellableQuantity,7);
    assert.equal(result.body.channelStock.find(r=>r.accountId===accounts[1].id&&r.sku==='TSH-LAC-M').sellableQuantity,null);
    assert.equal((await api('inventory/policy',{...policy,safetyStockByWarehouse:{'mock-main':-1}})).status,400);
    assert((await api('inventory/movements')).body.length>0);
    assert((await api('inventory/history')).body.length>0);
    browser=await chromium.launch({headless:true});const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base);await page.locator('.content').waitFor();await page.evaluate(()=>nav('inventory'));
    try { await page.getByText('Hesap bazlı stok önizlemesi',{exact:true}).waitFor({timeout:10000}); }
    catch(error) { throw new Error(error.message+'; UI: '+(await page.locator('body').innerText()).slice(-2000)+'; errors: '+errors.join('; ')); }
    await page.getByRole('button',{name:'Hareket geçmişi',exact:true}).click();await page.getByText('WMS stok hareketleri',{exact:true}).waitFor();
    await page.getByRole('button',{name:/Fark inceleme/}).click();await page.getByText('ERP / WMS fark incelemesi',{exact:true}).waitFor();
    assert.deepEqual(errors,[]);
    console.log('PASS WMS inventory HTTP/browser: explicit approval, multi-depot/account isolation, categories, safety and limits, missing SKU remains unknown, managed stock protection, validation, movement/history and discrepancy screens. No stock writes.');
  }finally{
    if(browser)await browser.close();
    const stopped=new Promise(r=>child.once('exit',r));child.kill();await stopped;
    const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-inventory-http-'));fs.rmSync(resolved,{recursive:true,force:true});
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
