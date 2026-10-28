const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net');
const {spawn}=require('node:child_process');
const {chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
const dll=process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll');

(async()=>{
  const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
  const base='http://127.0.0.1:'+port,temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-analytics-'));
  let child,browser;
  const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'};
  for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__)/i.test(key))delete env[key];
  env.Marketplaces__StockSource__BaseUrl='';
  async function start(){
    child=spawn('dotnet',[dll,'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,stdio:'ignore',windowsHide:true});
    for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('analytics test server startup');
  }
  async function stop(){if(!child)return;const process=child;child=null;process.kill();await new Promise(r=>{if(process.exitCode!==null)return r();process.once('exit',r);});}
  async function api(url,{body,method,cookie}={}){
    const r=await fetch(base+'/api/'+url,{method:method||(body===undefined?'GET':'POST'),headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
    return {status:r.status,body:await r.json().catch(()=>null),cookie:(r.headers.get('set-cookie')||'').split(';')[0]};
  }
  try{
    await start();await stop();
    const file=path.join(temp,'Data/demo-store.json'),db=JSON.parse(fs.readFileSync(file,'utf8'));
    db.orders=Array.from({length:80},(_,i)=>({id:'ANALYTICS-'+i,customer:'Test customer',channel:'Analytics test',sourceMode:'local',product:'Fixture',quantity:1,total:100+i,totalKnown:i!==1,currency:'TRY',status:i%10===0?'İptal':'Tamamlandı',date:'2026-10-15T12:00:00Z',lines:[]}));
    db.orders.push({...db.orders[2],id:'ANALYTICS-USD',total:9999,currency:'USD'});
    const product=db.products.find(p=>p.id==='p001');assert(product);product.price=200;product.stock=4;
    fs.writeFileSync(file,JSON.stringify(db));await start();
    const filtered=(await api('analytics/reports?mode=local&currency=TRY')).body;
    const eligible=db.orders.filter(o=>o.currency==='TRY'&&o.status!=='İptal'&&o.totalKnown),total=eligible.reduce((sum,o)=>sum+o.total,0);
    assert.equal(filtered.orderCount,80,'reports must include orders beyond the 50-item list');
    assert.equal(filtered.revenue,total);assert.equal(filtered.revenueOrderCount,eligible.length);assert.equal(filtered.unknownTotalCount,1);
    assert.equal(filtered.averageBasket,Math.round(total/eligible.length*100)/100);
    assert.equal((await api('analytics/reports?mode=local&currency=USD')).body.revenue,9999);
    assert.equal((await api('analytics/reports?mode=real')).body.orderCount,0);
    assert.equal((await api('analytics/reports?channel=does-not-exist')).body.averageBasket,null);
    assert.equal((await api('analytics/reports?mode=local&startDate='+Date.parse('2026-10-16T00:00:00Z'))).body.orderCount,0);
    for(const query of ['startDate=2&endDate=1','mode=invalid','currency=bad','startDate=-1'])assert.equal((await api('analytics/reports?'+query)).status,400);
    const overview=(await api('overview')).body,all=(await api('analytics/reports')).body;
    assert.equal(all.orderCount,overview.orders-1,'TRY report excludes the one USD order');assert.equal(all.revenue,overview.revenue);
    let profit=(await api('analytics/profit')).body.products.find(p=>p.id===product.id);
    assert.equal(profit.margin,null);assert.equal(profit.state,'missing_cost');
    const cost={unitCost:100,commissionRate:10,shippingCost:5,otherCost:5,currency:'TRY'};
    assert.equal((await api('analytics/costs/'+product.id,{method:'PUT',body:{...cost,unitCost:-1}})).status,400);
    assert.equal((await api('analytics/costs/'+product.id,{method:'PUT',body:{...cost,currency:'USD'}})).status,409);
    assert.equal((await api('analytics/costs/missing',{method:'PUT',body:cost})).status,404);
    assert.equal((await api('analytics/costs/'+product.id,{method:'PUT',body:cost})).status,200);
    profit=(await api('analytics/profit')).body.products.find(p=>p.id===product.id);assert.equal(profit.margin,70);assert.equal(profit.marginRate,35);
    await api('products/'+product.id,{method:'PUT',body:{price:250}});
    assert.equal((await api('analytics/profit')).body.products.find(p=>p.id===product.id).margin,115,'profit follows catalogue price changes');
    await stop();await start();assert.equal((await api('analytics/profit')).body.products.find(p=>p.id===product.id).margin,115,'costs persist across restart');
    assert((await api('analytics/workspace')).body.alerts.some(a=>a.id==='stock:'+product.id));

    browser=await chromium.launch({headless:true});const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
    await page.goto(base+'/#reports');await page.waitForFunction(()=>axState.reports&&axState.workspace);
    assert(!(await page.locator('.content').innerText()).includes('Gerçek API bağlantısı: 0'));
    await page.locator('[data-analysis=reports] select[name=mode]').selectOption('local');
    await page.locator('[data-analysis=reports] button').filter({hasText:'Filtrele'}).click();
    await page.waitForFunction(()=>axState.query.mode==='local'&&axState.reports?.orderCount===80);
    assert((await page.locator('[data-analysis=reports]').innerText()).includes('80'));
    for(const view of ['profit','alerts','activity','automation','settings','users']){
      await page.evaluate(view=>nav(view),view);
      if(view==='users')await page.getByRole('heading',{name:'İlk yönetici hesabını oluştur'}).waitFor();
      else await page.locator('[data-analysis='+view+']').waitFor();
      assert(!(await page.locator('.content').innerText()).includes('Tarayıcıya kaydedilen taslaklar'));
    }
    await page.evaluate(()=>nav('profit'));await page.waitForFunction(()=>axState.profit);
    await page.locator('[data-profit-search]').fill(product.sku);await page.getByRole('button',{name:'Maliyeti düzenle',exact:true}).click();
    await page.locator('.modal input[name=unitCost]').fill('110');await page.getByRole('button',{name:'Maliyeti kaydet',exact:true}).click();
    await page.locator('.modal').waitFor({state:'detached'});
    assert.equal((await api('analytics/profit')).body.products.find(p=>p.id===product.id).margin,105);
    await page.evaluate(()=>nav('alerts'));await page.waitForFunction(()=>axState.workspace?.alerts.some(a=>a.id==='stock:p001'));
    let settings=(await api('settings')).body;
    await api('settings',{method:'PUT',body:{...settings,lowStockThreshold:2}});
    await page.waitForFunction(()=>axState.workspace?.settings.lowStockThreshold===2,{},{timeout:15000});
    assert(!(await page.evaluate(()=>axAlerts())).some(a=>a.id==='stock:'+product.id),'alerts update without reloading the page');
    await page.evaluate(()=>nav('settings'));await page.locator('[data-analysis=settings] input[name=businessName]').fill('Connected workspace');
    await page.getByRole('button',{name:'Ayarları kaydet',exact:true}).click();
    await page.waitForFunction(()=>document.querySelector('.user-block')?.textContent.includes('Connected workspace'));
    assert((await page.locator('.user-block').innerText()).includes('Connected workspace'));
    await page.evaluate(()=>nav('activity'));assert((await page.locator('[data-analysis=activity]').innerText()).includes('Ayarlar güncellendi'));
    await page.evaluate(()=>nav('automation'));await page.locator('[data-automation] form').waitFor();
    await page.locator('[data-automation] input[name=intervalSeconds]').fill('45');
    await page.getByRole('button',{name:'Otomasyonu kaydet',exact:true}).click();
    await page.waitForFunction(()=>auData?.automation.settings.intervalSeconds===45&&!auSaving);
    assert.equal((await api('sync/automation')).body.automation.settings.intervalSeconds,45);
    for(const view of ['reports','profit','alerts','activity','automation','settings','users']){
      await page.evaluate(view=>nav(view),view);await page.setViewportSize({width:390,height:844});
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),view+' must fit mobile');
    }
    assert.deepEqual(errors,[]);

    const admin=await api('access/setup',{body:{name:'analytics-admin',password:'Test-password-2026'}});
    assert.equal((await api('analytics/workspace')).status,401);
    await api('access/users',{cookie:admin.cookie,body:{name:'analytics-viewer',password:'Viewer-password-2026',role:'viewer'}});
    await api('access/users',{cookie:admin.cookie,body:{name:'analytics-operator',password:'Operator-password-2026',role:'operator'}});
    for(const [name,password] of [['analytics-viewer','Viewer-password-2026'],['analytics-operator','Operator-password-2026']]){
      const user=await api('access/login',{body:{name,password}});
      assert.equal((await api('analytics/reports',{cookie:user.cookie})).status,200);
      assert.equal((await api('analytics/costs/'+product.id,{method:'PUT',body:cost,cookie:user.cookie})).status,403);
      const workspace=(await api('analytics/workspace',{cookie:user.cookie})).body;
      assert.equal(workspace.canReadAudit,false);assert.deepEqual(workspace.events,[]);
    }
    const adminWorkspace=(await api('analytics/workspace',{cookie:admin.cookie})).body;
    assert.equal(adminWorkspace.canReadAudit,true);
    console.log('PASS connected analysis/system: full-order reports, currencies/dates, unknown totals, durable costs, live prices/alerts/settings, seven screens, automation controls, mobile and role restrictions.');
  }finally{
    if(browser)await browser.close();await stop();
    const resolved=path.resolve(temp);if(!resolved.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(resolved).startsWith('marketpilot-analytics-'))throw Error('Unsafe test cleanup');
    fs.rmSync(resolved,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
