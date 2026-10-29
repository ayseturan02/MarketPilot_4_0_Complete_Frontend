using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;

public sealed class ErpCatalog(DemoStore db, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public object Import(ErpCatalogBatch batch, SourceSystem stockAuthority = SourceSystem.NebimV3, string? expectedWarehouseCode = null)
    {
        if (stockAuthority is not (SourceSystem.NebimV3 or SourceSystem.Wms))
            throw new ArgumentException("Stok yetkili kaynağı ERP veya WMS olmalıdır.");
        if (string.IsNullOrWhiteSpace(batch.RequestId) || batch.RequestId.Length > 150 || batch.Items == null || batch.Items.Length > 200000)
            throw new ArgumentException("Kaynak işlem kimliği ve ürün listesi geçersiz.");
        if (batch.Items.Any(item => item == null || string.IsNullOrWhiteSpace(item.Sku) || item.Sku.Length > 100 ||
                string.IsNullOrWhiteSpace(item.Barcode) || item.Barcode.Length > 120 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 300 ||
                item.Stock is < 0 or > 10000000 || item.Price is < 0 or > 10000000 ||
                item.Currency == null || !Regex.IsMatch(item.Currency, "^[A-Z]{3}$") ||
                item.Images?.Length > 20 || item.Images?.Any(image => !EnterpriseBridge.SafeLink(image)) == true ||
                item.Attributes?.Any(attribute => string.IsNullOrWhiteSpace(attribute.Key) || attribute.Key.Length > 100 || attribute.Value?.Length > 500) == true ||
                !StockValueFor(item).IsValid()))
            throw new ArgumentException("Kaynak ürün, stok durumu, para birimi, fiyat veya görsel bağlantısı geçersiz.");

        if (!string.IsNullOrWhiteSpace(expectedWarehouseCode) && batch.Items.Any(item =>
                item.WarehouseCode is not null && !string.Equals(item.WarehouseCode, expectedWarehouseCode, StringComparison.OrdinalIgnoreCase)))
            throw new IntegrationException("warehouse_mismatch", "Kaynak deposu seçilen depo ile eşleşmiyor; stok uygulanmadı.", 409);
        batch = batch with { Items = batch.Items.Select(item => item with
        {
            Sku = item.Sku.Trim(),
            Barcode = item.Barcode.Trim(),
            Name = item.Name.Trim(),
            WarehouseCode = item.WarehouseCode ?? (string.IsNullOrWhiteSpace(expectedWarehouseCode) ? null : expectedWarehouseCode)
        }).ToArray() };
        if (batch.Items.GroupBy(item => item.Barcode, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) ||
            batch.Items.GroupBy(item => item.Sku, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ArgumentException("Kaynak listesinde tekrarlayan SKU veya barkod var; hiçbir ürün uygulanmadı.");
        if (batch.StockAcknowledgements?.Length > 10000 || batch.Items.Any(item => item.StockAcknowledgements?.Length > 100))
            throw new ArgumentException("Stok onayı kayıt sınırı aşıldı.");

        object receiptPayload = batch.StockAcknowledgements is { Length: > 0 } || batch.Items.Any(item => item.StockAcknowledgements != null)
            ? new { batch.Items, batch.StockAcknowledgements }
            : batch.Items;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receiptPayload, Json))));
        var mode = config["Enterprise:Erp:DataMode"] == "demo" ? "demo" : "real";
        return db.Modify(database =>
        {
            if (database.ErpReceipts.TryGetValue(batch.RequestId, out var previous))
            {
                if (previous.Hash != hash)
                    throw new IntegrationException("source_conflict", "Kaynak işlem kimliği farklı içerikle tekrar kullanıldı.", 409);
                return new { updated = previous.ProductIds.Length, products = database.Products.Where(product => previous.ProductIds.Contains(product.Id)).ToArray() };
            }

            var applied = new List<Product>();
            foreach (var item in batch.Items)
            {
                var matches = database.Products.Where(product => product.SourceMode == mode &&
                    (product.Barcode.Equals(item.Barcode, StringComparison.OrdinalIgnoreCase) ||
                     product.Sku.Equals(item.Sku, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                if (matches.Length > 1)
                    throw new IntegrationException("source_ambiguous", "Kaynak ürün için birden fazla merkezi kayıt eşleşti.", 409);

                var product = matches.SingleOrDefault();
                if (product is null)
                {
                    product = new Product { Id = Guid.NewGuid().ToString("N"), SourceMode = mode, StockKnown = false, StockState = StockValueState.Unknown, CreatedAt = DateTimeOffset.UtcNow };
                    database.Products.Add(product);
                }

                // A projected provider master cannot be rewritten through the legacy bridge.
                // Stock/price observations with identical master fields remain independent.
                if (product.ErpProfileId.Length > 0 &&
                    (product.Name != item.Name || product.Sku != item.Sku || product.Barcode != item.Barcode ||
                     item.Category is not null && item.Category != product.Category ||
                     item.Brand is not null && item.Brand != product.Brand ||
                     item.Description is not null && item.Description != product.Description ||
                     item.Images is not null && !item.Images.SequenceEqual(product.Images) ||
                     item.VariantId is not null && item.VariantId != product.VariantId ||
                     item.Attributes is not null && (item.Attributes.Count != product.Attributes.Count || item.Attributes.Any(a=>product.Attributes.GetValueOrDefault(a.Key)!=a.Value))))
                    throw ProductOwnershipPolicy.ManualWriteDenied();

                var sourceId = string.IsNullOrWhiteSpace(item.SourceEntityId) ? item.Sku : item.SourceEntityId.Trim();
                var changed = false;
                changed |= Apply(product, OwnershipField.ProductCard, "product-card", sourceId,
                    new { item.Name, item.Description, item.Images }, item, stockAuthority, () =>
                    {
                        product.Name = item.Name.Trim();
                        product.Description = item.Description ?? product.Description;
                        if (item.Images is not null) product.Images = item.Images.ToList();
                    });
                changed |= Apply(product, OwnershipField.Variant, "variant:" + (item.VariantId ?? sourceId), sourceId,
                    new { item.VariantId, item.Attributes }, item, stockAuthority, () =>
                    {
                        if (item.VariantId is not null) product.VariantId = item.VariantId;
                        if (item.Attributes is not null) product.Attributes = new(item.Attributes, StringComparer.OrdinalIgnoreCase);
                    }, enabled: item.VariantId is not null || item.Attributes is not null);
                changed |= Apply(product, OwnershipField.Sku, "sku", sourceId, item.Sku, item, stockAuthority, () => product.Sku = item.Sku);
                changed |= Apply(product, OwnershipField.Barcode, "barcode", sourceId, item.Barcode, item, stockAuthority, () => product.Barcode = item.Barcode);
                changed |= Apply(product, OwnershipField.Category, "category", sourceId, item.Category, item, stockAuthority,
                    () => product.Category = item.Category!, item.Category is not null);
                changed |= Apply(product, OwnershipField.Brand, "brand", sourceId, item.Brand, item, stockAuthority,
                    () => product.Brand = item.Brand!, item.Brand is not null);
                changed |= Apply(product, OwnershipField.Price, "price", sourceId,
                    new { item.Price, item.Currency, item.VatIncluded }, item, stockAuthority, () =>
                    {
                        product.Currency = item.Currency;
                        product.PriceKnown = item.Price.HasValue;
                        product.Price = item.Price ?? 0;
                        product.VatIncluded = item.VatIncluded;
                    });

                if (stockAuthority == SourceSystem.NebimV3)
                {
                    var value = StockValueFor(item);
                    if (value.State != StockValueState.NotProvided)
                    {
                        if (string.IsNullOrWhiteSpace(item.WarehouseCode))
                            throw new IntegrationException("warehouse_required", "Fiziksel stok için kaynak depo açıkça belirtilmelidir.", 409);
                        changed |= Apply(product, OwnershipField.PhysicalStock, "physical-stock", item.WarehouseCode ?? sourceId,
                            value, item, stockAuthority, () => ApplyStockValue(product, value));
                    }
                }

                product.SourceSystem = ProductOwnershipPolicy.ErpSource;
                if (changed) product.Version++;
                applied.Add(product);
            }

            var acknowledgements = (batch.StockAcknowledgements ?? []).Concat(batch.Items.SelectMany(item => item.StockAcknowledgements ?? [])).ToArray();
            if (stockAuthority == SourceSystem.NebimV3)
                MarketPilot.Web.Workspace.OperationalInventory.Acknowledge(database, acknowledgements,
                    applied.Where(product => product.StockKnown).Select(product => product.Id).ToHashSet());
            database.ErpReceipts[batch.RequestId] = new() { Hash = hash, ProductIds = applied.Select(product => product.Id).ToArray() };
            foreach (var key in database.ErpReceipts.Keys.Take(Math.Max(0, database.ErpReceipts.Count - 2000)).ToArray())
                database.ErpReceipts.Remove(key);
            db.Event(database, "Nebim V3 verileri alındı", applied.Count + " ürün · kaynak sahipliği ve alan sürümleri denetlendi.");
            return new { updated = applied.Count, products = applied.ToArray() };
        });
    }

    public static StockValue StockValueFor(ErpItem item)
    {
        if (item.Inventory is not null)
        {
            if (item.Stock.HasValue && (item.Inventory.State != StockValueState.Known || item.Stock != item.Inventory.Quantity))
                return new(StockValueState.NotProvided, -1);
            return item.Inventory;
        }
        return item.Stock.HasValue ? StockValue.Known(item.Stock.Value) : StockValue.Unknown;
    }

    private static void ApplyStockValue(Product product, StockValue value)
    {
        product.StockState = value.State;
        product.StockKnown = value.State == StockValueState.Known;
        if (product.StockKnown) product.Stock = value.Quantity!.Value;
    }

    private static bool Apply(Product product, OwnershipField field, string entityType, string externalId, object? content,
        ErpItem item, SourceSystem stockAuthority, Action update, bool enabled = true)
    {
        if (!enabled) return false;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, Json))));
        var incoming = new ExternalReference(SourceSystem.NebimV3, entityType, externalId, item.SourceVersion,
            item.SourceUpdatedAt, DateTimeOffset.UtcNow, hash, item.WarehouseCode);
        var current = product.ExternalReferences.FirstOrDefault(reference => reference.EntityType == entityType);
        var decision = DataOwnershipPolicy.Resolve(field, SourceSystem.NebimV3, stockAuthority, current, incoming);
        switch (decision)
        {
            case OwnershipDecision.Duplicate:
                if (IsNewerReference(incoming, current!))
                {
                    product.ExternalReferences.RemoveAll(reference => reference.EntityType == entityType);
                    product.ExternalReferences.Add(incoming);
                }
                return false;
            case OwnershipDecision.Stale:
                throw new IntegrationException("source_stale", $"Eski {DataOwnershipPolicy.For(field, stockAuthority).DisplayName} sürümü reddedildi.", 409);
            case OwnershipDecision.Conflict:
                throw new IntegrationException("source_unordered", $"{DataOwnershipPolicy.For(field, stockAuthority).DisplayName} değişikliği kaynak sürümü/zamanı olmadan güvenle sıralanamıyor; uzlaştırma gerekli.", 409);
            case OwnershipDecision.NotOwner:
                throw new IntegrationException("source_not_owner", $"{DataOwnershipPolicy.For(field, stockAuthority).DisplayName} için gelen sistem yetkili kaynak değil.", 409);
        }

        update();
        product.ExternalReferences.RemoveAll(reference => reference.EntityType == entityType);
        product.ExternalReferences.Add(incoming);
        return true;
    }

    private static bool IsNewerReference(ExternalReference incoming, ExternalReference current) =>
        incoming.SourceVersion.HasValue && (!current.SourceVersion.HasValue || incoming.SourceVersion > current.SourceVersion) ||
        incoming.SourceUpdatedAt.HasValue && (!current.SourceUpdatedAt.HasValue || incoming.SourceUpdatedAt > current.SourceUpdatedAt);
}
