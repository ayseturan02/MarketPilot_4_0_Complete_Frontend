// Central stock over real HTTP in an isolated content root: baseline, cross-channel deduction, safety stock and oversell refusal.
const {spawn}=require('child_process'),fs=require('fs'),os=require('os'),path=require('path'),net=require('net'),assert=require('assert');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
  const probe=net.createServer();await new Promise((res,rej)=>probe.once('error',rej).listen(0,'127.0.0.1',res));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-central-stock-')),base='http://127.0.0.1:'+port;
  const child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Marketplaces__StockSource__BaseUrl:''}});
  const call=async(url,body)=>{const r=await fetch(base+'/api/integrations/central-stock'+url,body?{method:'POST',headers:{'Content-Type':'application/json',Origin:base},body:JSON.stringify(body)}:{});return {status:r.status,json:await r.json()};};
  const item=(v,sku)=>(v.view||v).items.find(i=>i.sku===sku);
  // Demo stores whose stock is not managed by the channel itself (FBA / n11depom) must all follow the central number.
  const followers=(v,i)=>v.stores.filter(s=>s.mode==='demo'&&!['amazon','n11'].includes(s.marketplace)).map(s=>i.channels.find(c=>c.storeId===s.id).stock);
  // Demo channel jobs fall due 2 s after queueing and the worker ticks every second; poll instead of one fixed wait.
  const settle=async(sku,expected)=>{for(let i=0;i<30;i++){await sleep(500);const r=await call('');if(r.status!==200)continue;const stocks=followers(r.json,item(r.json,sku));if(stocks.length&&stocks.every(n=>n===expected))return r;}throw Error('Channels did not converge to '+expected+' for '+sku);};
  try{
    for(let i=0;i<60;i++){try{if((await fetch(base+'/api/health')).ok)break;}catch{}await sleep(500);}
    let r=await call('/run',{});assert.equal(r.status,200);assert.equal(r.json.source.mode,'demo');
    assert.ok(r.json.events.some(e=>e.message.includes('izlemeye alındı')),'existing orders are baselined, not deducted');
    assert.equal(item(r.json,'PNT-BEJ-38').quantity,9,'baseline keeps source quantity');
    r=await settle('PNT-BEJ-38',9);assert.deepEqual([...new Set(followers(r.json,item(r.json,'PNT-BEJ-38')))],[9],'all channels equal central stock');
    r=await call('/demo-sale',{connectionId:'trendyol',barcode:'8691000000005',quantity:2});assert.equal(r.status,200);
    assert.equal(item(r.json,'PNT-BEJ-38').quantity,7,'sale deducted centrally');assert.ok(r.json.view.sales[0].reported,'sale reported to source');
    r=await settle('PNT-BEJ-38',7);assert.deepEqual([...new Set(followers(r.json,item(r.json,'PNT-BEJ-38')))],[7],'sale on one channel lowers every channel');
    r=await call('/run',{});assert.equal(item(r.json,'PNT-BEJ-38').quantity,7,'re-reading the source does not double count a reported sale');
    r=await call('/settings',{autoSync:false,intervalSeconds:60,safetyStock:2});assert.equal(r.status,200);await call('/run',{});r=await settle('PNT-BEJ-38',5);
    assert.equal(item(r.json,'PNT-BEJ-38').available,5);assert.deepEqual([...new Set(followers(r.json,item(r.json,'PNT-BEJ-38')))],[5],'safety stock withheld from channels');
    r=await call('/demo-sale',{connectionId:'trendyol',barcode:'8691000000003',quantity:5});assert.equal(r.status,400,'cannot sell more than the channel shows');
    r=await call('/settings',{autoSync:true,intervalSeconds:5,safetyStock:0});assert.equal(r.status,400,'interval lower bound');
    const cross=await fetch(base+'/api/integrations/central-stock/run',{method:'POST',headers:{'Content-Type':'application/json',Origin:'http://evil.example'},body:'{}'});assert.equal(cross.status,403,'cross-origin run rejected');
    console.log('PASS central stock: baseline without deduction, cross-channel deduction, source report without double count, safety stock, oversell refusal, settings bounds and cross-origin rejection. Isolated temp data.');
  }finally{child.kill();await sleep(500);fs.rmSync(temp,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
