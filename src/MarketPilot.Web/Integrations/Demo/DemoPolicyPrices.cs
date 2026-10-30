namespace MarketPilot.Web.Integrations.Demo;

public sealed partial class DemoWorkspace
{
    internal DemoJob? PolicyPriceReceipt(ConnectionSettings settings,string id)
    {
        lock(gate){var receipt=State(settings).Jobs.SingleOrDefault(j=>j.Kind=="erp-policy-price"&&j.RequestId==id);return receipt==null?null:System.Text.Json.JsonSerializer.Deserialize<DemoJob>(System.Text.Json.JsonSerializer.Serialize(receipt));}
    }
    internal DemoJob ApplyPolicyPrice(ConnectionSettings settings,string barcode,decimal? oldPrice,decimal price,string id)
    {
        if(settings.Mode!="demo")throw new IntegrationException("pricing_blocked","Real price writes are disabled.",409);
        lock(gate)
        {
            var state=State(settings);
            var existing=state.Jobs.SingleOrDefault(j=>j.Kind=="erp-policy-price"&&j.RequestId==id);
            var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new{barcode,oldPrice,price,id}))));
            if(existing!=null){if(existing.Fingerprint!=fingerprint)throw new IntegrationException("pricing_conflict","Price receipt has different content.",409);return existing;}
            var matches=state.Products.Where(p=>p.Barcode==barcode).ToArray();
            if(matches.Length!=1||matches[0].SalePrice!=oldPrice||price<=0||price>10000000||decimal.Round(price,2)!=price)throw new IntegrationException("pricing_conflict","Mock listing price or identity changed.",409);
            var product=matches[0];state.Products[state.Products.IndexOf(product)]=product with{SalePrice=price,ListPrice=Math.Max(product.ListPrice??price,price)};
            var receipt=new DemoJob{Kind="erp-policy-price",RequestId=id,Reference=barcode,Price=price,State="completed",Fingerprint=fingerprint,Title="ERP price policy applied to mock listing"};
            state.Jobs.Add(receipt);Save();return receipt;
        }
    }
}
