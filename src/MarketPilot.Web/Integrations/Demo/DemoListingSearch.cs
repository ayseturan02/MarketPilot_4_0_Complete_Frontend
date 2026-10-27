using System.Text.Json;

namespace MarketPilot.Web.Integrations.Demo;

public sealed partial class DemoWorkspace
{
    public object Search(ConnectionSettings settings, string kind, ListingQuery q)
    {
        lock (gate)
        {
            var state = State(settings);
            object result = kind switch
            {
                "products" => ListingFilters.Products(state.Products, q),
                "orders" or "invoices" or "labels" => ListingFilters.Orders(state.Orders.Where(o => kind != "labels" || o.Cargo.TrackingNumber.Length > 0), q),
                "claims" => ListingFilters.Page(state.Claims.Where(c => ListingFilters.Exact(c.Status, q.Status) && ListingFilters.Date(c.ClaimDate, q) && (string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(c.OrderNumber, q.Search) || ListingFilters.Text(c.CustomerName, q.Search) || ListingFilters.Text(c.Id, q.Search))).OrderByDescending(c => c.ClaimDate), q),
                "finance" => ListingFilters.Page(state.Finance.Where(f => ListingFilters.Exact(f.TransactionType, q.Status) && ListingFilters.Date(f.TransactionDate, q) && (string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(f.OrderNumber, q.Search) || ListingFilters.Text(f.Description, q.Search))).OrderByDescending(f => f.TransactionDate), q),
                "history" => ListingFilters.Page(state.Jobs.Where(j => ListingFilters.Exact(j.State, q.Status) && ListingFilters.Date(j.CreatedAt, q) && (string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(j.Title, q.Search) || ListingFilters.Text(j.Reference, q.Search))).OrderByDescending(j => j.CreatedAt), q),
                _ => throw new ArgumentException("Liste bulunamadı.")
            };
            // Mutable claim/job objects are copied only after filtering and paging.
            return JsonSerializer.SerializeToElement(result, json);
        }
    }
}
