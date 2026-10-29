using System.Text.Json;
using MarketPilot.Web.Integrations;
namespace MarketPilot.Web.Pricing;
public static class RealPriceWrites
{
    public static void Deny(decimal? price){if(price.HasValue)throw new IntegrationException("real_price_disabled","Gerçek fiyat gönderimi kapalı; yalnız ERP fiyat kuralları ve mock yayın destekleniyor.",409);}
    public static bool HasPrice(JsonElement value)=>value.ValueKind switch
    {
        JsonValueKind.Object=>value.EnumerateObject().Any(p=>p.Name.Contains("price",StringComparison.OrdinalIgnoreCase)||HasPrice(p.Value)),
        JsonValueKind.Array=>value.EnumerateArray().Any(HasPrice),_=>false
    };
}
