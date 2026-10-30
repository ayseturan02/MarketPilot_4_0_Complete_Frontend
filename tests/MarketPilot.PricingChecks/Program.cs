using MarketPilot.Web.Pricing;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
static void Reject(Action action){try{action();}catch(IntegrationException){return;}throw new Exception("Expected pricing rejection");}
var now=DateTimeOffset.UtcNow;
var entry=new PriceEntry("variant",120,"TRY","EA",true,20);
var rule=new ChannelPriceRule("trendyol","retail","TRY",CommissionPercent:10,VariableCostPercent:10,FixedCostNet:20,MarkupPercent:10);
Check(PriceCalculator.Calculate(entry,rule,now).CalculatedAmount==198,"Net fees markup and gross VAT formula");
Check(PriceCalculator.Calculate(entry,rule with{PercentageCostBasis="gross"},now).CalculatedAmount==208.42m,"Explicit gross commission basis");
Check(PriceCalculator.Calculate(entry,rule with{VatIncluded=false},now).CalculatedAmount==165,"VAT excluded output");
Check(PriceCalculator.Calculate(entry,rule with{PercentageCostBasis="gross"},now).CalculatedAmount==208.42m,"Explicit gross fee basis");
Check(PriceCalculator.Calculate(entry,rule with{PercentageCostBasis="gross",CommissionPercent=90,VariableCostPercent=0},now).BlockReason!=null,"VAT and gross fee denominator must stay positive");
Reject(()=>RealPriceWrites.Deny(120));
Check(RealPriceWrites.HasPrice(System.Text.Json.JsonSerializer.SerializeToElement(new{items=new[]{new{salePrice=120}}})),"Nested native price payload detection");
Check(PriceCalculator.Calculate(entry with{VatIncluded=false,Amount=100},rule,now).CalculatedAmount==198,"VAT excluded ERP input");
Check(PriceCalculator.Calculate(null,rule,now).CalculatedAmount==null,"Unknown stays null");
Check(PriceCalculator.Calculate(entry with{Amount=null},rule,now).CalculatedAmount==null,"Explicit unknown stays null");
Check(PriceCalculator.Calculate(entry with{Currency="USD"},rule,now).BlockReason!=null,"No implicit conversion");
Check(PriceCalculator.Calculate(entry with{Amount=0},rule,now).BlockReason!=null,"Zero blocks");
Check(PriceCalculator.Calculate(entry with{VatRate=null},rule,now).BlockReason!=null,"Unknown VAT blocks");
Check(PriceCalculator.Calculate(entry,rule with{MinimumPrice=200},now).CalculatedAmount==null,"Minimum blocks without clipping");
Check(PriceCalculator.Calculate(entry,rule with{MaximumPrice=190},now).CalculatedAmount==null,"Maximum blocks without clipping");
Check(PriceCalculator.Calculate(entry,rule with{CommissionPercent=100},now).BlockReason!=null,"Invalid fee rule blocks without division by zero");
var campaign=new PriceCampaign("sale",2,10,now.AddMinutes(-1),now.AddMinutes(1));
Check(PriceCalculator.Calculate(entry,rule with{Campaigns=[campaign,campaign with{Id="low",Priority=1,DiscountPercent=50}]},now).CalculatedAmount==178.20m,"Highest priority only; no stacking");
Check(PriceCalculator.Calculate(entry,rule with{Campaigns=[campaign,campaign with{Id="tie"}]},now).BlockReason!=null,"Ambiguous campaign blocks");
Check(PriceCalculator.Calculate(entry,rule with{Campaigns=[campaign]},campaign.EndsAt).CalculatedAmount==198,"End time exclusive");
var folder=Path.Combine(Path.GetTempPath(),"marketpilot-pricing-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
 var env=new TestEnvironment{ContentRootPath=folder};var db=new DemoStore(env);var registry=new IntegrationRegistry(env);var demo=new DemoWorkspace(env);var hub=new CatalogHub(db,registry,demo);hub.Ensure();
 var mock=new MockNebimProvider(db,new("mock-nebim","default"));
 var provider=new TestProvider{Lists=(await mock.ReadPriceListsAsync()).Data.ToArray(),Products=(await mock.ReadProductsAsync()).Data.ToArray(),Variants=(await mock.ReadVariantsAsync()).Data.ToArray(),Barcodes=(await mock.ReadIdentifiersAsync()).Data.Barcodes.ToArray()};
 var catalog=new ErpCatalogReadModel(db,provider);catalog.Apply((await catalog.Preview(default)).Token);
 var clock=new TestClock{Now=now};var pricing=new ErpPricingService(db,provider,registry,demo,hub,clock);await pricing.Refresh(default);
 await pricing.Refresh(default); // identical version is a harmless replay
 var list=provider.Lists[0];pricing.Configure(new PricingPolicy(list.Id));
 var product=db.CatalogRead(s=>s.Products.Single(p=>p.ErpVariantId==provider.Variants[0].Context.SourceRecordId));
 var links=db.CatalogRead(s=>s.Catalog.Listings.Values.Where(l=>l.ProductId==product.Id).ToArray());Check(links.Length>=6,"Mock marketplace fixtures");
 foreach(var link in links){pricing.Configure(new ChannelPriceRule(link.StoreId,list.Id,"TRY"));var p=pricing.Preview(product.Id,link.StoreId,link.Key);Check(p.NewPrice==449&&p.State=="pending-approval","ERP preview for "+link.StoreId);Reject(()=>pricing.Publish(p.Id));pricing.Approve(p.Id,"test-admin");var receipt=pricing.Publish(p.Id);Check(receipt.State=="published"&&pricing.Publish(p.Id).ReceiptId==receipt.ReceiptId,"Idempotent mock publication");}
 var target=links.Single(l=>l.StoreId=="trendyol");PriceProposal Preview()=>pricing.Preview(product.Id,target.StoreId,target.Key);
 var stale=Preview();pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"TRY",MarkupPercent:1));Reject(()=>pricing.Approve(stale.Id,"test-admin"));
 var expired=Preview();clock.Now=now.AddMinutes(11);Reject(()=>pricing.Approve(expired.Id,"test-admin"));clock.Now=now;
 var sourceStale=Preview();var original=provider.Lists[0];provider.Lists=[original with{Context=original.Context with{SourceVersion=original.Context.SourceVersion+1},Entries=original.Entries.Select(e=>e with{Amount=500}).ToArray()}];await pricing.Refresh(default);Reject(()=>pricing.Approve(sourceStale.Id,"test-admin"));
 provider.Lists=[original];try{await pricing.Refresh(default);throw new Exception("Rollback accepted");}catch(IntegrationException){}
 var good=db.CatalogRead(s=>s.Pricing.Lists[list.Id]);provider.Lists=[good with{Name="same-version mutation"}];try{await pricing.Refresh(default);throw new Exception("Version conflict accepted");}catch(IntegrationException){}
 provider.Lists=[good with{Context=good.Context with{TenantId="foreign"}}];try{await pricing.Refresh(default);throw new Exception("Foreign source accepted");}catch(IntegrationException){}
 provider.Lists=[good with{Entries=good.Entries.Select(e=>e with{Currency="INVALID"}).ToArray()}];try{await pricing.Refresh(default);throw new Exception("Invalid currency accepted");}catch(IntegrationException){}
 provider.Lists=[good with{Entries=good.Entries.Concat(good.Entries).ToArray()}];try{await pricing.Refresh(default);throw new Exception("Duplicate row accepted");}catch(IntegrationException){}
 Check(db.CatalogRead(s=>s.Pricing.Lists[list.Id].Entries[0].Amount)==500,"Failed refresh preserves last valid snapshot");
 provider.Lists=[];await pricing.Refresh(default);Check(Preview().State=="blocked","Missing source list blocks");provider.Lists=[good];await pricing.Refresh(default);
 pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"TRY",VatIncluded:false));Check(Preview().State=="blocked","Mock gross VAT requirement");
 pricing.Configure(new PricingPolicy(list.Id,["TRY","USD"]));pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"USD"));Check(Preview().State=="blocked","Wrong channel currency cannot publish");
 pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"TRY"));clock.Now=now.AddHours(2);Check(Preview().State=="blocked","Source age blocks");clock.Now=now;
 provider.Lists=[good with{Context=good.Context with{SourceVersion=good.Context.SourceVersion+1},Entries=good.Entries.Select(e=>e with{Amount=null}).ToArray()}];await pricing.Refresh(default);Check(Preview().NewPrice==null&&Preview().State=="blocked","Unknown ERP value never becomes zero in proposals");
 provider.Lists=[good with{Context=good.Context with{SourceVersion=good.Context.SourceVersion+2}}];await pricing.Refresh(default);
 pricing.Configure(new PricingPolicy(list.Id,AlwaysRequireApproval:false,ApprovalChangePercent:100));Check(Preview().State=="ready","Configurable approval threshold");
 Reject(()=>pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"TRY",CommissionPercent:100)));
 Reject(()=>pricing.Configure(new PricingPolicy(list.Id,["INVALID"])));
 var restarted=new DemoStore(env);Check(restarted.CatalogRead(s=>s.Pricing.Proposals.Any(p=>p.State=="published")),"Proposals survive restart");
 var preview=hub.Preview([target.Key]);Check(!preview[0].Ready,"Legacy combined price publisher blocked");
 pricing.Configure(new ChannelPriceRule(target.StoreId,list.Id,"TRY",MarkupPercent:10));
 var oldStock=demo.Snapshot(registry.Connections[target.StoreId].Settings).Products.Single(p=>p.Barcode==target.Barcode).Stock;
 var otherBefore=demo.Snapshot(registry.Connections["hepsiburada"].Settings).Products.Single(p=>p.Barcode==target.Barcode).SalePrice;
 var changed=Preview();Check(changed.NewPrice==550,"Source-authorized markup result");pricing.Approve(changed.Id,"admin");pricing.Publish(changed.Id);
 var currentNative=demo.Snapshot(registry.Connections[target.StoreId].Settings).Products.Single(p=>p.Barcode==target.Barcode);Check(currentNative.SalePrice==550&&currentNative.Stock==oldStock,"Price-only publication preserves stock");Check(demo.Snapshot(registry.Connections["hepsiburada"].Settings).Products.Single(p=>p.Barcode==target.Barcode).SalePrice==otherBefore,"Another seller remains unchanged");
 var nativeStale=Preview();var marketFile=Path.Combine(folder,"Data","integration-demo.json");var marketJson=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marketFile))!;var setting=registry.Connections[target.StoreId].Settings;var space=marketJson[setting.Marketplace+"|"+setting.Environment+"|"+setting.Name]!;foreach(var row in space["products"]!.AsArray())if(row!["barcode"]!.GetValue<string>()==target.Barcode)row["salePrice"]=551;File.WriteAllText(marketFile,marketJson.ToJsonString());demo=new DemoWorkspace(env);pricing=new ErpPricingService(db,provider,registry,demo,hub,clock);Reject(()=>pricing.Approve(nativeStale.Id,"admin"));
 var retry=Preview();pricing.Approve(retry.Id,"admin");var dataFile=Path.Combine(folder,"Data","demo-store.json");var savedFile=dataFile+".saved";File.Move(dataFile,savedFile);Directory.CreateDirectory(dataFile);
 try{try{pricing.Publish(retry.Id);throw new Exception("Expected persistence failure");}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}}finally{Directory.Delete(dataFile);File.Move(savedFile,dataFile,true);}
 Check(pricing.Publish(retry.Id).State=="published","Recover completed mock receipt after metadata write failure");Check(demo.Snapshot(registry.Connections[target.StoreId].Settings).Jobs.Count(j=>j.RequestId==retry.Id&&j.Kind=="erp-policy-price")==1,"Recovery must not publish twice");
 var snapshot=db.CatalogRead(s=>s.Pricing.Lists[list.Id]);provider.Lists=[snapshot with{Context=snapshot.Context with{SourceVersion=snapshot.Context.SourceVersion+1},Entries=snapshot.Entries.Select(e=>e with{Amount=null}).ToArray()}];await pricing.Refresh(default);var unknown=Preview();Check(unknown.SourceAmount==null&&unknown.NewPrice==null&&unknown.State=="blocked","Unknown ERP price remains null through preview");Check(System.Text.Json.JsonSerializer.SerializeToElement(hub.Search(new(Search:product.Sku)).Items.Single().Product).GetProperty("price").ValueKind==System.Text.Json.JsonValueKind.Null,"Product API serializes unknown price as null");
 var unknownSnapshot=provider.Lists[0];provider.Lists=[unknownSnapshot with{Context=unknownSnapshot.Context with{SourceVersion=unknownSnapshot.Context.SourceVersion+1},Entries=unknownSnapshot.Entries.Select(e=>e with{Currency="ZZZ"}).ToArray()}];try{await pricing.Refresh(default);throw new Exception("Invalid currency accepted");}catch(IntegrationException){}Check(db.CatalogRead(s=>s.Pricing.Lists[list.Id].Entries[0].Amount)==null,"Invalid source cannot overwrite unknown with zero");
 Reject(()=>RealPriceWrites.Deny(449));Check(RealPriceWrites.HasPrice(System.Text.Json.JsonSerializer.SerializeToElement(new{items=new[]{new{salePrice=449}}})),"Native nested price payload detection");
 Console.WriteLine("PASS ERP pricing: VAT, currencies, fees, bounds, campaigns, mock channels, approvals, idempotency, stale previews, source versions, missing lists, freshness, restart and legacy bypass prevention.");
}
finally{var absolute=Path.GetFullPath(folder);if(!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("marketpilot-pricing-"))throw new Exception("Unsafe cleanup");Directory.Delete(absolute,true);}
sealed class TestClock:TimeProvider{public DateTimeOffset Now;public override DateTimeOffset GetUtcNow()=>Now;}
