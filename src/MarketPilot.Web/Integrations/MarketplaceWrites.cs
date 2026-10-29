using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MarketPilot.Web.Synchronization;

namespace MarketPilot.Web.Integrations.Hepsiburada
{
    public sealed partial class HepsiburadaAdapter
    {
        public async Task<string?> WriteStockPrice(ConnectionSettings s, MarketplaceProduct p, int quantity, decimal? price, CancellationToken ct)
        {
            MarketPilot.Web.Pricing.RealPriceWrites.Deny(price);
            if (!CredentialProfiles.Writes(configuration,s)) throw new IntegrationException("write_disabled","Hepsiburada yazma izni kapalı.",409);
            if (string.IsNullOrWhiteSpace(p.ContentId) || string.IsNullOrWhiteSpace(p.Sku))
                throw new IntegrationException("identifier_required", "Hepsiburada SKU ve satıcı stok kodu gerekli.", 409);
            // An inventory upload submits both requested fields in one operation, avoiding partially queued stock/price pairs.
            var item = new Dictionary<string,object> { ["hepsiburadaSku"] = p.ContentId, ["merchantSku"] = p.Sku, ["availableStock"] = quantity };
            if (price.HasValue) item["price"] = price.Value;
            using var body = JsonContent.Create(new[] { item });
            using var doc = await Send(s.Environment,"listing",$"listings/merchantid/{Merchant(s.Environment)}/inventory-uploads",new(),body,ct);
            var id = Text(doc.RootElement,"id");
            if (id.Length == 0) throw new IntegrationException("invalid_receipt","Hepsiburada yükleme kimliği okunamadı.");
            return id;
        }
    }
}
namespace MarketPilot.Web.Integrations.N11
{
    public sealed partial class N11Adapter
    {
        public async Task<string?> WriteStockPrice(ConnectionSettings s, MarketplaceProduct p, int quantity, decimal? price, CancellationToken ct)
        {
            MarketPilot.Web.Pricing.RealPriceWrites.Deny(price);
            if (!CredentialProfiles.Writes(configuration,s)) throw new IntegrationException("write_disabled","n11 yazma izni kapalı.",409);
            if (quantity > 999999) throw new IntegrationException("invalid_quantity","n11 stoku en fazla 999999 olabilir.",400);
            var page = await Products(s,new(Search:p.Sku,Sender:"ALL",Size:100),ct);
            var rows = page.Items.Where(x=>x.Product.Sku==p.Sku).ToArray();
            if (rows.Length!=1 || rows[0].Sender!="SELLER") throw new IntegrationException("managed_inventory","n11depom veya göndericisi doğrulanamayan stok değiştirilemez.",409);
            var item = new Dictionary<string,object> { ["stockCode"] = p.Sku, ["quantity"] = quantity };
            if (price.HasValue)
            {
                if (p.Currency!="TRY") throw new IntegrationException("currency_mismatch","n11 fiyat gönderimi için TRY ürün gerekli.",409);
                item["salePrice"]=price.Value; item["listPrice"]=Math.Max(price.Value,p.ListPrice??price.Value); item["currencyType"]="TL";
            }
            using var body=JsonContent.Create(new { payload=new { integrator="MarketPilot", skus=new[]{item} } });
            using var doc=JsonDocument.Parse(await Send(s,"/ms/product/tasks/price-stock-update",null,body,ct));
            var id=Nj.S(doc.RootElement,"id");
            if (Nj.S(doc.RootElement,"status")!="IN_QUEUE" || id.Length==0) throw new IntegrationException("write_rejected","n11 stok/fiyat isteğini kabul etmedi.",409);
            return id;
        }
    }
}
namespace MarketPilot.Web.Integrations.Boyner
{
    public sealed partial class BoynerAdapter
    {
        private readonly SemaphoreSlim writeGate=new(1,1);
        private DateTimeOffset nextWrite;
        public async Task<string?> WriteStockPrice(ConnectionSettings s, MarketplaceProduct p, int quantity, decimal? price, CancellationToken ct)
        {
            MarketPilot.Web.Pricing.RealPriceWrites.Deny(price);
            if (!CredentialProfiles.Writes(configuration,s)) throw new IntegrationException("write_disabled","Boyner yazma izni kapalı.",409);
            var sale=price??p.SalePrice;var list=price.HasValue?Math.Max(price.Value,p.ListPrice??price.Value):p.ListPrice;
            if(p.Barcode.Length==0 || p.Currency!="TRY" || sale is not >0 || list is null || list<sale)
                throw new IntegrationException("price_unknown","Boyner stok gönderimi için mevcut liste ve satış fiyatları doğrulanmalı.",409);
            await writeGate.WaitAsync(ct);
            try
            {
                if(DateTimeOffset.UtcNow<nextWrite) throw new IntegrationException("rate_limited","Boyner mağazasının gönderim aralığı bekleniyor.",429);
                nextWrite=DateTimeOffset.UtcNow.AddSeconds(37); // Documented 100 requests/hour per merchant.
                using var body=JsonContent.Create(new{items=new[]{new{barcode=p.Barcode,quantity,listPrice=list.Value,salePrice=sale.Value}}});
                using var doc=await Send(s,$"/sapigw/suppliers/{Supplier(s)}/products/price-and-inventory",new(),body,ct);
                var id=Bj.S(doc.RootElement,"batchRequestId");
                if(Bj.F(doc.RootElement,"success").ValueKind!=JsonValueKind.True || id.Length==0) throw new IntegrationException("write_rejected","Boyner stok/fiyat isteğini kabul etmedi.",409);
                return id;
            }finally{writeGate.Release();}
        }
    }
}
namespace MarketPilot.Web.Integrations.Amazon
{
    public sealed partial class AmazonApiClient
    {
        public async Task<string?> WriteStockPrice(ConnectionSettings s, MarketplaceProduct p, int quantity, decimal? price, CancellationToken ct)
        {
            MarketPilot.Web.Pricing.RealPriceWrites.Deny(price);
            if (!CredentialProfiles.Writes(configuration,s)) throw new IntegrationException("write_disabled","Amazon yazma izni kapalı.",409);
            if(p.Sku.Length==0) throw new IntegrationException("identifier_required","Amazon satıcı SKU gerekli.",409);
            var path="/listings/2021-08-01/items/"+Uri.EscapeDataString(Seller(s.Environment))+"/"+Uri.EscapeDataString(p.Sku);
            using var listing=await Get(s.Environment,path,new(){["marketplaceIds"]=MarketplaceId,["includedData"]="summaries,attributes,fulfillmentAvailability"},ct);
            var root=listing.RootElement;
            var availability=Aj.A(root,"fulfillmentAvailability");
            if(Aj.S(root,"sku")!=p.Sku || !availability.Any(x=>Aj.S(x,"fulfillmentChannelCode")=="DEFAULT"))
                throw new IntegrationException("managed_inventory","Amazon FBA veya göndericisi doğrulanamayan stok değiştirilemez.",409);
            var attrs=Aj.F(root,"attributes");var merchant=Aj.A(attrs,"fulfillment_availability").Where(x=>Aj.S(x,"fulfillment_channel_code")=="DEFAULT").ToArray();
            if(merchant.Length!=1 || merchant[0].TryGetProperty("supply_source_id",out _))
                throw new IntegrationException("multi_location_inventory","Amazon çok konumlu stokları ayrı bir konum eşleştirmesi gerektirir.",409);
            var patches=new List<object>{new{op="merge",path="/attributes/fulfillment_availability",value=new[]{new{fulfillment_channel_code="DEFAULT",quantity}}}};
            if(price.HasValue)
            {
                var offers=Aj.A(attrs,"purchasable_offer").Where(x=>Aj.S(x,"marketplace_id")==MarketplaceId && Aj.S(x,"currency")=="TRY" && Aj.S(x,"audience") is "" or "ALL").ToArray();
                if(offers.Length!=1 || Aj.F(offers[0],"discounted_price").ValueKind==JsonValueKind.Array && Aj.A(offers[0],"discounted_price").Length>0 || Aj.F(offers[0],"automated_pricing_merchandising_rule_plan").ValueKind==JsonValueKind.Array && Aj.A(offers[0],"automated_pricing_merchandising_rule_plan").Length>0)
                    throw new IntegrationException("price_conflict","Amazon kampanyası/otomatik fiyatı veya teklif kimliği ayrıca kontrol edilmeli; fiyat değiştirilmedi.",409);
                patches.Add(new{op="merge",path="/attributes/purchasable_offer",value=new[]{new{marketplace_id=MarketplaceId,currency="TRY",audience="ALL",our_price=new[]{new{schedule=new[]{new{value_with_tax=price.Value}}}}}}});
            }
            using var body=JsonContent.Create(new{productType="PRODUCT",patches});
            using var doc=await Send(s.Environment,path,new(){["marketplaceIds"]=MarketplaceId},body,ct);
            if(Aj.S(doc.RootElement,"status")!="ACCEPTED" || Aj.A(doc.RootElement,"issues").Any(i=>Aj.S(i,"severity")=="ERROR"))
                throw new IntegrationException("write_rejected","Amazon stok/fiyat isteğini kabul etmedi.",409);
            return Aj.S(doc.RootElement,"submissionId") is{Length:>0} id?id:throw new IntegrationException("invalid_receipt","Amazon işlem kimliği okunamadı.");
        }
    }
}
