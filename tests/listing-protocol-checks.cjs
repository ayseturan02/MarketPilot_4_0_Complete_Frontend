const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const root=path.resolve(__dirname,'../src/MarketPilot.Web/wwwroot'),calls=[];
const context=vm.createContext({document:{querySelector:()=>({innerHTML:''})},localStorage:{getItem:()=>null},Intl,Date,Map,Set,Promise,URLSearchParams,AbortController,DOMException,console,
 FormData:class{constructor(form){return new Map(Object.entries(form.values));}},
 fetch:async(url)=>{const q=new URL('http://local'+url).searchParams;calls.push({url,q});const orders=url.includes('/orders?');return {ok:true,json:async()=>({result:orders?{items:[],hasMore:!q.get('nextCursor'),nextCursor:q.get('nextCursor')?null:'order-next'}:{items:Array.from({length:25},(_,i)=>({id:String(i),barcode:'B'+i,sku:'S'+i})),page:Number(q.get('page')||0),size:25,totalElements:100000,totalPages:4000,nextPageToken:'product-next'}})};}});
for(const file of ['assets/integrations.js','assets/trendyol/trendyol-api.js','assets/trendyol/trendyol-workflows.js','assets/product-store-stocks.js','assets/trendyol/trendyol-demo-workspace.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context);
const app=fs.readFileSync(path.join(root,'assets/app.js'),'utf8');vm.runInContext(app.slice(0,app.lastIndexOf('(async()=>')),context);
vm.runInContext(fs.readFileSync(path.join(root,'assets/listing-filters.js'),'utf8'),context);
const run=script=>vm.runInContext(script,context);const settle=async()=>{await run('Promise.all([...tyStoreQueues.values()])');await new Promise(setImmediate);};
(async()=>{
run(`render=()=>{};notice=message=>{};view='test';data['integrations/connections']=[{id:'store',settings:{marketplace:'trendyol',mode:'real',environment:'production'},status:'connected'}];tyStore='store';tyTab='products';tyLoad(tyCurrentStore(),'products');`);await settle();
run(`tyPage('products',1);tyLoad(tyCurrentStore(),'products');`);await settle();assert.equal(calls.at(-1).q.get('nextPageToken'),'product-next');assert.equal(calls.at(-1).q.get('size'),'25');
context.form={dataset:{scope:'store',tab:'products'},values:{barcode:'EXACT',size:'25',sort:'newest'}};
run(`lfApply({preventDefault(){},target:form});tyLoad(tyCurrentStore(),'products');`);await settle();assert.equal(calls.at(-1).q.get('page'),'0');assert.equal(calls.at(-1).q.get('barcode'),'EXACT');assert.equal(calls.at(-1).q.has('nextPageToken'),false);
run(`tyTab='orders';tyLoad(tyCurrentStore(),'orders');`);await settle();run(`tyPage('orders',1);tyLoad(tyCurrentStore(),'orders');`);await settle();assert.equal(calls.at(-1).q.get('nextCursor'),'order-next');assert.equal(calls.at(-1).q.get('stream'),'true');
run(`tyTab='products';var result=tyData[tyKey(tyCurrentStore(),'products')].result;result.items=Array.from({length:75},(_,i)=>({sku:'variant-'+i}));result.totalPages=1;result.nextPageToken=null;`);
assert.equal(run(`lfVisibleRows(result.items,'products').length`),25);const before=calls.length;run(`tyPage('products',1)`);assert.equal(run(`lfVisibleRows(result.items,'products')[0].sku`),'variant-25');assert.equal(calls.length,before);
run(`for(let i=0;i<150;i++)tyData['old-'+i]={result:{items:[]}};lfPrune(tyCurrentStore(),'products',tyKey(tyCurrentStore(),'products'));`);assert(run('Object.keys(tyData).length')<=80);
console.log('PASS: 100k metadata with bounded fetch, product/order cursors, filter cursor reset, variant subpages without refetch and bounded page cache. No network used.');
})().catch(e=>{console.error(e);process.exitCode=1;});
