namespace MarketPilot.Web.Integrations.Demo;

// Creates a sample order in one demo store so central stock distribution can be tried without a live marketplace.
public sealed partial class DemoWorkspace
{
    public MarketplaceOrder SimulateSale(ConnectionSettings settings, string barcode, int quantity)
    {
        lock (gate)
        {
            if (quantity is < 1 or > 100) throw new ArgumentException("Adet 1–100 arasında olmalı.");
            var state = State(settings);
            var index = state.Products.FindIndex(p => p.Barcode == barcode);
            if (index < 0) throw new ArgumentException("Ürün bu mağazada bulunamadı.");
            var product = state.Products[index];
            if (product.ApprovalStatus != "approved" || product.OnSale != true) throw new ArgumentException("Ürün bu mağazada satışta değil.");
            if ((product.Stock ?? 0) < quantity) throw new ArgumentException("Kanal stoğu bu satış için yetersiz.");
            var now = DateTimeOffset.UtcNow; var number = "DEMO-" + now.ToUnixTimeMilliseconds();
            var price = product.SalePrice ?? 0;
            var order = new MarketplaceOrder("demo-pkg-" + Guid.NewGuid().ToString("N")[..10], number, "Created", now, "Örnek Müşteri", "ornek@example.invalid",
                price * quantity, (product.ListPrice ?? price) * quantity, ((product.ListPrice ?? price) - price) * quantity, "TRY", "Kredi kartı", "Shipment", null,
                [new("line-" + Guid.NewGuid().ToString("N")[..8], product.Sku, product.Barcode, product.Name, quantity, price, price * quantity, 20, "TRY", "Created", product.CategoryId)],
                new("Demo kargo", "", "", "", "", null, null), new("", "Bekliyor", "", false, "", "", "", false, []));
            var left = (product.Stock ?? 0) - quantity;
            state.Products[index] = product with { Stock = left, OnSale = left > 0 };
            state.Orders.Insert(0, order);
            state.Events.Insert(0, new("Demo satış oluşturuldu", now, order.PackageId));
            Save();
            return order;
        }
    }
}
