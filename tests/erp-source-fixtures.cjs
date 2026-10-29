const {createHmac,randomUUID,createHash}=require('node:crypto');
// The test ERP is an authenticated external source, not a manual product-creation bypass.
const key='isolated-erp-contract-fixture-key';
process.env.Enterprise__Erp__InboundKey=key;
process.env.Enterprise__Erp__DataMode='demo';
async function importSource(base,items,requestId=randomUUID(),secret=key){
  const body=JSON.stringify({requestId,items}),stamp=String(Math.floor(Date.now()/1000));
  return fetch(base+'/hooks/erp/catalog',{method:'POST',headers:{'Content-Type':'application/json','X-ERP-Timestamp':stamp,'X-ERP-Signature':createHmac('sha256',secret).update(stamp+'.'+body).digest('hex')},body});
}
async function sourceProduct(base,p){
  const barcode=p.barcode||'FIXTURE-'+createHash('sha256').update(p.sku).digest('hex').slice(0,16);
  const response=await importSource(base,[{sku:p.sku,barcode,name:p.name,stock:p.stock??null,price:p.price??null,currency:p.currency||'TRY',images:p.images||[],category:p.category||'',brand:p.brand||'',description:p.description||'',vatIncluded:p.vatIncluded??true}]);
  const result=await response.json();return {status:response.status,body:response.ok?result.products[0]:result};
}
module.exports={sourceProduct,importSource,key};
