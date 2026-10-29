using MarketPilot.Web.Catalog;

namespace MarketPilot.Web.Enterprise;

public static class DemoErpCatalogFixture
{
    private static readonly string[] LegacySeedSkus =
    [
        "TSH-3002", "GML-1018", "PNT-1033", "SWT-4512", "HRK-5551",
        "TSH-6090", "ELB-3339", "MNT-2040", "POL-1001"
    ];

    private static readonly ErpItem[] Items =
    [
        new("TSH-EKR-S", "8691000000001", "Pamuklu Oversize Tişört · Ekru / S", 36, 449, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("TSH-EKR-M", "8691000000002", "Pamuklu Oversize Tişört · Ekru / M", 18, 449, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("TSH-SYH-M", "8691000000003", "Pamuklu Oversize Tişört · Siyah / M", 0, 449, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("GML-TS-L", "8691000000004", "Keten Gömlek · Taş / L", 12, 899, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("PNT-BEJ-38", "8691000000005", "Dokuma Pantolon · Bej / 38", 7, 1099, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("SWT-GRI-M", "8691000000006", "Fermuarlı Sweatshirt · Gri / M", 24, 799, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("HRK-KHV-ST", "8691000000007", "Triko Hırka · Kahve / Standart", 9, 1199, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true),
        new("TSH-LCV-L", "8691000000008", "Çizgili Tişört · Lacivert / L", 16, 499, "TRY", Category: "Giyim / Günlük Koleksiyon", Brand: "İnternet Tekstil", Description: "Demo ERP ürün kaydı.", WarehouseCode: "MAIN", VatIncluded: true)
    ];

    public static void Apply(Database database)
    {
        foreach (var product in database.Products.Where(p => p.SourceMode == "local" && string.IsNullOrEmpty(p.SourceSystem) &&
                     LegacySeedSkus.Contains(p.Sku, StringComparer.OrdinalIgnoreCase)))
        {
            product.SourceSystem = "nebim-v3";
            product.SourceMode = "demo";
            product.Version++;
        }

        foreach (var item in Items)
        {
            var matches = database.Products.Where(p => p.SourceMode == "demo" &&
                (p.Sku.Equals(item.Sku, StringComparison.OrdinalIgnoreCase) ||
                 p.Barcode.Equals(item.Barcode, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException("Demo ERP kataloğunda yinelenen ürün kimliği bulundu.");

            var product = matches.SingleOrDefault();
            if (product is null)
            {
                product = new Product { Id = Guid.NewGuid().ToString("N"), SourceMode = "demo" };
                database.Products.Add(product);
            }
            else if (product.SourceSystem == "nebim-v3")
                continue;

            var changed = product.SourceSystem != "nebim-v3" || product.Name != item.Name || product.Sku != item.Sku ||
                product.Barcode != item.Barcode || product.Category != item.Category || product.Brand != item.Brand ||
                product.Description != item.Description || product.Stock != item.Stock || product.StockKnown != item.Stock.HasValue || product.StockState != (item.Stock.HasValue ? StockValueState.Known : StockValueState.Unknown) ||
                product.Price != item.Price || product.PriceKnown != item.Price.HasValue || product.Currency != item.Currency ||
                product.VatIncluded != item.VatIncluded || product.SourceMode != "demo";
            product.SourceSystem = "nebim-v3";
            product.SourceMode = "demo";
            product.Name = item.Name;
            product.Sku = item.Sku;
            product.Barcode = item.Barcode;
            product.Category = item.Category ?? "";
            product.Brand = item.Brand ?? "";
            product.Description = item.Description ?? "";
            product.Stock = item.Stock ?? 0;
            product.StockKnown = item.Stock.HasValue;
            product.StockState = item.Stock.HasValue ? StockValueState.Known : StockValueState.Unknown;
            product.Price = item.Price ?? 0;
            product.PriceKnown = item.Price.HasValue;
            product.Currency = item.Currency;
            product.VatIncluded = item.VatIncluded;
            if (changed)
                product.Version++;
        }

        foreach (var product in database.Products.Where(p => string.IsNullOrWhiteSpace(p.SourceSystem)))
        {
            product.SourceSystem = "unassigned";
            product.Version++;
        }
    }
}
