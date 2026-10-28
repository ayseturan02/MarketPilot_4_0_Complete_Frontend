using System.Globalization;
namespace MarketPilot.Web.Integrations.Ticimax;
public sealed partial class TicimaxSoap
{
    public async Task<string?> WriteStockPrice(ConnectionSettings s, MarketplaceProduct p, int quantity, decimal? price, CancellationToken ct)
    {
        if(!CredentialProfiles.Writes(configuration,s))throw new IntegrationException("write_disabled","Ticimax yazma izni kapalı.",409);
        if(!int.TryParse(p.Id,out var id)||id<=0)throw new IntegrationException("identifier_required","Ticimax varyasyon numarası gerekli.",409);
        var contract=await Contract(s,"UrunServis",ct);
        var fields=contract.ParameterFields("VaryasyonGuncelle","urun");
        var switches=contract.ParameterFields("VaryasyonGuncelle","ayar");
        var needed=price.HasValue?new[]{"ID","StokAdedi","SatisFiyati","IndirimliFiyati"}:new[]{"ID","StokAdedi"};
        if(needed.Any(k=>!fields.ContainsKey(k)) || !switches.ContainsKey("StokAdediGuncelle") ||
            price.HasValue&&(!switches.ContainsKey("SatisFiyatiGuncelle")||!switches.ContainsKey("IndirimliFiyatiGuncelle")) ||
            switches.Values.Any(v=>v!="boolean"))
            throw new IntegrationException("unsupported_contract","Mağazanın Ticimax stok/fiyat güncelleme sözleşmesi desteklenmiyor.",409);
        // Explicit false flags prevent unrelated product fields from being changed.
        var flags=switches.ToDictionary(k=>k.Key,k=>(object?)false,StringComparer.OrdinalIgnoreCase);
        flags["StokAdediGuncelle"]=true;
        var variant=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase){["ID"]=id,["StokAdedi"]=quantity};
        if(price.HasValue)
        {
            // The requested final sale price replaces the base price and clears the old discount.
            variant["SatisFiyati"]=price.Value; variant["IndirimliFiyati"]=0m;
            flags["SatisFiyatiGuncelle"]=true; flags["IndirimliFiyatiGuncelle"]=true;
        }
        var result=await CallCore(s,"UrunServis","VaryasyonGuncelle",new(StringComparer.OrdinalIgnoreCase){["urun"]=variant,["ayar"]=flags},true,ct);
        if(!int.TryParse(result.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var changed)||changed<=0)
            throw new IntegrationException("write_rejected","Ticimax varyasyon güncellemesini doğrulamadı.",409);
        return id.ToString(CultureInfo.InvariantCulture);
    }
}
