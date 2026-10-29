using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.Hepsiburada;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Ticimax;
using MarketPilot.Web.Integrations.Trendyol;

namespace MarketPilot.Web.Synchronization;

public sealed class MarketplaceStockWriter(MarketplaceClients clients, IConfiguration configuration)
{
    public void Check(ConnectionSettings s)
    {
        var c = clients.Configuration(s);
        if (!configuration.GetValue<bool>("Synchronization:AllowRealWrites") || !CredentialProfiles.Writes(c,s))
            throw new IntegrationException("write_disabled", "Bu mağazada gerçek stok/fiyat gönderimi sunucuda açılmamış.", 409);
    }
    public async Task<MarketplaceProduct> Read(ConnectionSettings s, string barcode, string? sku, CancellationToken ct)
    {
        var adapter = clients.Adapter(s);
        var native = !string.IsNullOrWhiteSpace(sku);
        var page = await adapter.ProductsAsync(s, new(Size: 20, Barcode: native ? null : barcode, StockCode: native ? sku : null), ct);
        var rows = page.Items.Where(p => native ? p.Sku == sku : p.Barcode == barcode).Take(2).ToArray();
        if (rows.Length != 1 || native && barcode.Length > 0 && !barcode.StartsWith("sku:", StringComparison.Ordinal) && rows[0].Barcode.Length > 0 && rows[0].Barcode != barcode)
            throw new IntegrationException("product_ambiguous", "Mağazada tek ve aynı SKU/barkod eşleşmesi doğrulanamadı; gönderim yapılmadı.", 409);
        return rows[0];
    }
    public async Task<string?> Send(ConnectionSettings s, MarketplaceProduct product, int quantity, decimal? price, CancellationToken ct, string? expectedIdentity=null)
    {
        if(price.HasValue)throw new IntegrationException("pricing_blocked","Real price writes are disabled. Use ERP pricing preview.",409);
        MarketPilot.Web.Pricing.RealPriceWrites.Deny(price);
        var session=clients.Session(s);
        if(!configuration.GetValue<bool>("Synchronization:AllowRealWrites") || !CredentialProfiles.Writes(session.Config,s)) throw new IntegrationException("write_disabled","Bu mağazada gerçek yazma izni açılmamış.",409);
        if(expectedIdentity is not null && CredentialProfiles.Fingerprint(session.Config,s)!=expectedIdentity) throw new IntegrationException("account_changed","Gönderimden önce satıcı kimlik bilgileri değişti; işlem durduruldu.",409);
        if (quantity is <0 or >1000000 || price is <=0 or >10000000 || price.HasValue && decimal.Round(price.Value,2)!=price.Value)
            throw new IntegrationException("invalid_stock_price", "Tam sayı stok ve en fazla iki ondalıklı pozitif fiyat gerekli.", 400);
        return s.Marketplace switch
        {
            "hepsiburada" => await ((HepsiburadaAdapter)session.Adapter).WriteStockPrice(s, product, quantity, price, ct),
            "n11" => await ((N11Adapter)session.Adapter).WriteStockPrice(s, product, quantity, price, ct),
            "boyner" => await ((BoynerAdapter)session.Adapter).WriteStockPrice(s, product, quantity, price, ct),
            "amazon" => await session.Amazon!.WriteStockPrice(s, product, quantity, price, ct),
            "ticimax" => await session.Soap!.WriteStockPrice(s, product, quantity, price, ct),
            "trendyol" => await Trendyol(session.Runner!,s, product, quantity, price, ct),
            _ => throw new IntegrationException("unsupported_marketplace", "Bu kanal desteklenmiyor.", 409)
        };
    }
    private static async Task<string?> Trendyol(TrendyolOperationRunner runner,ConnectionSettings s, MarketplaceProduct product, int quantity, decimal? price, CancellationToken ct)
    {
        var item = new Dictionary<string, object> { ["barcode"] = product.Barcode, ["quantity"] = quantity };
        if (price.HasValue) { item["salePrice"] = price.Value; item["listPrice"] = Math.Max(price.Value, product.ListPrice ?? price.Value); }
        var result = await runner.RunAsync(TrendyolCatalog.Operations["inventory.updatePriceAndInventory"], s, new(Body:JsonSerializer.SerializeToElement(new { items = new[] { item } }), Confirm:true), ct);
        if (!result.Sent) throw new IntegrationException("write_disabled", "Trendyol gönderimi açılmamış.", 409);
        return result.Data is {ValueKind:JsonValueKind.Object} d && d.TryGetProperty("batchRequestId",out var id) ? id.GetString() : throw new IntegrationException("invalid_receipt", "Trendyol işlem kimliği okunamadı.");
    }
}
