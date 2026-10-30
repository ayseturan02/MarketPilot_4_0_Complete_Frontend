const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const root = path.resolve(__dirname, '../src/MarketPilot.Web/wwwroot');
const app = {innerHTML:'',focus(){}};
const context = vm.createContext({
  document: {querySelector: () => app},
  localStorage: {getItem: () => null},
  Intl, Date, console, URLSearchParams, setInterval:()=>0
});
vm.runInContext(fs.readFileSync(path.join(root,'assets/integrations.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/trendyol/trendyol-api.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/trendyol/trendyol-workflows.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/product-store-stocks.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/trendyol/trendyol-demo-workspace.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/hepsiburada/hepsiburada-center.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/hepsiburada/hepsiburada-details.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/amazon/amazon-center.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/n11/n11-center.js'),'utf8'),context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/boyner/boyner-center.js'),'utf8'),context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/ticimax/ticimax-center.js'),'utf8'),context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/wms-inventory.js'),'utf8'),context);
const source = fs.readFileSync(path.join(root,'assets/app.js'),'utf8');
// Skip network bootstrap; exercise the same page render functions with explicit fixtures.
vm.runInContext(source.slice(0,source.lastIndexOf('(async()=>')), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/wms-fulfillment.js'),'utf8'), context);
vm.runInContext(`
  if(Object.keys(names).length !== 42) throw new Error('42 menu entries must remain');
  data.connections=[]; data.orders=[];
  data['integrations/readiness']={trendyol:{stage:false,production:false}};
  data['integrations/connections']=Object.keys(marketplaceLabels).map((marketplace,i)=>({
    id:marketplace, settings:{name:'<script>unsafe</script>',marketplace,mode:i===0?'real':'demo',environment:'stage'},
    status:i===0?'untested':'demo', message:'Ready', lastOperation:null
  }));
  view='connections'; render();
`, context);
assert(app.innerHTML.includes('GERÇEK API') && app.innerHTML.includes('DEMO'));
app.scrollTop=321;vm.runInContext(`render()`,context);assert.equal(app.scrollTop,321);
assert(app.innerHTML.includes('İnternet Tekstil 5.1') && app.innerHTML.includes('Mağaza bilgileri'));
assert(app.innerHTML.includes('Profil / anahtarlar eksik') && app.innerHTML.includes('Kurulum adımları'));
const profileBadge=vm.runInContext(`(()=>{const previous=data['integrations/readiness'];try{data['integrations/readiness']={trendyol:{production:false,stage:false},profiles:[{marketplace:'trendyol',environment:'production',configured:true}]};return integrationReadiness();}finally{data['integrations/readiness']=previous;}})()`,context);
assert(profileBadge.includes('Üretim: Hazır profil var'),'Profile readiness must not depend on legacy environment credentials');

assert(app.innerHTML.includes('&lt;script&gt;unsafe&lt;/script&gt;'));
assert(!app.innerHTML.includes('<script>unsafe</script>'));
assert(app.innerHTML.includes('Mağaza ekle') && app.innerHTML.includes('Ürünler'));
vm.runInContext(`
  integrationResults.trendyol={operation:'products',query:{size:'20',status:'onSale'},result:{page:0,size:20,totalElements:1,totalPages:1,items:[{id:'v1',contentId:'c1',productMainId:'m1',sku:'SKU',barcode:'BC',name:'Detaylı ürün',categoryId:'5',categoryName:'Elektronik',brandName:'Marka',listPrice:120,salePrice:100,customerPrice:95,stock:0,currency:'TRY',onSale:true,productUrl:'https://example.com/product',images:['https://example.com/image.jpg']}]}};
  view='connections';render();
`,context);
assert(app.innerHTML.includes('Detaylı ürün') && app.innerHTML.includes('Elektronik') && app.innerHTML.includes('Filtrele'));
vm.runInContext(`integrationProductDetail('trendyol',0)`,context);
assert(app.innerHTML.includes('Müşteri fiyatı') && app.innerHTML.includes('Stok</small><strong>0'));
vm.runInContext(`
  modal=null;integrationResults.trendyol={operation:'orders',query:{size:'20'},result:{page:0,size:20,totalElements:1,totalPages:1,items:[{packageId:'p1',orderNumber:'o1',status:'Created',orderDate:'2026-10-26T10:00:00Z',customerName:'Müşteri',customerEmail:'m@example.com',total:100,grossTotal:120,discount:20,currency:'TRY',paymentMethod:'Kredi Kartı',deliveryAddressType:'Shipment',shipmentAddress:{fullAddress:'Adres'},lines:[{name:'Ürün',sku:'SKU',barcode:'BC',quantity:1,unitPrice:100,currency:'TRY'}],cargo:{provider:'Trendyol Express',trackingNumber:'TR1',trackingLink:'https://example.com/track',shipmentNumber:'S1',boxQuantity:1,deci:2},invoice:{number:'F1',status:'Invoiced',link:'https://example.com/invoice',commercial:false,company:'',taxOffice:'',taxNumber:'',eInvoiceAvailable:false,rejectedReasons:[]}}]}};
  render();integrationOrderDetail('trendyol',0);
`,context);
assert(app.innerHTML.includes('Kargo') && app.innerHTML.includes('Fatura') && app.innerHTML.includes('Trendyol Express'));
assert(vm.runInContext(`safeExternalUrl('javascript:alert(1)')`,context)==='');
assert(vm.runInContext(`integrationHasNext({page:99,size:100,totalPages:120,nextPageToken:'opaque'})`,context)===true);
assert(vm.runInContext(`integrationHasNext({page:0,size:200,totalPages:null,hasMore:true,nextCursor:'cursor'})`,context)===true);
vm.runInContext(`modal=null;view='n11';ncState(ncCurrent(),'products').result={items:[]};ncState(ncCurrent(),'orders').result={items:[]};ncWorkspaces.set('n11',{value:{jobs:[]}});render();`,context);
assert(app.innerHTML.includes('n11 Merkezi')&&app.innerHTML.includes('n11depom')&&!app.innerHTML.includes('Yeni taslak'));
vm.runInContext(`integrationForm('n11');`,context);assert(app.innerHTML.includes('value="n11" selected')&&app.innerHTML.includes('name="marketplace" disabled'));
vm.runInContext(`modal=null;view='hepsiburada';hbWorkspaces.set('hepsiburada',{value:{products:[],orders:[],claims:[],jobs:[]}});render();`,context);
assert.deepEqual(JSON.parse(vm.runInContext("JSON.stringify(hbTabs.map(t=>t[1]))",context)),["Genel bakış","Ürünler","Siparişler","Stok ve fiyat","Kargo","İadeler","Finans","Marka ve kategori","Fatura ve garanti","Etiketler","Adresler","İşlem geçmişi"]);
assert(app.innerHTML.includes('Hepsiburada Merkezi')&&!app.innerHTML.includes('Yeni taslak')&&!app.innerHTML.includes('JSON'));
assert(app.innerHTML.includes('Stok ve fiyat')&&app.innerHTML.includes('İşlem geçmişi'));
vm.runInContext(`integrationForm('hepsiburada');`,context);
assert(app.innerHTML.includes('name="marketplace" disabled')&&app.innerHTML.includes('value="hepsiburada" selected'));
vm.runInContext(`modal=null;view='amazon';amState(amCurrent(),'products').result={items:[]};amState(amCurrent(),'orders').result={items:[]};amWorkspaces.set('amazon',{value:{jobs:[]}});render();`,context);
assert(app.innerHTML.includes('Amazon Merkezi')&&app.innerHTML.includes('Amazon depoları (FBA)')&&app.innerHTML.includes('Raporlar'));
assert(!app.innerHTML.includes('Yeni taslak')&&!app.innerHTML.includes('JSON'));
vm.runInContext(`integrationForm('amazon');`,context);assert(app.innerHTML.includes('value="amazon" selected')&&app.innerHTML.includes('name="marketplace" disabled'));
vm.runInContext(`modal=null;view='boyner';bnState(bnCurrent(),'products').result={items:[]};bnState(bnCurrent(),'orders').result={items:[]};bnWorkspaces.set('boyner',{value:{jobs:[]}});render();`,context);
assert(app.innerHTML.includes('Boyner Merkezi')&&!app.innerHTML.includes('Yeni taslak'));
vm.runInContext(`modal=null;view='ticimax';tcState(tcCurrent(),'products').result={items:[]};tcState(tcCurrent(),'orders').result={items:[]};tcWorkspaces.set('ticimax',{value:{jobs:[]}});render();`,context);assert(app.innerHTML.includes('Ticimax Merkezi'));
vm.runInContext(`integrationForm('boyner');`,context);assert(app.innerHTML.includes('value="boyner" selected')&&app.innerHTML.includes('name="marketplace" disabled'));
const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
assert(html.indexOf('/assets/integrations.js') < html.indexOf('/assets/app.js') && html.includes('/assets/trendyol/trendyol-api.js'));


vm.runInContext(`modal=null;view='trendyol';tyStore='trendyol';tyData['trendyol:products:0']={result:{items:[],page:0,totalPages:1}};tyData['trendyol:orders:0']={result:{items:[],page:0,totalPages:1}};render();`,context);
assert(app.innerHTML.includes('Trendyol Merkezi'));
assert(!app.innerHTML.includes('JSON')&&!app.innerHTML.includes('Dokümantasyon'));
assert(!app.innerHTML.includes('Yeni taslak')&&!app.innerHTML.includes('ty-form'));
assert(app.innerHTML.includes('0 bağlı mağaza'));
vm.runInContext(`tyTab='products';render();`,context);
assert(app.innerHTML.includes('Gösterilecek kayıt bulunmuyor.'));
vm.runInContext(`integrationForm('trendyol');`,context);
assert(app.innerHTML.includes('name="marketplace" disabled'));
const wmsHtml=vm.runInContext(`(()=>{
  wmsInventoryData={
    profile:{providerKey:'mock',tenantId:'default'},
    capabilities:{isMock:true},
    policy:{safetyStockByWarehouse:{'mock-main':2},warehouseAssignmentsByAccount:{trendyol:['mock-main']},channelStockLimits:{trendyol:10}},
    approvedSellableRule:'strict-deduction',ruleApprovedAt:'2026-10-29T10:00:00Z',
    warehouses:[{id:'mock-main',code:'MAIN',name:'Ana Depo',isActive:true}],
    stock:[
      {warehouse:{id:'mock-main',code:'MAIN',name:'Ana Depo'},snapshot:{warehouseId:'mock-main',sku:'SKU-1',onHandQuantity:20,reservedQuantity:3,damagedQuantity:1,blockedQuantity:0,returnInspectionQuantity:0,allocatedQuantity:0,inTransferQuantity:0,unit:'EA',capturedAt:'2026-10-29T10:00:00Z',sourceVersion:2},safetyStock:2,sellableQuantity:14,sellableState:'known'},
      {warehouse:{id:'mock-main',code:'MAIN',name:'Ana Depo'},snapshot:{warehouseId:'mock-main',sku:'FBA-SKU',onHandQuantity:8,reservedQuantity:0,unit:'EA',capturedAt:'2026-10-29T10:00:00Z',providerManaged:true,providerName:'Amazon FBA'},sellableQuantity:null,sellableState:'provider-managed'}
    ],
    movements:[],history:[],discrepancies:[{warehouseCode:'MAIN',sku:'SKU-1',wmsQuantity:20,erpQuantity:18,wmsUnit:'EA',erpUnit:'EA',state:'conflict',wmsSourceTime:'2026-10-29T10:00:00Z'}],
    channelStock:[],marketplaceAccounts:[{id:'trendyol',marketplace:'trendyol',name:'Trendyol'}],readAt:'2026-10-29T10:01:00Z'
  };
  view='inventory';return inventory();
})()`,context);
assert(wmsHtml.includes('Ana Depo')&&wmsHtml.includes('SKU-1')&&wmsHtml.includes('14'));
assert(wmsHtml.includes('Amazon FBA')&&wmsHtml.includes('İşletme onaylı'));
assert(wmsHtml.includes('Fark inceleme')&&wmsHtml.includes('WMS fiziksel'));
assert(wmsHtml.includes('Atanmış depolar')&&wmsHtml.includes('Kanal stok üst limiti'));
assert(wmsHtml.includes('stok göndermez'),'WMS inventory UI must not imply a stock write.');
assert(html.indexOf('/assets/wms-inventory.js') < html.indexOf('/assets/app.js'));
assert(html.includes('/assets/wms-fulfillment.js') && html.indexOf('/assets/app.js') < html.indexOf('/assets/wms-fulfillment.js'));
const fulfillmentHtml=vm.runInContext(`(()=>{
  wmsFulfillment={
    policy:{approved:false,writeSequence:['wms-fulfillment-submission'],warehousePriority:['mock-main','mock-outlet'],allowPartialShipments:true},
    warehouses:[{id:'mock-main',code:'MAIN',name:'Ana Depo'},{id:'mock-outlet',code:'OUT',name:'Ek Depo'}],
    workflows:[{
      id:'wf-1',externalRequestId:'req-1',idempotencyKey:'idem-1',state:'wms-outcome-unknown',
      order:{externalOrderId:'order-1',marketplace:'trendyol',accountId:'seller-1'},
      warehouses:[{warehouseId:'mock-main',externalRequestId:'wms-req-1',idempotencyKey:'wms-idem-1',submissionState:'unknown',acceptanceState:'pending',reservationState:'pending',pickingState:'pending',packingState:'pending'}],
      packages:[{packageId:'pkg-1',lines:[{sku:'SKU-1',quantity:1}],carrier:'Mock Cargo',trackingNumber:'TRK-1',shippedAt:null}],
      publications:[],history:[{at:'2026-10-29T10:00:00Z',state:'wms-outcome-unknown',message:'Submission timed out',externalRequestId:'wms-req-1'}],
      updatedAt:'2026-10-29T10:00:00Z',blockReason:'Submission outcome unknown'
    }],
    selectedId:'wf-1',error:'',loading:false,busy:false
  };
  view='wms-fulfillment';render();return app.innerHTML;
})()`,context);
assert(fulfillmentHtml.includes('WMS Fulfillment')&&fulfillmentHtml.includes('İşletme fulfillment politikası'));
assert(fulfillmentHtml.includes('Pazaryeri siparişini içeri al')&&fulfillmentHtml.includes('Siparişi doğrula ve al'));
assert(fulfillmentHtml.includes('Sipariş satırı ekle')&&fulfillmentHtml.includes('Sipariş satırı 1'));
assert(fulfillmentHtml.includes('Sonucu güvenle sorgula')&&fulfillmentHtml.includes('Kabul')&&fulfillmentHtml.includes('Rezervasyon'));
assert(fulfillmentHtml.includes('TRK-1')&&fulfillmentHtml.includes('Durum geçmişi'));
assert(fulfillmentHtml.includes('mock-main, mock-outlet')&&fulfillmentHtml.includes('WMS talebi → ERP sipariş kaydı'));
assert.equal(vm.runInContext("names['wms-fulfillment']",context),'WMS Fulfillment','Fulfillment must be reachable from the application menu.');
console.log('PASS: 42 menu entries, marketplace panels, WMS multi-warehouse inventory, provider-managed stock, discrepancy review, fulfillment orchestration and no-write policy.');
