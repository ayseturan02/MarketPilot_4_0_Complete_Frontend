using MarketPilot.Web.Integrations;
namespace MarketPilot.Web.Catalog;
public sealed partial class CatalogHub
{
    public object Lookup(string storeId, string? externalId, string? sku, string? barcode)
    {
        Ensure();
        if (new[] { storeId, externalId, sku, barcode }.Any(v => v?.Length > 120))
            throw new IntegrationException("invalid_identity", "Ürün kimliği çok uzun.", 400);
        return db.CatalogRead(s => { var products=s.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).Select(p=>p.Id).ToHashSet();var matches = s.Catalog.Listings.Values.Where(l => products.Contains(l.ProductId) && l.StoreId == storeId && registry.Connections.TryGetValue(storeId, out var store) && Source(store) == l.Source && (!string.IsNullOrWhiteSpace(externalId) ? l.ExternalId == externalId : !string.IsNullOrWhiteSpace(barcode) ? l.Barcode == barcode : !string.IsNullOrWhiteSpace(sku) && l.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))).Select(l => l.ProductId).Distinct().ToArray(); if (matches.Length > 1) throw new IntegrationException("ambiguous_identity", "Birden fazla merkezi ürün eşleşiyor. Ürün eşleştirme bölümünden kontrol et.", 409); return new { productId = matches.SingleOrDefault() }; });
    }
    private static void ValidateEdit(CatalogEdit e)
    {
        if (e == null || string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 300 || string.IsNullOrWhiteSpace(e.Sku) || e.Sku.Length > 100 || e.Barcode == null || e.Barcode.Length > 100 || e.Category == null || e.Category.Length > 200 || e.Brand == null || e.Brand.Length > 200 || e.Description == null || e.Description.Length > 20000 || e.Stock is < 0 or > 10000000 || e.Price is < 0 or > 10000000 || e.Currency == null || e.Currency.Length > 3 || e.Currency.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(e.Currency, "^[A-Z]{3}$") || e.Status is not ("Aktif" or "Pasif" or "Arşiv"))
            throw new IntegrationException("invalid_product", "Ürün alanlarını kontrol et.", 400);
    }
    public object Listings(CatalogQuery q)
    {
        Ensure();
        var fresh = Fresh();
        return db.CatalogRead(s =>
        {
            var products = s.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).ToDictionary(p => p.Id);
            var rows = s.Catalog.Listings.Values.Where(l => products.ContainsKey(l.ProductId) && ListingFilters.Exact(l.StoreId, q.StoreId) && ListingFilters.Exact(l.Source.Split('|')[0], q.Mode) && ListingFilters.Text(products[l.ProductId].Category, q.Category))
                .Where(l => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(l.Name, q.Search) || ListingFilters.Text(l.Sku, q.Search) || ListingFilters.Text(l.Barcode, q.Search) || ListingFilters.Text(products[l.ProductId].Sku, q.Search))
                .Select(l => new { listing = fresh(l), product = products[l.ProductId] }).Where(r => ListingFilters.Exact(r.listing.Status, q.Status)).OrderBy(r => r.product.Name).ThenBy(r => r.listing.Key);
            var total = rows.Count();
            return new {
                items = rows.Skip(q.Page * q.Size).Take(q.Size).Select(r => new { r.listing, product = Clone(r.product) }).ToArray(),
                page = q.Page,
                size = q.Size,
                totalElements = total,
                totalPages = (int)Math.Ceiling(total / (double)q.Size)
            };
        });
    }
}
