using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Pricing;

public sealed class ErpPricingService(DemoStore db,INebimErpProvider provider,IntegrationRegistry registry,DemoWorkspace demo,CatalogHub catalog,TimeProvider clock)
{
    private static readonly HashSet<string> Currencies=CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c=>{try{return new RegionInfo(c.Name).ISOCurrencySymbol;}catch(ArgumentException){return "";}}).Where(c=>c.Length==3).ToHashSet(StringComparer.Ordinal);
    private static IntegrationException Error(string text,int status=409)=>new("pricing_blocked",text,status);
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid,string text){if(!valid)throw Error(text,400);}
    private static bool Currency(string? value)=>value!=null&&Currencies.Contains(value);
    private DateTimeOffset Now=>clock.GetUtcNow();
    public object Status()=>db.CatalogRead(s=>JsonSerializer.SerializeToElement(new{policy=s.Pricing.Policy,ruleVersion=s.Pricing.RuleVersion,rules=s.Pricing.Rules.Values,lists=s.Pricing.Lists.Values,missingLists=s.Pricing.MissingLists,lastSourceReadAt=s.Pricing.LastSourceReadAt,proposals=s.Pricing.Proposals.TakeLast(100).Reverse(),realPriceWrites=false},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    public void Configure(PricingPolicy policy)
    {
        var allowed=policy.AllowedCurrencies??["TRY"];
        Need(allowed.Length is >0 and <=30&&allowed.All(Currency)&&allowed.Distinct().Count()==allowed.Length&&policy.ApprovalChangePercent is >=0 and <=10000&&policy.PreviewMinutes is >=1 and <=60&&policy.SourceMaxAgeMinutes is >=1 and <=1440,"Para birimi, onay eşiği veya önizleme süresi geçersiz.");
        db.Modify(s=>{if(policy.MasterPriceListId!=null&&!s.Pricing.Lists.ContainsKey(policy.MasterPriceListId))throw Error("Yetkili ana fiyat listesi önce ERP'den okunmalı.");s.Pricing.Policy=policy with{AllowedCurrencies=allowed};s.Pricing.RuleVersion++;return true;});
    }
    public void Configure(ChannelPriceRule rule)
    {
        Need(!string.IsNullOrWhiteSpace(rule.StoreId)&&!string.IsNullOrWhiteSpace(rule.PriceListId),"Store and ERP price list are required.");
        Need(!string.IsNullOrWhiteSpace(rule.StoreId)&&rule.StoreId.Length<=200&&!string.IsNullOrWhiteSpace(rule.PriceListId)&&rule.PriceListId.Length<=200&&registry.Connections.ContainsKey(rule.StoreId)&&rule.PercentageCostBasis is "net" or "gross"&&Currency(rule.Currency)&&rule.CommissionPercent is >=0 and <100&&rule.VariableCostPercent is >=0 and <100&&rule.CommissionPercent+rule.VariableCostPercent<100&&rule.FixedCostNet is >=0 and <=1000000&&rule.MarkupPercent is >=0 and <=1000&&rule.MinimumPrice>0&&rule.MaximumPrice>=rule.MinimumPrice&&rule.MaximumPrice<=10000000&&rule.VatRate is null or >=0 and <=100,"Kanal fiyat kuralı geçersiz.");
        Need((rule.Campaigns??[]).Length<=100&&(rule.Campaigns??[]).All(c=>c!=null&&!string.IsNullOrWhiteSpace(c.Id)&&c.Id.Length<=100&&c.DiscountPercent is >=0 and <100&&c.EndsAt>c.StartsAt)&&(rule.Campaigns??[]).Select(c=>c.Id).Distinct().Count()==(rule.Campaigns??[]).Length,"Kampanya kimliği, indirimi veya tarihleri geçersiz.");
        db.Modify(s=>{if(!s.Pricing.Lists.ContainsKey(rule.PriceListId)||!(s.Pricing.Policy.AllowedCurrencies??["TRY"]).Contains(rule.Currency))throw Error("Kanal listesi veya izinli para birimi doğrulanamadı.");s.Pricing.Rules[rule.StoreId]=rule;s.Pricing.RuleVersion++;return true;});
    }
    public async Task<object> Refresh(CancellationToken ct)
    {
        if(provider.Profile.Mode!="mock"||!provider.Capabilities.IsMock)throw Error("Gerçek ERP bağlantısı kapalı; fiyatlar yalnız mock sağlayıcıdan okunur.");
        if(!provider.Profile.Enabled||!provider.Capabilities.Read.HasFlag(ErpReadCapability.Prices))throw Error("ERP price read capability is unavailable.");
        var priceResult=await provider.ReadPriceListsAsync(ct);var variantResult=await provider.ReadVariantsAsync(ct);
        if(!priceResult.IsMock||!variantResult.IsMock)throw Error("Only verified mock price snapshots are supported.");
        var lists=priceResult.Data;var variants=variantResult.Data;
        if(lists!=null)lists=lists.Select(l=>l==null?null!:JsonSerializer.Deserialize<PriceList>(JsonSerializer.Serialize(l))!).ToArray();
        Need(lists!=null&&lists.Count<=1000&&lists.All(l=>l!=null&&l.Context!=null&&l.Context.TenantId==provider.Profile.TenantId&&l.Context.SourceSystem=="mock-nebim-v3"&&!string.IsNullOrWhiteSpace(l.Id)&&l.Id.Length<=200&&!string.IsNullOrWhiteSpace(l.Name)&&l.Name.Length<=300&&!string.IsNullOrWhiteSpace(l.Context.CorrelationId)&&!string.IsNullOrWhiteSpace(l.Context.IdempotencyKey)&&l.Context.OccurredAt!=default&&l.Context.ReceivedAt>=l.Context.OccurredAt&&!string.IsNullOrWhiteSpace(l.Context.SourceRecordId)&&l.Context.SourceVersion>0&&l.Entries!=null&&l.Entries.Count<=10000&&l.ValidFrom!=default&&(!l.ValidTo.HasValue||l.ValidTo>l.ValidFrom)&&l.Entries.All(e=>e!=null&&e.Amount is null or >=0 and <=10000000&&Currency(e.Currency)&&e.VatRate is null or >=0 and <=100&&e.UnitCode!=null))&&lists.Select(l=>l.Id).Distinct().Count()==lists.Count&&lists.Select(l=>l.Context.SourceRecordId).Distinct().Count()==lists.Count,"ERP fiyat listesi, fiyat, para birimi veya kaynak kimliği geçersiz.");
        Need(variants!=null&&variants.All(v=>v!=null&&v.Context!=null&&v.Context.TenantId==provider.Profile.TenantId&&v.Context.SourceSystem=="mock-nebim-v3"&&!string.IsNullOrWhiteSpace(v.Context.SourceRecordId))&&variants.Select(v=>v.Id).Distinct().Count()==variants.Count&&variants.Select(v=>v.Context.SourceRecordId).Distinct().Count()==variants.Count,"ERP varyant referansları geçersiz.");
        foreach(var list in lists!)Need(list.Entries.Select(e=>(e.VariantId,e.UnitCode,e.Currency)).Distinct().Count()==list.Entries.Count&&list.Entries.All(e=>variants!.Any(v=>v.Id==e.VariantId&&v.BaseUnit==e.UnitCode)),"Fiyat satırında bilinmeyen varyant/birim veya yinelenen fiyat var.");
        return db.Modify(s=>{
            foreach(var list in lists)
            {
                var old=s.Pricing.Lists.GetValueOrDefault(list.Id);
                if(old!=null&&(old.Context.SourceRecordId!=list.Context.SourceRecordId||old.Context.SourceVersion>list.Context.SourceVersion))throw Error("ERP fiyat kaynağı değişmiş veya eski sürüm geldi; listeler korunuyor.");
                if(old!=null&&old.Context.SourceVersion==list.Context.SourceVersion&&CanonicalStore.Fingerprint(old)!=CanonicalStore.Fingerprint(list))throw Error("Aynı ERP fiyat sürümü farklı içerik taşıyor.");
                s.Pricing.Lists[list.Id]=list;
            }
            s.Pricing.MissingLists=s.Pricing.Lists.Keys.Except(lists.Select(l=>l.Id)).ToHashSet();s.Pricing.VariantSources=variants!.ToDictionary(v=>v.Id,v=>v.Context.SourceRecordId);s.Pricing.LastSourceReadAt=Now;
            return (object)new{count=lists.Count,readAt=Now};
        });
    }
    public static PriceEntry? MasterEntry(Database s,Product product,DateTimeOffset now)
    {
        if(product.ErpVariantId.Length==0)return null;
        var id=s.Pricing.Policy.MasterPriceListId;if(id==null||s.Pricing.MissingLists.Contains(id)||!s.Pricing.Lists.TryGetValue(id,out var list)||list.ValidFrom>now||list.ValidTo<=now)return null;
        var variant=s.Pricing.VariantSources.Where(v=>v.Value==product.ErpVariantId).Select(v=>v.Key).ToArray();if(variant.Length!=1)return null;
        var rows=list.Entries.Where(e=>e.VariantId==variant[0]).ToArray();return rows.Length==1?rows[0]:null;
    }
    private (PriceCalculation Calculation,string Fingerprint,decimal? OldPrice,string ListId,long Version) Evaluate(Database s,string productId,string storeId,string listingKey)
    {
        var product=s.Products.SingleOrDefault(p=>p.Id==productId)??throw Error("Ana ürün bulunamadı.");
        var store=registry.Connections.GetValueOrDefault(storeId)??throw Error("Mağaza bulunamadı.");
        var link=s.Catalog.Listings.GetValueOrDefault(listingKey)??throw Error("İlan eşleştirmesi bulunamadı.");
        if(link.ProductId!=productId||link.StoreId!=storeId||CatalogHub.Source(store)!=link.Source)throw Error("İlan veya satıcı kaynağı değişmiş.");
        var rule=s.Pricing.Rules.GetValueOrDefault(storeId)??throw Error("Kanalın yetkili ERP fiyat listesi seçilmemiş.");
        var list=s.Pricing.Lists.GetValueOrDefault(rule.PriceListId);var entries=list?.Entries.Where(e=>s.Pricing.VariantSources.GetValueOrDefault(e.VariantId)==product.ErpVariantId&&product.ErpVariantId.Length>0).ToArray()??[];
        var entry=entries.Length==1?entries[0]:null;
        MarketplaceProduct? native=null;if(store.Settings.Mode=="demo")native=demo.Snapshot(store.Settings).Products.SingleOrDefault(p=>p.Id==link.ExternalId&&p.Sku==link.Sku&&p.Barcode==link.Barcode);
        var currency=native?.Currency??link.Currency;var oldPrice=store.Settings.Mode=="demo"?native?.SalePrice:link.Price;
        string? blocked=store.Settings.Mode!="demo"?"Gerçek fiyat gönderimi her koşulda kapalı.":native==null?"Mock ilan bulunamadı veya kimliği değişmiş.":product.Status!="Aktif"||product.ErpProfileId!=provider.Profile.Id?"Ürün mock ERP ile doğrulanmış ve aktif olmalı.":s.Pricing.MissingLists.Contains(rule.PriceListId)||list==null?"ERP fiyat listesi kaynakta bulunamadı.":list.ValidFrom>Now||list.ValidTo<=Now?"ERP fiyat listesinin geçerlilik süresi dışında.":entries.Length>1?"Varyant için birden fazla fiyat var; tekil liste gerekli.":!(s.Pricing.Policy.AllowedCurrencies??["TRY"]).Contains(rule.Currency)||currency!=rule.Currency?"Kanal para birimi uyuşmuyor; fiyat yayınlanamaz.":!rule.VatIncluded?"Bu mock kanal KDV dahil fiyat bekliyor.":null;
        if(oldPrice is >10000000 or <0)blocked="Mevcut kanal fiyat? ge?ersiz; yeniden do?rulay?n.";
        if(s.Pricing.LastSourceReadAt==null||s.Pricing.LastSourceReadAt.Value.AddMinutes(s.Pricing.Policy.SourceMaxAgeMinutes)<=Now)blocked="ERP fiyat g?r?nt?s? eskimi?; kaynak listelerini yenileyin.";
        var calculation=PriceCalculator.Calculate(entry,rule,Now);if(blocked!=null)calculation=calculation with{CalculatedAmount=null,BlockReason=blocked};
        var fingerprint=Hash(new{product.Id,product.Version,product.ErpContentHash,link,source=CatalogHub.Source(store),variantSources=s.Pricing.VariantSources.OrderBy(v=>v.Key),ruleVersion=s.Pricing.RuleVersion,rule,listHash=list==null?null:CanonicalStore.Fingerprint(list),missing=s.Pricing.MissingLists.Contains(rule.PriceListId),oldPrice,currency,calculation});
        return(calculation,fingerprint,oldPrice,rule.PriceListId,list?.Context.SourceVersion??0);
    }
    public PriceProposal Preview(string productId,string storeId,string listingKey)
    {
        Need(!string.IsNullOrWhiteSpace(productId)&&productId.Length<=200&&!string.IsNullOrWhiteSpace(storeId)&&storeId.Length<=200&&!string.IsNullOrWhiteSpace(listingKey)&&listingKey.Length<=200,"?r?n, ma?aza ve ilan kimli?i gerekli.");
        catalog.Ensure();return db.Modify(s=>{
            if(s.Pricing.Proposals.Count>=10000)throw Error("Fiyat önizleme kayıt sınırı dolu.");
            var e=Evaluate(s,productId,storeId,listingKey);var policy=s.Pricing.Policy;
            var approval=e.Calculation.BlockReason==null&&(policy.AlwaysRequireApproval||e.OldPrice is null or <=0||e.Calculation.CalculatedAmount.HasValue&&Math.Abs(e.Calculation.CalculatedAmount.Value-e.OldPrice.Value)>=e.OldPrice.Value*policy.ApprovalChangePercent/100);
            var p=new PriceProposal{ProductId=productId,StoreId=storeId,ListingKey=listingKey,Fingerprint=e.Fingerprint,SourceListId=e.ListId,SourceVersion=e.Version,RuleVersion=s.Pricing.RuleVersion,SourceAmount=e.Calculation.SourceAmount,OldPrice=e.OldPrice,NewPrice=e.Calculation.CalculatedAmount,Currency=e.Calculation.Currency,CampaignId=e.Calculation.CampaignId,Error=e.Calculation.BlockReason,State=e.Calculation.BlockReason!=null?"blocked":approval?"pending-approval":"ready",CreatedAt=Now,ExpiresAt=Now.AddMinutes(policy.PreviewMinutes)};s.Pricing.Proposals.Add(p);return Clone(p);
        });
    }
    private static PriceProposal Clone(PriceProposal p)=>JsonSerializer.Deserialize<PriceProposal>(JsonSerializer.Serialize(p))!;
    private PriceProposal Current(Database s,string id)=>s.Pricing.Proposals.SingleOrDefault(p=>p.Id==id)??throw Error("Fiyat önizlemesi bulunamadı.");
    private void Check(Database s,PriceProposal p){if(p.ExpiresAt<=Now||Evaluate(s,p.ProductId,p.StoreId,p.ListingKey).Fingerprint!=p.Fingerprint)throw Error("Fiyat kaynağı, kural, kampanya, ürün veya ilan değişmiş; yeni önizleme gerekli.");}
    public PriceProposal Approve(string id,string actor)=>db.Modify(s=>{var p=Current(s,id);if(p.State=="published")return Clone(p);if(p.State is not ("pending-approval" or "ready" or "approved"))throw Error("Bu fiyat onaylanabilir durumda değil.");Check(s,p);p.State="approved";p.ApprovedBy=actor;p.ApprovedAt=Now;return Clone(p);});
    public PriceProposal Publish(string id)=>db.Modify(s=>{
        var p=Current(s,id);if(p.State=="published")return Clone(p);
        if(p.State is not ("approved" or "ready")||p.NewPrice==null)throw Error("Fiyat onayı eksik veya fiyat engelli.");
        var store=registry.Connections.GetValueOrDefault(p.StoreId)??throw Error("Mağaza bulunamadı.");if(store.Settings.Mode!="demo")throw Error("Gerçek fiyat gönderimi kapalı.");
        var receipt=demo.PolicyPriceReceipt(store.Settings,p.Id);
        if(receipt==null){Check(s,p);var link=s.Catalog.Listings[p.ListingKey];receipt=demo.ApplyPolicyPrice(store.Settings,link.Barcode,p.OldPrice,p.NewPrice.Value,p.Id);}
        p.State="published";p.ReceiptId=receipt.Id;return Clone(p);
    });
}
