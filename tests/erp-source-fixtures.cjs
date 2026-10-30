const {createHmac,randomUUID,createHash}=require('node:crypto');
const sourceVersions=new Map();
// The test ERP is an authenticated external source, not a manual product-creation bypass.
const key='isolated-erp-contract-fixture-key';
process.env.Enterprise__Erp__InboundKey=key;
process.env.Enterprise__Erp__DataMode='demo';
async function importSource(base,items,requestId=randomUUID(),secret=key){
  const body=JSON.stringify({requestId,items}),stamp=String(Math.floor(Date.now()/1000));
  return fetch(base+'/hooks/erp/catalog',{method:'POST',headers:{'Content-Type':'application/json','X-ERP-Timestamp':stamp,'X-ERP-Signature':createHmac('sha256',secret).update(stamp+'.'+body).digest('hex')},body});
}
async function sourceProduct(base,p){
  if(typeof p.currency!=='string'||!/^[A-Z]{3}$/.test(p.currency))throw new Error('ERP fixture must provide an explicit currency');
  if(typeof p.vatIncluded!=='boolean'&&p.vatIncluded!==null)throw new Error('ERP fixture must provide an explicit VAT-included value or null');
  let inventory=p.inventory;
  if(inventory===undefined){
    if(p.stockKnown===false)inventory={state:'Unknown',quantity:null};
    else if(typeof p.stock==='number')inventory={state:'Known',quantity:p.stock};
    else inventory={state:'NotProvided',quantity:null};
  }
  if(inventory.state!=='NotProvided'&&(typeof p.warehouseCode!=='string'||p.warehouseCode.trim().length===0))throw new Error('ERP stock fixture must name its source warehouse');
  const barcode=p.barcode||'FIXTURE-'+createHash('sha256').update(p.sku).digest('hex').slice(0,16);
  const prior=sourceVersions.get(p.sku)||0,sourceVersion=p.sourceVersion??Math.max(prior,Number(p.version)||0)+1;sourceVersions.set(p.sku,sourceVersion);
  const response=await importSource(base,[{sku:p.sku,barcode,name:p.name,stock:inventory.state==='Known'?inventory.quantity:null,inventory,warehouseCode:p.warehouseCode,price:p.price??null,currency:p.currency,images:p.images||[],category:p.category??'',brand:p.brand??'',description:p.description??'',vatIncluded:p.vatIncluded,sourceVersion,sourceUpdatedAt:p.sourceUpdatedAt||new Date().toISOString()}]);
  const result=await response.json();return {status:response.status,body:response.ok?result.products[0]:result};
}
module.exports={sourceProduct,importSource,key};
