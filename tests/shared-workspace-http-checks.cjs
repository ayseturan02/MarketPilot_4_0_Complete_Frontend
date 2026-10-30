const {sourceProduct}=require('./erp-source-fixtures.cjs');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict');
const {spawn}=require('node:child_process'),{randomUUID}=require('node:crypto'),{chromium}=require('./browser-test-runtime.cjs');
const root=path.resolve(__dirname,'..'),sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const base='http://127.0.0.1:'+port,temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-whole-workspace-'));
 const env={...process.env,ASPNETCORE_ENVIRONMENT:'Production',DOTNET_ENVIRONMENT:'Production',Deployment__Mode:'local',Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false'};
 for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Marketplaces__)/i.test(key))delete env[key];env.Marketplaces__StockSource__BaseUrl='';
 let child,browser;
 async function start(){child=spawn('dotnet',[process.env.MARKETPILOT_WEB_DLL||path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{env,windowsHide:true,stdio:'ignore'});for(let i=0;i<100;i++){try{if((await fetch(base+'/api/health')).ok)return;}catch{}await sleep(100);}throw Error('server startup');}
 async function stop(){if(!child)return;const running=child;child=null;running.kill();await new Promise(r=>{if(running.exitCode!==null)return r();running.once('exit',r);});}
 async function api(route,{body,method,cookie}={}){const r=await fetch(base+'/api/'+route,{method:method||(body===undefined?'GET':'POST'),headers:{'Content-Type':'application/json',Origin:base,...(cookie?{Cookie:cookie}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});return{status:r.status,body:await r.json().catch(()=>null),cookie:(r.headers.get('set-cookie')||'').split(';')[0]};}
 const view=async(name,search='',cookie)=>{const r=await api('workspace/views/'+name+'?'+new URLSearchParams({search,size:'100'}),{cookie});assert.equal(r.status,200,name);return r.body;};
 async function eventually(check){for(let i=0;i<100;i++){if(await check())return;await sleep(100);}throw Error('shared state did not converge');}
 try{
  await start();
  const modules=['picking','labels','tracking','returns','refunds','invoices','invoice_drafts','efatura','invoice_archive','invoice_matching','credit_notes','payments','reconciliation','marketplace_rules','messages'];
  for(const module of modules){const data=await view(module);assert(data.items.length<=100);assert(!JSON.stringify(data).includes('Taslak-001'),'static example records must not appear');}
  assert.equal((await api('workspace/views/invalid')).status,404);assert.equal((await api('workspace/views/invoices?size=101')).status,400);
  const created=await sourceProduct(base,{name:'Shared workflow product',sku:'SHARED-WORKFLOW',category:'Giyim / Tişört',price:200,stock:40,warehouseCode:'MAIN',currency:'TRY',vatIncluded:true,channels:[]});assert.equal(created.status,200);
  const product=created.body,initialOverview=(await api('overview')).body;
  const draft={customer:'Shared customer',channel:'Yerel satış',requestId:randomUUID(),lines:[{productId:product.id,quantity:3}]};
  const order=(await api('workspace/orders',{body:draft})).body;assert(order.id,JSON.stringify(order));assert.equal(order.total,600);assert.equal(order.lines[0].productId,product.id);
  assert.equal((await api('workspace/orders',{body:draft})).body.id,order.id);
  assert.equal((await api('workspace/orders',{body:{...draft,customer:'Different'}})).status,409);
  assert.equal((await api('workspace/orders',{body:{...draft,requestId:randomUUID(),lines:[{productId:product.id,quantity:1000}]}})).status,400);
  const master=()=>api('products/search?sku='+product.sku);
  assert.equal((await master()).body.items[0].stock,40,'local orders must not mutate ERP-owned stock');
  assert.equal((await api('overview')).body.orders,initialOverview.orders+1);
  assert((await view('picking',order.id)).items.some(o=>o.orderId===order.id));
  assert.equal((await api('analytics/reports?channel='+encodeURIComponent('Yerel satış'))).body.revenue,600);
  const localRecord={kind:'invoice',orderId:order.id,requestId:randomUUID(),note:'Shared invoice draft'};
  const invoice=(await api('workspace/records',{body:localRecord})).body;assert(invoice.id,JSON.stringify(invoice));
  assert.equal((await api('workspace/records',{body:localRecord})).body.id,invoice.id);
  assert.equal((await api('workspace/records',{body:{...localRecord,requestId:randomUUID()}})).status,409);
  assert.equal((await api('workspace/records',{body:{...localRecord,orderId:'missing',requestId:randomUUID()}})).status,404);
  for(const name of ['invoices','invoice_drafts','efatura'])assert((await view(name,order.id)).items.some(r=>r.id===invoice.id),name+' must share invoice identity');
  assert((await view('invoice_matching',order.id)).items.some(r=>r.status==='Eşleşti'));
  assert((await api('workspace/orders/'+order.id)).body.documents.some(r=>r.id===invoice.id));
  const archive={action:'archive',requestId:randomUUID(),version:invoice.version};
  assert.equal((await api('workspace/views/invoice_drafts/'+invoice.id+'/actions',{body:archive})).status,200);
  assert.equal((await api('workspace/views/invoice_drafts/'+invoice.id+'/actions',{body:archive})).status,200,'action retry must be idempotent');
  assert(!(await view('invoice_drafts',order.id)).items.some(r=>r.id===invoice.id));assert((await view('invoice_archive',order.id)).items.some(r=>r.id===invoice.id));
  const refund=(await api('workspace/records',{body:{kind:'refund',orderId:order.id,requestId:randomUUID(),amount:100}})).body;
  assert((await view('refunds',order.id)).items.some(r=>r.id===refund.id));assert((await view('returns',order.id)).items.some(r=>r.id===refund.id));
  assert.equal((await api('workspace/records',{body:{kind:'refund',orderId:order.id,requestId:randomUUID(),amount:501}})).status,400);
  const shipment=(await api('shipments',{body:{orderId:order.id,carrier:'Local carrier',trackingNumber:'SHARED-TRACK'}})).body;assert(shipment.id);
  for(const name of ['tracking','labels'])assert((await view(name,'SHARED-TRACK')).items.some(r=>r.orderId===order.id));
  assert.equal((await api('shipments/'+shipment.id+'/status',{method:'PUT',body:{status:'Teslim edildi'}})).status,200);
  assert.equal((await api('workspace/orders/'+order.id)).body.order.status,'Tamamlandı');
  for(let i=0;i<2;i++)assert.equal((await api('orders/'+order.id+'/status',{method:'PUT',body:{status:'İptal'}})).status,200);
  assert.equal((await master()).body.items[0].stock,40,'cancellation must not write ERP-owned stock');
  assert.equal((await api('stock/movements')).body.filter(m=>m.productId===product.id).length,0);
  assert.equal((await api('orders/'+order.id+'/status',{method:'PUT',body:{status:'Yeni'}})).status,200);assert.equal((await master()).body.items[0].stock,40);
  await api('orders/'+order.id+'/status',{method:'PUT',body:{status:'İade'}});assert.equal((await master()).body.items[0].stock,40);

  const question=(await view('messages')).items.find(r=>r.storeId==='trendyol'&&r.action==='answer');assert(question);
  const answer={action:'answer',requestId:randomUUID(),text:'Merhaba, ürün bilgileri ortak kayıttan güncellenmiştir.',source:question.source};
  assert.equal((await api('workspace/views/messages/'+encodeURIComponent(question.id)+'/actions',{body:{...answer,source:'foreign-store'}})).status,409);
  assert.equal((await api('workspace/views/messages/'+encodeURIComponent(question.id)+'/actions',{body:answer})).status,200);
  await eventually(async()=> (await view('messages')).items.find(r=>r.id===question.id)?.status==='ANSWERED');
  assert.equal((await api('workspace/views/messages/'+encodeURIComponent(question.id)+'/actions',{body:answer})).status,200);
  const native=(await api('integrations/demo/connections/trendyol/workspace')).body;
  assert(native.questions.some(q=>q.id===question.nativeReference&&q.answer.text===answer.text));
  const claim=(await view('returns')).items.find(r=>r.storeId==='trendyol'&&r.action==='approve'),approval={action:'approve',requestId:randomUUID(),source:claim.source};
  const stockBefore=native.products.find(p=>p.barcode===native.claims.find(c=>c.id===claim.nativeReference).barcode).stock;
  await api('workspace/views/returns/'+encodeURIComponent(claim.id)+'/actions',{body:approval});
  await eventually(async()=> (await view('returns')).items.find(r=>r.id===claim.id)?.status==='Onaylandı');
  await api('workspace/views/returns/'+encodeURIComponent(claim.id)+'/actions',{body:approval});
  const nativeAfter=(await api('integrations/demo/connections/trendyol/workspace')).body;
  assert.equal(nativeAfter.products.find(p=>p.barcode===native.claims.find(c=>c.id===claim.nativeReference).barcode).stock,stockBefore+1);
  assert.equal((await view('refunds')).items.filter(r=>r.reference==='return-'+claim.nativeReference&&r.storeId==='trendyol').length,1);

  const revision=(await api('operations/revision')).body.revision;
  const newStore=(await api('integrations/connections',{body:{name:'Shared extra outlet',marketplace:'trendyol',mode:'demo',environment:'stage'}})).body;
  assert.notEqual((await api('operations/revision')).body.revision,revision,'store changes must invalidate the global revision');
  assert((await view('messages')).items.some(r=>r.storeId===newStore.id));
  await stop();await start();assert((await view('invoice_archive',order.id)).items.some(r=>r.id===invoice.id),'documents survive restart');assert.equal((await master()).body.items[0].stock,40);

  browser=await chromium.launch({headless:true});const context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage(),errors=[];
  page.on('pageerror',error=>errors.push(error.message));await context.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(base+'/#dashboard');await page.waitForFunction(()=>workspaceDashboardReport&&workspaceLastCheck);
  assert(!(await page.locator('.content').innerText()).includes('temsili grafik'));
  for(const module of modules){await page.evaluate(id=>nav(id),module);await page.waitForFunction(id=>wuState(id).result,module);assert(!(await page.locator('[data-workflow='+module+']').innerText()).includes('Örnek işlem kayıtları'));}
  await page.evaluate(()=>nav('invoice_drafts'));await page.getByRole('button',{name:'Yeni fatura taslağı',exact:true}).click();await page.waitForFunction(()=>wuOrders.length>0);
  await page.locator('[data-wu-form] input[name=lookupSearch]').fill(order.id);await page.locator('[data-wu-form]').getByRole('button',{name:'Ara',exact:true}).click();await page.waitForFunction(()=>wuOrders.length===1);
  assert.equal(await page.locator('[data-wu-form] select[name=orderId]').inputValue(),order.id);await page.getByRole('button',{name:'Pencereyi kapat'}).count().then(async n=>{if(n)await page.getByRole('button',{name:'Pencereyi kapat'}).click();else await page.evaluate(()=>closeModal());});

  // Another client edits a product while this client views profitability.
  await page.evaluate(()=>nav('profit'));await page.waitForFunction(id=>axState.profit?.products.some(p=>p.id===id),product.id);
  await api('analytics/costs/'+product.id,{method:'PUT',body:{unitCost:100,commissionRate:0,shippingCost:0,otherCost:0,currency:'TRY'}});
  await sourceProduct(base,{...product,price:250,warehouseCode:'MAIN'});
  await page.waitForFunction(id=>axState.profit?.products.find(p=>p.id===id)?.state==='missing_price',product.id,{timeout:15000});
  await page.evaluate(()=>nav('settings'));await page.locator('[data-analysis=settings] input[name=businessName]').fill('Do not erase my edit');
  await page.locator('[data-analysis=settings] input[name=businessName]').blur();await sourceProduct(base,{...product,price:251,warehouseCode:'MAIN'});await page.waitForTimeout(3000);
  assert.equal(await page.locator('[data-analysis=settings] input[name=businessName]').inputValue(),'Do not erase my edit');
  await page.getByRole('button',{name:'Ayarları kaydet',exact:true}).click();await page.waitForFunction(()=>data.settings.businessName==='Do not erase my edit');
  await page.evaluate(()=>nav('products'));await page.locator('.ch-body .ty-business-table').waitFor();await page.locator('.content .lf-filter-form input[name=search]').fill('SHARED-WORKFLOW');await page.getByRole('button',{name:'Filtrele',exact:true}).click();await page.locator('.ch-body .ty-business-table').waitFor();assert.equal(await page.locator('.lf-filter-form').getAttribute('data-workspace-dirty'),null,'catalog filters must not block live updates');
  await sourceProduct(base,{...product,price:251,stock:39,warehouseCode:'MAIN'});await page.waitForFunction(()=>chState().result?.items.length===1&&chState().result.items[0].product.stock===39,{},{timeout:15000});
  assert.equal(await page.locator('.content .lf-filter-form input[name=search]').inputValue(),'SHARED-WORKFLOW','live refresh must preserve filters');
  const newerStore=(await api('integrations/connections',{body:{name:'Second shared outlet',marketplace:'boyner',mode:'demo',environment:'stage'}})).body;
  await page.waitForFunction(id=>data['integrations/connections'].some(c=>c.id===id),newerStore.id,{timeout:15000});
  for(const module of modules){await page.evaluate(id=>nav(id),module);await page.waitForFunction(id=>wuState(id).result,module);await page.setViewportSize({width:390,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),module+' must fit mobile');}
  await page.evaluate(()=>nav('users'));await page.waitForFunction(()=>localSystem&&!localSystemLoading);
  const setupForm=page.locator('.local-account-form');await setupForm.locator('input[name=name]').fill('Invalid account name');
  await setupForm.locator('input[name=password]').fill('Draft-password-2026');await setupForm.locator('input[name=confirmation]').fill('Draft-password-2026');
  const rejectedSetup=page.waitForResponse(r=>r.url().endsWith('/api/access/setup')&&r.request().method()==='POST');
  await setupForm.getByRole('button',{name:'Yönetici hesabını oluştur',exact:true}).click();assert.equal((await rejectedSetup).status(),400);
  await sourceProduct(base,{...product,price:252,warehouseCode:'MAIN'});await page.waitForTimeout(5500);
  assert.equal(await setupForm.locator('input[name=name]').inputValue(),'Invalid account name','a rejected form must survive unrelated refreshes');
  assert.deepEqual(errors,[]);
  const admin=await api('access/setup',{body:{name:'whole-admin',password:'Whole-test-password-2026'}});
  assert.equal((await api('workspace/views/invoices')).status,401);
  await api('access/users',{cookie:admin.cookie,body:{name:'whole-viewer',password:'Viewer-test-password-2026',role:'viewer'}});
  const viewer=await api('access/login',{body:{name:'whole-viewer',password:'Viewer-test-password-2026'}});
  assert.equal((await api('workspace/views/invoices',{cookie:viewer.cookie})).status,200);
  assert.equal((await api('workspace/records',{cookie:viewer.cookie,body:localRecord})).status,403);
  assert.equal((await api('workspace/orders',{cookie:viewer.cookie,body:draft})).status,403);
  console.log('PASS shared application: 15 connected workflows, cross-screen order/document/shipment identities, ERP stock isolation across local order/status flows, idempotency, native questions/claims/finance, source isolation, restart persistence, global store revision, live refresh, preserved forms/filters, mobile and viewer guards.');
 }finally{if(browser)await browser.close();await stop();const absolute=path.resolve(temp);if(!absolute.startsWith(path.resolve(os.tmpdir())+path.sep)||!path.basename(absolute).startsWith('marketpilot-whole-workspace-'))throw Error('Unsafe cleanup');fs.rmSync(absolute,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1});
