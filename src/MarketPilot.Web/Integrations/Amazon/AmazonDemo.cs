using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Integrations.Amazon;

// Explicitly local illustrations of Amazon concepts. These are not Amazon response fixtures.
public static class AmazonDemo
{
    public static bool IsAmazonFulfilled(string barcode)=>barcode is "8691000000003" or "8691000000005" or "8691000000007";
    public static string Channel(MarketplaceOrder order)=>order.Lines.Any(l=>IsAmazonFulfilled(l.Barcode))?"AMAZON":"MERCHANT";
    public static string NativeStatus(string status)=>status switch {"Created" or "Picking" or "Invoiced"=>"UNSHIPPED","Shipped" or "Delivered" or "Returned"=>"SHIPPED","Cancelled"=>"CANCELLED",_=>"PENDING"};
    public static AmazonListing Listing(MarketplaceProduct p,DemoWorkspaceState w)
    {
        var fba=IsAmazonFulfilled(p.Barcode);var info=w.ProductInfo.GetValueOrDefault(p.Barcode);var asin="B0DEMO"+p.Id.PadLeft(4,'0');
        return new(p with {ContentId=asin,Stock=fba?null:p.Stock,ProductUrl=""},asin,fba?"DEMO-FNSKU-"+p.Id:"","SHIRT",p.OnSale==true?["BUYABLE","DISCOVERABLE"]:p.ApprovalStatus=="approved"?["DISCOVERABLE"]:[],p.RejectReasons.Select(r=>new AmazonIssue("DEMO-ATTRIBUTE","ERROR",r)).ToArray(),info?.Description??"",info?.Attributes??new(),fba?"AMAZON":"MERCHANT");
    }
    public static AmazonOrder Order(MarketplaceOrder o)
    {
        var channel=Channel(o);var order=o with {OrderNumber="408-"+(1000000+int.Parse(o.OrderNumber.Split('-').Last()))+"-1234567",Cargo=o.Cargo with {Provider=channel=="AMAZON"?"Amazon lojistik · Demo":"Satıcı anlaşmalı kargo · Demo"}};
        return new(order,channel,NativeStatus(o.Status),o.OrderDate?.AddDays(2),o.Cargo.TrackingNumber.Length>0?[new(o.PackageId,order.Cargo.Provider,o.Cargo.TrackingNumber,o.Status,o.OrderDate?.AddDays(1))]:[]);
    }
    public static object Read(DemoWorkspace demo,ConnectionSettings settings,string kind,AmazonQuery q)
    {
        var w=demo.Snapshot(settings);int offset=0;
        if(q.Token is {Length:>0} && (!q.Token.StartsWith("demo:")||!int.TryParse(q.Token[5..],out offset)||offset<0))throw new IntegrationException("invalid_token","Demo sayfa bilgisi geçersiz.",400);
        AmazonPage<T> Page<T>(IEnumerable<T> source){var rows=source.ToArray();var items=rows.Skip(offset).Take(20).ToArray();return new(items,offset+20<rows.Length?"demo:"+(offset+20):null,rows.Length);}
        bool Date(DateTimeOffset? d)=>(!q.StartDate.HasValue||d?.ToUnixTimeMilliseconds()>=q.StartDate)&&(!q.EndDate.HasValue||d?.ToUnixTimeMilliseconds()<=q.EndDate);
        if(kind is "products" or "inventory")
        {
            var rows=w.Products.Select(p=>Listing(p,w)).Where(p=>string.IsNullOrWhiteSpace(q.Search)||(q.IdentifierType switch {"ASIN"=>p.Asin,"EAN" or "GTIN"=>p.Product.Barcode,"FNSKU"=>p.FnSku,_=>p.Product.Sku}).Equals(q.Search,StringComparison.OrdinalIgnoreCase));
            if(q.Status is "ERROR" or "WARNING")rows=rows.Where(p=>p.Issues.Any(i=>i.Severity==q.Status));else if(q.Status is {Length:>0})rows=rows.Where(p=>p.Statuses.Contains(q.Status));
            return Page(rows);
        }
        if(kind is "orders" or "shipping" or "invoices")
        {
            var rows=w.Orders.Select(Order).Where(o=>Date(o.Order.OrderDate)&&(string.IsNullOrWhiteSpace(q.Search)||o.Order.OrderNumber==q.Search)&&(string.IsNullOrWhiteSpace(q.Status)||o.NativeStatus==q.Status)&&(string.IsNullOrWhiteSpace(q.FulfilledBy)||o.FulfilledBy==q.FulfilledBy));return Page(rows);
        }
        if(kind=="fba")return Page(w.Products.Where(p=>IsAmazonFulfilled(p.Barcode)&&(string.IsNullOrWhiteSpace(q.Search)||p.Sku.Equals(q.Search,StringComparison.OrdinalIgnoreCase))).Select(p=>new {asin="B0DEMO"+p.Id.PadLeft(4,'0'),sku=p.Sku,fnSku="DEMO-FNSKU-"+p.Id,name=p.Name,available=p.Id=="3"?0:15,reserved=3,inbound=8,unfulfillable=1,total=p.Id=="3"?12:27,updatedAt=w.LastSyncAt}));
        if(kind=="claims")return Page(w.Claims.Where(c=>Date(c.ClaimDate)&&(string.IsNullOrWhiteSpace(q.Status)||c.Status==q.Status)).Select(c=>new{c.Id,orderNumber=w.Orders.Where(o=>o.OrderNumber==c.OrderNumber).Select(Order).FirstOrDefault()?.Order.OrderNumber??c.OrderNumber,c.CustomerName,c.Barcode,c.Quantity,c.Reason,c.Status,c.ClaimDate}));
        if(kind=="finance")return Page(w.Finance.Where(f=>Date(f.TransactionDate)&&(string.IsNullOrWhiteSpace(q.Search)||w.Orders.Where(o=>o.OrderNumber==f.OrderNumber).Select(Order).FirstOrDefault()?.Order.OrderNumber==q.Search)).Select(f=>new{id=f.Id,type=f.TransactionType,status="RELEASED",description=f.Description,date=f.TransactionDate,amount=f.Amount,currency="TRY",orderNumber=w.Orders.Where(o=>o.OrderNumber==f.OrderNumber).Select(Order).FirstOrDefault()?.Order.OrderNumber??f.OrderNumber}));
        if(kind=="reports")return Page(new[]{new{id="DEMO-REPORT-01",type="GET_MERCHANT_LISTINGS_ALL_DATA",status="DONE",createdAt=w.LastSyncAt,startDate=w.LastSyncAt.AddDays(-1),endDate=w.LastSyncAt,hasDocument=false},new{id="DEMO-REPORT-02",type="GET_FBA_FULFILLMENT_CUSTOMER_RETURNS_DATA",status="IN_QUEUE",createdAt=w.LastSyncAt,startDate=w.LastSyncAt.AddDays(-7),endDate=w.LastSyncAt,hasDocument=false}}.Where(r=>(string.IsNullOrWhiteSpace(q.ReportType)||r.type==q.ReportType)&&(string.IsNullOrWhiteSpace(q.Status)||r.status==q.Status)));
        if(kind=="history")return Page(w.Jobs.Where(j=>Date(j.CreatedAt)&&(string.IsNullOrWhiteSpace(q.Status)||j.State==q.Status)).OrderByDescending(j=>j.CreatedAt));
        if(kind=="catalog")return Page(new[]{new{name="SHIRT",displayName="Tekstil koleksiyonu · Demo"}});
        throw new IntegrationException("not_available","Bu bölüm bulunamadı.",404);
    }
}
