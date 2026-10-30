const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),net=require('node:net'),assert=require('node:assert/strict'),{spawn}=require('node:child_process');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
(async()=>{
 const root=path.resolve(__dirname,'..'),temp=fs.mkdtempSync(path.join(os.tmpdir(),'marketpilot-orchestrator-'));
 const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
 const base='http://127.0.0.1:'+port,apiPath='/api/sync/enterprise';let child;
 const start=()=>child=spawn('dotnet',[path.join(root,'src/MarketPilot.Web/bin/Debug/net8.0/MarketPilot.Web.dll'),'--contentRoot',temp,'--webroot',path.join(root,'src/MarketPilot.Web/wwwroot'),'--urls',base],{windowsHide:true,stdio:'ignore',env:{...process.env,ASPNETCORE_ENVIRONMENT:'Production',Logging__LogLevel__Default:'Critical',Synchronization__IncludeRealStores:'false',Synchronization__AllowRealWrites:'false',Wms__AllowMockWrites:'true',Wms__Mock__AmbiguousSubmission:'true',Enterprise__Nebim__AllowMockWrites:'true'}});
 async function stop(){if(child.exitCode!==null)return;const done=new Promise(r=>child.once('exit',r));child.kill();await done;}
 async function api(url,body,method='POST'){const response=await fetch(base+apiPath+url,{method:body===undefined?'GET':method,headers:{'Content-Type':'application/json',Origin:base},body:body===undefined?undefined:JSON.stringify(body)});return{status:response.status,body:await response.json().catch(()=>null)};}
 async function ready(){for(let i=0;i<100;i++){try{if((await api('/integration/events')).status===200)return;}catch{}await sleep(100);}throw Error('server startup');}
 const at='2026-01-01T00:00:00+00:00';
 function order(id){return{packageId:id,orderNumber:'ORDER-'+id,status:'Created',orderDate:at,customerName:'MOCK Customer',customerEmail:'mock@example.invalid',total:180,grossTotal:200,discount:20,currency:'TRY',paymentMethod:'MOCK',deliveryAddressType:'Shipment',shipmentAddress:null,
   lines:[{id:'L1',sku:'SKU1',barcode:'BC1',name:'MOCK item',quantity:2,unitPrice:100,grossAmount:200,vatRate:10,currency:'TRY',status:'Created',categoryId:''}],cargo:{provider:'',trackingNumber:'',trackingLink:'',senderNumber:'',shipmentNumber:'',deci:null,boxQuantity:null},invoice:{number:'',status:'',link:'',commercial:false,company:'',taxOffice:'',taxNumber:'',eInvoiceAvailable:null,rejectedReasons:[]},lastModifiedDate:1,fulfilledBy:null,promotions:[{id:'CAMPAIGN1',description:'MOCK campaign',amount:20,currency:'TRY',fundedBy:'marketplace',lineId:null}]};}
 const policy={tenantId:'default',accountId:'fixture-account',approved:true,salesChannelCode:'ERP-MARKETPLACE',customerPolicy:'fixed-account',fixedCustomerCode:'MOCK-CARI',customers:{},currencies:['TRY'],vatRates:[10],variants:[{sku:'SKU1',barcode:'BC1',erpProductCode:'ERP1',erpVariantCode:'ERP1-S'}],channel:'trendyol',unitPricesIncludeVat:true};
 function event(id,scenario='success',operation='create',original=null){return{tenantId:'default',accountId:'fixture-account',eventId:id,correlationId:'corr-'+id,eventType:operation==='cancel'?'OrderCancelled':operation==='return'?'ReturnAccepted':'MarketplaceOrderReceived',sourceSystem:'mock-marketplace',sourceVersion:1,targetSystem:'erp',occurredAt:at,
   payload:{tenantId:'default',accountId:'fixture-account',channel:'trendyol',sourceOrderId:operation==='create'?id:'success',operation,operationId:id,order:order(operation==='create'?id:'success'),customerIdentity:null,originalAcceptanceReference:original,mockScenario:scenario}};}
 async function jobs(){return(await api('/integration/events')).body;}
 async function wait(id,states,limit=400){for(let i=0;i<limit;i++){const j=(await jobs()).find(j=>j.integrationEvent.eventId===id);if(j&&states.includes(j.state))return j;await sleep(100);}throw Error('state timeout '+id+': '+JSON.stringify((await jobs()).find(j=>j.integrationEvent.eventId===id)));}
 async function retry(id){const j=(await jobs()).find(j=>j.integrationEvent.eventId===id);assert.equal((await api('/integration/events/'+j.id+'/retry',{})).status,202);}
 try{
  start();await ready();
  const matrices=(await api('/channel-contracts')).body;assert.equal(matrices.length,6);assert(matrices.every(x=>x.unsupportedOperations.includes('create-product')));
  assert.equal((await api('/erp-transfer/policy',policy,'PUT')).status,200);
  assert.equal((await api('/erp-transfer/policy',{...policy,customerPolicy:'existing-customer'},'PUT')).status,200);
  assert.equal((await api('/erp-transfer/preview',event('missing-customer').payload)).status,400);
  assert.equal((await api('/erp-transfer/policy',policy,'PUT')).status,200);
  let request=event('preview').payload;const preview=await api('/erp-transfer/preview',request);assert.equal(preview.status,200);assert.equal(preview.body.plan.discount,20);assert.equal(preview.body.plan.sourceOrder.promotions[0].id,'CAMPAIGN1');
  for(const bad of [{...request,channel:'amazon'},{...request,order:{...request.order,currency:'USD'}},{...request,order:{...request.order,total:181}},{...request,order:{...request.order,lines:[{...request.order.lines[0],vatRate:null}]}},{...request,order:{...request.order,lines:[{...request.order.lines[0],sku:'unknown'}]}}])assert.equal((await api('/erp-transfer/preview',bad)).status,400);
  for(const scenario of ['success','rejected','pending','timeout','unavailable','mismatch'])assert.equal((await api('/integration/events',event(scenario,scenario))).status,202);
  // Capture transient states while other jobs are still queued; the worker may reconcile them automatically.
  const pending=await wait('pending',['waiting']);assert.equal(pending.externalReference,null);assert.equal(JSON.parse(pending.result).acceptanceReference,null);
  await wait('timeout',['uncertain']);
  const accepted=await wait('success',['completed']);assert.equal(JSON.parse(accepted.result).reconciled,true);assert(accepted.processedAt);
  assert.equal((await wait('rejected',['intervention-required'])).attempts,1);
  assert.equal((await wait('mismatch',['reconciliation-required'])).attempts,1);
  for(const id of ['pending','timeout']){
    for(let i=0;i<3;i++){
      const current=(await jobs()).find(job=>job.integrationEvent.eventId===id);if(current.state==='completed')break;
      const response=await api('/integration/events/'+current.id+'/retry',{});
      if(response.status!==202)assert.equal((await jobs()).find(job=>job.id===current.id).state,'completed','Retry rejected before reconciliation');
      await wait(id,['waiting','completed']);
    }
    await wait(id,['completed']);
  }
  await wait('unavailable',['uncertain']);await retry('unavailable');const unavailable=await wait('unavailable',['uncertain']);assert.equal(unavailable.attempts,1);
  const before=(await jobs()).length;assert.equal((await api('/integration/events',event('success'))).body.id,accepted.id);assert.equal((await jobs()).length,before);
  assert.equal((await api('/integration/events',{...event('success'),sourceVersion:2})).status,409);
  assert.equal((await api('/integration/events',{...event('other'),tenantId:'other'})).status,403);
  const duplicate=event('duplicate');duplicate.payload.sourceOrderId='success';duplicate.payload.order=order('success');
  assert.equal((await api('/integration/events',duplicate)).status,202);await wait('duplicate',['completed']);
  const conflict=event('conflicting-order');conflict.payload.sourceOrderId='success';conflict.payload.order={...order('success'),total:170,discount:30};
  assert.equal((await api('/integration/events',conflict)).status,202);await wait('conflicting-order',['intervention-required']);
  const reference=JSON.parse(accepted.result).acceptanceReference;
  for(const operation of ['correct','cancel','return']){const id='op-'+operation;assert.equal((await api('/integration/events',event(id,'success',operation,reference))).status,202);await wait(id,['completed']);}
  const rejectedJob=(await jobs()).find(j=>j.integrationEvent.eventId==='rejected');
  const correction=event('reviewed-rejection-correction','success','correct');correction.payload.sourceOrderId='rejected';correction.payload.order=order('rejected');correction.payload.rejectedRequestKey=rejectedJob.transferPlan.requestKey;
  assert.equal((await api('/integration/events',correction)).status,202);await wait('reviewed-rejection-correction',['completed']);
  async function local(url,body,method='POST'){const r=await fetch(base+url,{method:body===undefined?'GET':method,headers:{'Content-Type':'application/json',Origin:base},body:body===undefined?undefined:JSON.stringify(body)});return{status:r.status,body:await r.json().catch(()=>null)};}
  assert.equal((await local('/api/erp/nebim/sync',{})).status,200);
  assert.equal((await local('/api/wms/inventory/policy',{safetyStockByWarehouse:{},warehouseAssignmentsByAccount:{},channelStockLimits:{}},'PUT')).status,200);
  assert.equal((await local('/api/wms/fulfillment/policy',{approved:true,writeSequence:['wms-fulfillment-submission'],warehousePriority:['mock-main'],allowPartialShipments:false,updateTargets:['marketplace','erp']},'PUT')).status,200);
  const isolatedWorkflow=(await local('/api/wms/fulfillment/orders',{marketplace:'trendyol',accountId:'fixture-account',externalOrderId:'out-of-order-fixture',idempotencyKey:'out-of-order-fixture',status:'Created',orderedAt:at,currency:'TRY',lines:[{externalLineId:'L1',sku:'TSH-EKR-S',quantity:1,unit:'EA',unitPrice:100,currency:'TRY'}]})).body;
  assert.equal(isolatedWorkflow.state,'validated');
  const premature={...event('premature-package'),eventType:'PackageCompleted',targetSystem:'wms',payload:{workflowId:isolatedWorkflow.id,sourceOrderId:'out-of-order-fixture'}};
  assert.equal((await api('/integration/events',premature)).status,202);await wait('premature-package',['blocked']);
  const stillIsolated=(await local('/api/wms/fulfillment/workflows/'+isolatedWorkflow.id)).body;
  assert.equal(stillIsolated.history.length,isolatedWorkflow.history.length);assert(stillIsolated.warehouses.every(task=>task.submissionState==='pending'));
  const wmsEvent={...event('wms-order'),targetSystem:'wms'};wmsEvent.payload.order.lines[0].sku='TSH-EKR-S';wmsEvent.payload.order.lines[0].barcode='8691000000001';
  assert.equal((await api('/integration/events',wmsEvent)).status,202);
  let wmsJob;
  for(let i=0;i<5;i++){wmsJob=await wait('wms-order',['waiting','completed','blocked']);if(wmsJob.state==='completed')break;assert.notEqual(wmsJob.state,'blocked',JSON.stringify(wmsJob));await retry('wms-order');}
  assert.equal(wmsJob.state,'completed');assert.equal(JSON.parse(wmsJob.result).accepted,true);assert.equal(JSON.parse(wmsJob.result).processed,false);
  const beforeRestartWorkflow=(await local('/api/wms/fulfillment/workflows/'+JSON.parse(wmsJob.result).workflowId)).body;
  assert(beforeRestartWorkflow.history.some(h=>h.state==='wms-outcome-unknown'),'mock WMS timeout was not exercised');
  await stop();start();await ready();
  const recoveredAcceptance=await local('/api/wms/fulfillment-orders/'+beforeRestartWorkflow.warehouses[0].externalRequestId+'/acceptance');
  assert.equal(recoveredAcceptance.status,200);assert.equal(recoveredAcceptance.body.data.status,'accepted','WMS receipt must survive restart');
  policy.variants.push({sku:'TSH-EKR-S',barcode:'8691000000001',erpProductCode:'ERP-TSH',erpVariantCode:'ERP-TSH-S'});
  assert.equal((await api('/erp-transfer/policy',policy,'PUT')).status,200);
  for(const [id,sequence] of [['erp-first',['erp-order-registration','wms-fulfillment-submission']],['wms-first',['wms-fulfillment-submission','erp-order-registration']]]){
    assert.equal((await local('/api/wms/fulfillment/policy',{approved:true,writeSequence:sequence,warehousePriority:['mock-main'],allowPartialShipments:false,updateTargets:['marketplace','erp']},'PUT')).status,200);
    const e={...event(id),targetSystem:'wms'};e.payload.order.lines[0].sku='TSH-EKR-S';e.payload.order.lines[0].barcode='8691000000001';
    assert.equal((await api('/integration/events',e)).status,202);
    let j;for(let i=0;i<9;i++){j=await wait(id,['waiting','completed','blocked']);assert.notEqual(j.state,'blocked',JSON.stringify(j));if(j.state==='completed')break;await retry(id);await sleep(100);}
    assert.equal(j.state,'completed',id);assert(j.externalReference);
    const workflow=(await local('/api/wms/fulfillment/workflows/'+JSON.parse(j.result).workflowId)).body;
    const erpIndex=workflow.history.findIndex(h=>h.state==='erp-registered'),wmsIndex=workflow.history.findIndex(h=>h.state==='wms-accepted');
    assert(erpIndex>=0&&wmsIndex>=0);assert(id==='erp-first'?erpIndex<wmsIndex:wmsIndex<erpIndex,'Business sequence bypassed');
  }
  const wmsFlowId=JSON.parse(wmsJob.result).workflowId;
  for(let i=0;i<150;i++){if((await jobs()).some(j=>j.integrationEvent.eventType==='ShipmentDispatched'&&j.integrationEvent.payload.workflowId===wmsFlowId))break;await sleep(100);}
  assert((await jobs()).some(j=>j.integrationEvent.eventType==='ShipmentDispatched'&&j.integrationEvent.payload.workflowId===wmsFlowId),'WMS lifecycle did not reach shipment');
  const beforeReplay=(await local('/api/wms/fulfillment/workflows/'+wmsFlowId)).body;
  const lifecycleReplay={...event('lifecycle-replay'),eventType:'WmsOrderAccepted',targetSystem:'wms',payload:{workflowId:wmsFlowId,sourceOrderId:'wms-order'}};
  assert.equal((await api('/integration/events',lifecycleReplay)).status,202);await wait('lifecycle-replay',['completed']);
  assert.equal((await local('/api/wms/fulfillment/workflows/'+wmsFlowId)).body.history.length,beforeReplay.history.length,'Replay advanced an already completed stage');
  assert.equal((await api('/erp-transfer/policy',{...policy,approved:false},'PUT')).status,200);
  const guarded={...event('erp-preparation-guard'),targetSystem:'wms'};guarded.payload.order.lines[0].sku='TSH-EKR-S';guarded.payload.order.lines[0].barcode='8691000000001';
  assert.equal((await api('/integration/events',guarded)).status,202);await wait('erp-preparation-guard',['blocked']);
  const guardedWorkflow=(await local('/api/wms/fulfillment/workflows')).body.find(w=>w.order.externalOrderId==='erp-preparation-guard');assert(guardedWorkflow);
  const manualAdvance=(await local('/api/wms/fulfillment/workflows/'+guardedWorkflow.id+'/advance',{})).body;
  assert.equal(manualAdvance.state,'blocked-erp-preparation');assert(manualAdvance.warehouses.every(w=>w.submissionState==='pending'));
  assert.equal((await api('/erp-transfer/policy',policy,'PUT')).status,200);
  const stock={...event('stock-event'),accountId:'trendyol',eventType:'StockChanged',targetSystem:'marketplace',payload:{barcode:'8691000000001',quantity:3}};
  assert.equal((await api('/integration/events',stock)).status,202);let stockJob=await wait('stock-event',['waiting','completed']);
  if(stockJob.state!=='completed'){await retry('stock-event');stockJob=await wait('stock-event',['completed']);}assert(JSON.parse(stockJob.result).outboxId);
  const stale={...stock,eventId:'stale-stock',sourceVersion:0};assert.equal((await api('/integration/events',stale)).body.result,'stale-source-version');
  const stockConflict={...stock,eventId:'conflicting-stock',payload:{...stock.payload,quantity:4}};assert.equal((await api('/integration/events',stockConflict)).body.result,'source-version-conflict');
  const listingProduct={id:'EXISTING-LISTING',contentId:'C1',productMainId:'M1',sku:'SKU1',barcode:'BC1',name:'MOCK item',categoryId:'',categoryName:'',brandName:'',listPrice:100,salePrice:100,customerPrice:null,stock:0,currency:'TRY',onSale:true,productUrl:'',images:[],approvalStatus:'approved',rejectReasons:[],vatRate:10,vatIncluded:true};
  assert.equal((await api('/integration/events',{...event('listing-preparation'),eventType:'ProductChanged',targetSystem:'catalog',payload:{accountId:'fixture-account',product:listingProduct}})).status,202);
  const listingJob=await wait('listing-preparation',['completed']);assert.equal(JSON.parse(listingJob.result).state,'publication-preparation');
  // Poller acceptance is durable before its watermark, even without ERP mappings.
  assert.equal((await local('/api/sync/automation',{enabled:true,intervalSeconds:30,stockEnabled:false,lookbackDays:2})).status,200);
  let observed=false;for(let i=0;i<60;i++){if((await jobs()).some(j=>j.integrationEvent.sourceSystem.includes('demo')&&j.integrationEvent.eventType==='MarketplaceOrderReceived')){observed=true;break;}await sleep(100);}assert(observed,'OrderSynchronization did not route orders');
  assert.equal((await local('/api/sync/automation',{enabled:false,intervalSeconds:30,stockEnabled:false,lookbackDays:2})).status,200);
  for(const type of ['ProductChanged','PriceChanged','StockChanged','MarketplaceOrderReceived','ErpOrderAccepted','WmsOrderAccepted','StockReserved','PickCompleted','PackageCompleted','ShipmentDispatched','OrderCancelled','ReturnAccepted']){
    const e={...event('audit-'+type),eventType:type,targetSystem:'audit',payload:{fixture:true}};assert.equal((await api('/integration/events',e)).status,202);
  }
  await wait('audit-ReturnAccepted',['completed'],1200);
  // Price changes never escape the existing price approval workflow.
  const price={...event('price-guard'),eventType:'PriceChanged',targetSystem:'marketplace',payload:{price:1}};
  assert.equal((await api('/integration/events',price)).status,202);await wait('price-guard',['blocked']);
  const state=JSON.parse(fs.readFileSync(path.join(temp,'Data/enterprise-sync.json'),'utf8'));
  assert(Object.values(state.mockErpTransferReceipts).every(r=>r.submissions===1));
  assert(Object.values(state.mockErpTransferReceipts).some(r=>r.queries>0));
  assert.equal(state.jobs.find(j=>j.id===accepted.id).transferPlan.sourceOrder.promotions[0].id,'CAMPAIGN1');
  const count=(await jobs()).length;await stop();start();await ready();assert.equal((await jobs()).length,count);assert.equal((await jobs()).find(j=>j.id===accepted.id).state,'completed');
  await retry('unavailable');await wait('unavailable',['uncertain']);
  console.log('PASS ERP transfer + IntegrationOrchestrator HTTP: mappings, VAT/currency/discount/promotion preservation, success/rejected/pending/timeout/unavailable/mismatch, duplicate/conflict, reviewed rejection, separate correction/cancel/return, WMS acceptance vs processing, StockOutbox delegation, OrderSynchronization intake, all 12 event envelopes, tenant checks, restart persistence and query-before-retry. Existing enterprise queue only; mock providers only.');
 }finally{await stop();const resolved=path.resolve(temp);assert.equal(path.dirname(resolved),path.resolve(os.tmpdir()));assert(path.basename(resolved).startsWith('marketpilot-orchestrator-'));fs.rmSync(resolved,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
