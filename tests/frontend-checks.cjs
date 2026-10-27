const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const root = path.resolve(__dirname, '../src/MarketPilot.Web/wwwroot');
const app = {innerHTML:''};
const context = vm.createContext({
  document: {querySelector: () => app},
  localStorage: {getItem: () => null},
  Intl, Date, console
});
vm.runInContext(fs.readFileSync(path.join(root,'assets/integrations.js'),'utf8'), context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/trendyol/trendyol-api.js'),'utf8'), context);
vm.runInContext(`tyCatalog={environments:{stage:{configured:false,writes:false},production:{configured:true,writes:false}},panels:[
  {key:'orders',title:'Siparişler',description:'Paketler',operations:[{key:'orders.updatePackage',panel:'orders',title:'<b>Paket</b> statüsü',method:'PUT',path:'order/sellers/{sellerId}/shipment-packages/{packageId}',query:[],write:true,body:'Json',sampleBody:'{"status":"Picking"}',formFields:[],fileFields:[],stageOnly:false,note:'',doc:'paket-statü-bildirimi-updatepackage',pathParameters:['packageId']}]},
  {key:'claims',title:'İadeler',description:'İade',operations:[{key:'claims.list',panel:'claims',title:'İade listesi',method:'GET',path:'order/sellers/{sellerId}/claims',query:['claimItemStatus'],write:false,body:'None',formFields:[],fileFields:[],stageOnly:false,note:'',doc:'',pathParameters:[]}]}]};`, context);
const source = fs.readFileSync(path.join(root,'assets/app.js'),'utf8');
// Skip network bootstrap; exercise the same page render functions with explicit fixtures.
vm.runInContext(source.slice(0,source.lastIndexOf('(async()=>')), context);
vm.runInContext(`
  if(Object.keys(names).length !== 39) throw new Error('39 menu entries must remain');
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
assert(app.innerHTML.includes('Seller ID / anahtarlar eksik') && app.innerHTML.includes('Kurulum adımları'));
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
for(const marketplace of ['trendyol','hepsiburada','n11','amazon']) {
  vm.runInContext(`view='${marketplace}';render();`,context);
  assert(app.innerHTML.includes('İnternet Tekstil 5.1'));
  assert(app.innerHTML.includes('Yeni taslak'));
}
vm.runInContext(`view='trendyol';tyPanel='orders';tyOpenKey='orders.updatePackage';render();`,context);
assert(app.innerHTML.includes('Trendyol API Merkezi') && app.innerHTML.includes('2 panel · 2 servis') && app.innerHTML.includes('Yazma'));
assert(app.innerHTML.includes('&lt;b&gt;Paket&lt;/b&gt;') && !app.innerHTML.includes('<b>Paket</b>'));
assert(app.innerHTML.includes('name="path:packageId"') && app.innerHTML.includes('Önizle / Gönder') && app.innerHTML.includes('&quot;Picking&quot;'));
vm.runInContext(`tyResults['orders.updatePackage']={operation:'orders.updatePackage',sent:false,dryRun:true,reason:'Demo mod',method:'PUT',url:'https://stageapigw.trendyol.com/integration/order/sellers/DEMO/shipment-packages/1',requestBody:{status:'Picking'}};render();`,context);
assert(app.innerHTML.includes('Önizleme · gönderilmedi') && app.innerHTML.includes('Demo mod'));
vm.runInContext(`view='hepsiburada';render();`,context);
assert(!app.innerHTML.includes('Trendyol API Merkezi'));
const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
assert(html.indexOf('/assets/integrations.js') < html.indexOf('/assets/app.js') && html.includes('/assets/trendyol/trendyol-api.js'));
console.log('PASS: 39 menu entries, connection modes/actions, escaping, four marketplace panels, Trendyol API center, existing draft pages and script order.');
