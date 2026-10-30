// Offline fixture setup only. These are MarketPilot canonical mock fields, not Nebim schema.
function setAuthority(data,product,amount){
 const now=new Date().toISOString(),variant='fixture-variant-'+product.id,listId='fixture-retail';
 product.erpVariantId=variant;product.erpProfileId='mock-nebim';product.erpProductId='fixture-product-'+product.id;
 data.pricing??={rules:{},lists:{},variantSources:{},missingLists:[],proposals:[],ruleVersion:0};
 const s=data.pricing;s.variantSources??={};s.variantSources[variant]=variant;s.missingLists=[];s.lastSourceReadAt=now;
 s.policy={masterPriceListId:listId,allowedCurrencies:['TRY'],alwaysRequireApproval:true,approvalChangePercent:5,previewMinutes:10,sourceMaxAgeMinutes:60};
 s.lists[listId]={id:listId,name:'Mock authoritative fixture list',context:{tenantId:'default',sourceSystem:'mock-nebim-v3',sourceRecordId:listId,externalAccountId:null,correlationId:'fixture',idempotencyKey:'fixture-list-'+now,sourceVersion:(s.lists[listId]?.context.sourceVersion||0)+1,occurredAt:now,receivedAt:now},validFrom:'2026-01-01T00:00:00Z',validTo:null,entries:[{variantId:variant,amount,currency:'TRY',unitCode:'EA',vatIncluded:true,vatRate:10}]};
}
module.exports={setAuthority};
