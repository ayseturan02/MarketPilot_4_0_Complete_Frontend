using System.Text.Json;
namespace MarketPilot.Web.Pricing;
public static class PricingProjection
{
    public static Product Clone(Database s,Product product)
    {
        var p=JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(product))!;Apply(s,p);return p;
    }
    public static void Apply(Database s,Product product)
    {
        var entry=ErpPricingService.MasterEntry(s,product,DateTimeOffset.UtcNow);
        if(s.Pricing.LastSourceReadAt==null||s.Pricing.LastSourceReadAt.Value.AddMinutes(s.Pricing.Policy.SourceMaxAgeMinutes)<=DateTimeOffset.UtcNow)entry=null;
        product.PriceKnown=entry?.Amount!=null;
        if(entry?.Amount is {} amount){product.Price=amount;product.Currency=entry.Currency;product.VatIncluded=entry.VatIncluded;}
    }
}
