using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using System.Security.Cryptography;
using System.Text;

namespace MarketPilot.Web.Workspace;
public static class OperationalInventory
{
    public static bool Active(InventoryHold h)=>h.State is "held" or "awaiting-source" or "awaiting-replacement"&&(!h.ExpiresAt.HasValue||h.ExpiresAt>DateTimeOffset.UtcNow);
    private static decimal FamilyQuantity(IEnumerable<InventoryHold> holds)=>Math.Max(holds.Where(h=>h.State=="awaiting-replacement").Sum(h=>(decimal)h.Quantity),holds.Where(h=>h.State!="awaiting-replacement").Sum(h=>(decimal)h.Quantity));
    public static decimal Reserved(Database s,string productId,string? except=null)=>s.Operations.Holds.Where(h=>h.ProductId==productId&&h.Id!=except&&Active(h)).GroupBy(h=>new{h.StoreId,h.Source,h.AccountIdentity,h.OrderNumber}).Sum(FamilyQuantity);
    public static Dictionary<string,decimal> Totals(Database s)=>s.Operations.Holds.Where(Active).GroupBy(h=>h.ProductId).ToDictionary(g=>g.Key,g=>g.GroupBy(h=>new{h.StoreId,h.Source,h.AccountIdentity,h.OrderNumber}).Sum(FamilyQuantity));
    public static decimal Available(Database s,Product p,string? except=null)=>Math.Max(0,p.Stock-Reserved(s,p.Id,except));
    public static string LineId(OperationLine l)=>!string.IsNullOrWhiteSpace(l.NativeLineId)?l.NativeLineId:l.ProductId??l.Barcode;
    public static void Observe(Database s,ConnectionState store,IEnumerable<MarketplaceOrder> orders,string accountIdentity="")
    {
        if(!s.Operations.Policy.ReservationsEnabled)return;
        var source=CatalogHub.Source(store);
        foreach(var order in orders)
        {
            var package=order.PackageId.Length>0?order.PackageId:order.OrderNumber;
            var existing=s.Operations.Holds.Where(h=>h.StoreId==store.Id&&h.Source==source&&h.AccountIdentity==accountIdentity&&h.PackageId==package).ToArray();
            if(order.Status=="UnPacked"){foreach(var h in existing.Where(h=>h.State=="held")){h.State="awaiting-replacement";Touch(s,h.ProductId);}continue;}
            if(order.Status is "Cancelled" or "Canceled")
            {foreach(var h in existing.Where(h=>h.State=="held")){h.State="released";Touch(s,h.ProductId);}continue;}
            if(order.Status=="Returned")
            {foreach(var h in existing.Where(h=>h.State=="held")){h.State="awaiting-source";Touch(s,h.ProductId);}continue;}
            if(order.Status is "Shipped" or "Delivered")
            {foreach(var h in existing.Where(h=>h.State=="held")){h.State="awaiting-source";Touch(s,h.ProductId);}continue;}
            if(order.Status is not ("Created" or "Picking" or "Invoiced" or "Unshipped" or "Pending" or "PendingAvailability"))continue;
            var groups=order.Lines.GroupBy(l=>l.Barcode).ToArray();
            foreach(var group in groups)
            {
                var matches=s.Products.Where(p=>p.SourceSystem=="nebim-v3"&&p.SourceMode==store.Settings.Mode&&p.Barcode==group.Key).Take(2).ToArray();
                if(matches.Length!=1)continue;
                var p=matches[0];var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source+"|"+accountIdentity+"|"+store.Id+"|"+package+"|"+p.Id)));
                var hold=s.Operations.Holds.FirstOrDefault(h=>h.Id==id)??existing.FirstOrDefault(h=>h.ProductId==p.Id);var quantity=group.Sum(l=>l.Quantity);
                if(hold==null){s.Operations.Holds.Add(new(){Id=id,StoreId=store.Id,Source=source,AccountIdentity=accountIdentity,PackageId=package,OrderNumber=order.OrderNumber,ProductId=p.Id,Quantity=quantity});Touch(s,p.Id);}
                else if(hold.State=="released"||hold.State=="held"&&hold.Quantity!=quantity){hold.State="held";hold.Quantity=quantity;Touch(s,p.Id);}
            }
            foreach(var hold in existing.Where(h=>Active(h)&&!groups.Any(g=>s.Products.Any(p=>p.Id==h.ProductId&&p.Barcode==g.Key)))){hold.State="released";Touch(s,hold.ProductId);}
        }
        foreach(var h in s.Operations.Holds.Where(h=>h.StoreId==store.Id&&h.Source==source&&h.AccountIdentity==accountIdentity&&h.State=="awaiting-replacement").ToArray())
            if(s.Operations.Holds.Where(child=>child.StoreId==h.StoreId&&child.Source==h.Source&&child.AccountIdentity==h.AccountIdentity&&child.OrderNumber==h.OrderNumber&&child.ProductId==h.ProductId&&child.PackageId!=h.PackageId&&child.State is "held" or "awaiting-source" or "absorbed").Sum(child=>child.Quantity)>=h.Quantity){h.State="replaced";Touch(s,h.ProductId);}
    }
    public static void Acknowledge(Database s,StockAcknowledgement[] acknowledgements,HashSet<string> updatedProducts)
    {
        foreach(var a in acknowledgements)
        {
            if(a==null||string.IsNullOrWhiteSpace(a.StoreId)||string.IsNullOrWhiteSpace(a.Source)||string.IsNullOrWhiteSpace(a.PackageId)||a.StoreId.Length>100||a.Source.Length>500||a.PackageId.Length>150)throw new ArgumentException("Stok onayında mağaza, kaynak veya paket kimliği geçersiz.");
            var matches=s.Operations.Holds.Where(h=>h.StoreId==a.StoreId&&h.Source==a.Source&&h.PackageId==a.PackageId&&(a.AccountIdentity==null||h.AccountIdentity==a.AccountIdentity)&&h.State=="awaiting-source"&&updatedProducts.Contains(h.ProductId)).ToArray();
            if(matches.Select(h=>h.AccountIdentity).Distinct().Count()>1)throw new ArgumentException("Paket birden fazla satıcı hesabında var; stok onayında hesap kimliği gerekli.");
            foreach(var h in matches){h.State="absorbed";Touch(s,h.ProductId);}
        }
    }
    public static void Touch(Database s,string productId){var p=s.Products.FirstOrDefault(p=>p.Id==productId);if(p!=null)p.Version++;}
}
