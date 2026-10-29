using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Catalog;

public static class ProductOwnershipPolicy
{
    public const string ErpSource = "nebim-v3";

    public static IntegrationException ManualWriteDenied() =>
        new("master_read_only", "Ürün ana verisi salt okunurdur; değişiklik Nebim V3 kaynağından alınmalıdır.", 409);
}
