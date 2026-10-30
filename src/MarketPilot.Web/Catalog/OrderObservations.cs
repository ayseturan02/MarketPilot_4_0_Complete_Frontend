using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.Ticimax;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MarketPilot.Web.Catalog;
public sealed record ObservedOrder(string StoreId, string Source, MarketplaceOrder Order, DateTimeOffset ObservedAt, long? SourceVersion = null, string? AccountIdentity = null);
public sealed partial class OperationsHub
{
    private bool ObservationActive(ObservedOrder row)
    {
        if(!registry.Connections.TryGetValue(row.StoreId,out var store)||CatalogHub.Source(store)!=row.Source)return false;
        if(configuration==null)return true;
        try{return row.AccountIdentity!=null&&row.AccountIdentity==CredentialProfiles.Identity(configuration,store.Settings);}catch(IntegrationException){return false;}
    }
    // Capture only the page actually read by a centre. Never scan an entire remote account.
    public int Observe(ConnectionState store, object result)
    {
        if (store.Settings.Mode != "real")
            return 0;
        IEnumerable<MarketplaceOrder> rows = result switch {
            MarketplacePage<MarketplaceOrder> p => p.Items,
            AmazonPage<AmazonOrder> p => p.Items.Select(r => r.Order),
            N11Page<N11Order> p => p.Items.Select(r => r.Order),
            BoynerPage<BoynerOrder> p => p.Items.Select(r => r.Order),
            TicimaxPage<TicimaxOrder> p => p.Items.Select(r => new MarketplaceOrder(r.Id, r.Number, r.StatusName.Length > 0 ? r.StatusName : TicimaxStatus(r.Status), r.Date, r.CustomerName, "", r.Total, null, r.Discount, r.Currency, "", "", null, [], new("", r.TrackingNumber, "", "", "", null, null), new(r.InvoiceNumber, "", "", false, "", "", "", null, []))),
            _ => []
        };
        var all = rows.ToArray();
        if (all.Length == 0)
            return 0;
        var source=CatalogHub.Source(store);
        var account=configuration==null?null:CredentialProfiles.Identity(configuration,store.Settings);
        return db.Modify(s => {
            var appliedOrders=new List<MarketplaceOrder>();
            foreach(var order in all)
            {
                var identity=order.PackageId.Length>0?order.PackageId:order.OrderNumber;
                if(identity.Length==0)continue;
                var legacyKey=CatalogHub.Key("observed",source+"|"+identity);
                var legacy=s.Catalog.Orders.GetValueOrDefault(legacyKey);
                var key=legacy!=null&&legacy.AccountIdentity==account?legacyKey:CatalogHub.Key("observed",source+"|"+account+"|"+identity);
                if(legacy!=null&&legacy.AccountIdentity==account)foreach(var hold in s.Operations.Holds.Where(h=>h.StoreId==store.Id&&h.Source==source&&h.PackageId==identity&&h.AccountIdentity.Length==0))hold.AccountIdentity=account??"";
                var old=s.Catalog.Orders.GetValueOrDefault(key);
                if(old is not null)
                {
                    var oldReference=MarketplaceOrderReference(old.Order,old.SourceVersion,old.ObservedAt,identity);
                    var newReference=MarketplaceOrderReference(order,order.LastModifiedDate,DateTimeOffset.UtcNow,identity);
                    var decision=DataOwnershipPolicy.Resolve(OwnershipField.Order,SourceSystem.Marketplace,SourceSystem.NebimV3,oldReference,newReference);
                    if(decision==OwnershipDecision.Stale)throw new IntegrationException("order_stale","Eski pazaryeri sipariş sürümü reddedildi; güncel kaynak kaydı korundu.",409);
                    if(decision==OwnershipDecision.Conflict)throw new IntegrationException("order_conflict","Sipariş değişikliği kaynak sürümü/zamanı ile sıralanamıyor veya aynı sürümde içeriği farklı; uzlaştırma için bloke edildi.",409);
                    if(decision==OwnershipDecision.NotOwner)throw new IntegrationException("order_not_owner","Sipariş kaynağı pazaryeri değil; güncelleme bloke edildi.",409);
                    if(decision==OwnershipDecision.Duplicate&&(!order.LastModifiedDate.HasValue||old.SourceVersion==order.LastModifiedDate))continue;
                }
                s.Catalog.Orders[key]=new(store.Id,source,order,DateTimeOffset.UtcNow,order.LastModifiedDate,account);
                appliedOrders.Add(order);
            }
            if(appliedOrders.Count>0){s.Catalog.OrderRevision++;MarketPilot.Web.Workspace.OperationalInventory.Observe(s,store,appliedOrders,account??"");}
            return appliedOrders.Count;
        });
    }
    private static ExternalReference MarketplaceOrderReference(MarketplaceOrder order,long? sourceVersion,DateTimeOffset observedAt,string identity)
    {
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(order))));
        return new(SourceSystem.Marketplace,"marketplace-order",identity,sourceVersion,null,observedAt,hash);
    }
    private static string TicimaxStatus(int? s) => s switch { 0 or 1 or 2 or 3 => "Created", 4 or 5 => "Picking", 6 => "Shipped", 7 => "Delivered", 8 => "Cancelled", 9 or 11 or 12 or 13 or 16 or 17 => "Returned", _ => "Bildirilmedi" };
}
