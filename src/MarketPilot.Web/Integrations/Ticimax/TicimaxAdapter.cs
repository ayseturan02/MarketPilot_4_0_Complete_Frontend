using System.Globalization;
using System.Xml.Linq;
using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Integrations.Ticimax;
public record TicimaxQuery(string? Search=null,string SearchType="CARD_ID",int? Status=null,long? StartDate=null,long? EndDate=null,int Page=0,int Size=25,long? OrderId=null,int? Active=null);
public record TicimaxVariant(string Id,string Sku,string Barcode,decimal? Quantity,decimal? Price,decimal? DiscountPrice,string CurrencyId,bool? VatIncluded,decimal? VatRate,Dictionary<string,string> Attributes);
public record TicimaxCard(string Id,string Name,string Brand,string Category,string Description,bool? Active,string[] Images,TicimaxVariant[] Variants);
public record TicimaxPayment(string Id,int? Type,int? Status,decimal? Amount,string Currency,DateTimeOffset? Date);
public record TicimaxOrder(string Id,string Number,int? Status,string StatusName,DateTimeOffset? Date,string CustomerName,string Address,string Currency,decimal? Total,decimal? Discount,string InvoiceNumber,string TrackingNumber,string Note,TicimaxPayment[] Payments,string? DemoReference=null,string? DemoStage=null);
public record TicimaxPackage(string Id,string OrderId,string Carrier,string TrackingNumber,string TrackingLink,string Barcode);
public record TicimaxPage<T>(IReadOnlyList<T> Items,int Page,int Size,bool HasMore=false,long? Total=null);
public sealed class TicimaxAdapter(TicimaxSoap soap):IMarketplaceAdapter
{
    public string Marketplace=>"ticimax";
    public static string? Validate(TicimaxQuery q)
    {
        if(q.Page<0||q.Size is <1 or >100||(long)q.Page*q.Size>int.MaxValue)return "Geçersiz sayfa bilgisi.";
        if(q.Search?.Length>120||q.SearchType is not("CARD_ID" or "SKU" or "BARCODE")||q.OrderId<1||q.Active is <0 or >1||q.Status<0)return "Geçersiz filtre.";
        if(q.StartDate.HasValue!=q.EndDate.HasValue||q.StartDate<0||q.EndDate>253402300799999||q.StartDate>q.EndDate||q.EndDate-q.StartDate>TimeSpan.FromDays(31).TotalMilliseconds)return "Tarihleri birlikte seçin; aralık en fazla 31 gün olabilir.";
        return null;
    }
    private static Dictionary<string,object?> Fields(params(string Key,object? Value)[] values)=>values.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string,object?> Paging(TicimaxQuery q)=>Fields(("BaslangicIndex",q.Page*q.Size),("KayitSayisi",q.Size),("SiralamaDegeri","ID"),("SiralamaDeger","ID"),("SiralamaYonu","DESC"));
    private async Task<Dictionary<string,object?>> ProductFilter(ConnectionSettings s,TicimaxQuery q,CancellationToken ct)
    {
        var f=Fields(("Aktif",q.Active??-1),("Firsat",-1),("Indirimli",-1),("Vitrin",-1),("KategoriID",0),("MarkaID",0),("UrunKartiID",0));
        if(q.Search is{Length:>0}){var key=q.SearchType switch{"SKU"=>"StokKodu","BARCODE"=>"Barkod",_=>"UrunKartiID"};var contract=await soap.Contract(s,"UrunServis",ct);if(!contract.FilterFields("SelectUrun").Contains(key))throw new IntegrationException("unsupported_filter","Bu mağazanın Ticimax ürün filtresi seçilen kimlik aramasını desteklemiyor. Ürün kartı numarasıyla arayın.",400);if(key=="UrunKartiID"){if(!int.TryParse(q.Search,out var id)||id<1)throw new IntegrationException("invalid_id","Ürün kartı numarası geçersiz.",400);f[key]=id;}else f[key]=q.Search;}
        return f;
    }
    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings s,CancellationToken ct){await Cards(s,new(Size:1),ct);return new("ticimax",soap.Base(s).Host,s.Environment,"TR",[],DateTimeOffset.UtcNow);}
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s,CancellationToken ct)=>TestAsync(s,ct);
    public async Task<TicimaxPage<TicimaxCard>> Cards(ConnectionSettings s,TicimaxQuery q,CancellationToken ct)
    {
        var contract=await soap.Contract(s,"UrunServis",ct);contract.RequirePaging("SelectUrun");if(q.Active.HasValue&&!contract.FilterFields("SelectUrun").Contains("Aktif"))throw new IntegrationException("unsupported_filter","Yayın durumu filtresi desteklenmiyor.",400);var f=await ProductFilter(s,q,ct);var result=await soap.Call(s,"UrunServis","SelectUrun",Fields(("$filter",f),("$paging",Paging(q))),ct);var rows=result.Elements().Select(MapCard).ToArray();return new(rows,q.Page,q.Size,rows.Length==q.Size);
    }
    public static TicimaxCard MapCard(XElement e)
    {
        if(Tx.S(e,"ID").Length==0||Tx.S(e,"UrunAdi").Length==0)throw new IntegrationException("invalid_response","Ticimax ürün kartı okunamadı.");var variants=Tx.F(e,"Varyasyonlar")?.Elements().Select(v=>new TicimaxVariant(Tx.S(v,"ID"),Tx.S(v,"StokKodu"),Tx.S(v,"Barkod"),Tx.N(v,"StokAdedi"),Tx.N(v,"SatisFiyati"),Tx.N(v,"IndirimliFiyati"),Tx.S(v,"ParaBirimiID"),Tx.B(v,"KdvDahil"),Tx.N(v,"KdvOrani"),Tx.F(v,"Ozellikler")?.Elements().Where(a=>Tx.S(a,"Tanim").Length>0).GroupBy(a=>Tx.S(a,"Tanim")).ToDictionary(a=>a.Key,a=>Tx.S(a.Last(),"Deger"))??new())).ToArray()??[];
        return new(Tx.S(e,"ID"),Tx.S(e,"UrunAdi"),Tx.S(e,"Marka"),Tx.S(e,"AnaKategori"),Tx.S(e,"Aciklama"),Tx.B(e,"Aktif"),Tx.F(e,"Resimler")?.Elements().Select(x=>x.Value).ToArray()??[],variants);
    }
    public async Task<TicimaxPage<TicimaxOrder>> Orders(ConnectionSettings s,TicimaxQuery q,CancellationToken ct)
    {
        var contract=await soap.Contract(s,"SiparisServis",ct);contract.RequirePaging("SelectSiparis");var supported=contract.FilterFields("SelectSiparis");foreach(var key in new[]{q.Search is {Length:>0}?"SiparisNo":null,q.Status.HasValue?"SiparisDurumu":null,q.OrderId.HasValue?"SiparisID":null,q.StartDate.HasValue?"SiparisTarihiBas":null,q.EndDate.HasValue?"SiparisTarihiSon":null}.OfType<string>())if(!supported.Contains(key))throw new IntegrationException("unsupported_filter","Seçilen sipariş filtresi mağaza sözleşmesinde desteklenmiyor.",400);var f=Fields(("EntegrasyonAktarildi",-1),("EntegrasyonParams",Fields(("EntegrasyonParamsAktif",false))),("IptalEdilmisUrunler",true),("OdemeDurumu",-1),("OdemeTipi",-1),("SiparisDurumu",q.Status??-1),("SiparisID",q.OrderId??-1),("TedarikciID",-1),("UyeID",-1),("SiparisNo",q.Search??""));
        if(q.StartDate.HasValue){f["SiparisTarihiBas"]=DateTimeOffset.FromUnixTimeMilliseconds(q.StartDate.Value).ToOffset(TimeSpan.FromHours(3)).ToString("O");f["SiparisTarihiSon"]=DateTimeOffset.FromUnixTimeMilliseconds(q.EndDate!.Value).ToOffset(TimeSpan.FromHours(3)).ToString("O");}
        var result=await soap.Call(s,"SiparisServis","SelectSiparis",Fields(("$filter",f),("$paging",Paging(q))),ct);var rows=result.Elements().Select(MapOrder).ToArray();return new(rows,q.Page,q.Size,rows.Length==q.Size);
    }
    public static TicimaxOrder MapOrder(XElement e)
    {
        if(Tx.S(e,"ID").Length==0)throw new IntegrationException("invalid_response","Ticimax sipariş kaydı okunamadı.");var address=Tx.F(e,"TeslimatAdresi");var currency=Tx.S(e,"ParaBirimi");return new(Tx.S(e,"ID"),Tx.S(e,"SiparisNo") is{Length:>0} number?number:Tx.S(e,"SiparisKodu"),(int?)Tx.N(e,"Durum"),Tx.S(e,"SiparisDurumu"),Tx.D(e,"SiparisTarihi"),Tx.S(e,"AdiSoyadi"),Tx.S(address,"Adres"),currency,Tx.N(e,"SiparisToplamTutari")??Tx.N(e,"ToplamTutar"),Tx.N(e,"IndirimTutari"),Tx.S(e,"FaturaNo"),Tx.S(e,"KargoTakipNo"),Tx.S(e,"SiparisNotu"),Tx.F(e,"Odemeler")?.Elements().Select(p=>Payment(p,currency)).ToArray()??[]);
    }
    public static TicimaxPayment Payment(XElement e,string currency="")=>new(Tx.S(e,"ID"),(int?)Tx.N(e,"OdemeTipi"),(int?)Tx.N(e,"Onaylandi"),Tx.N(e,"Tutar"),currency,Tx.D(e,"Tarih"));
    public async Task<object> Read(ConnectionSettings s,string kind,TicimaxQuery q,CancellationToken ct)
    {
        if(Validate(q) is{} error)throw new IntegrationException("invalid_query",error,400);
        if(kind is "products" or "inventory")return await Cards(s,q,ct);
        if(kind is "orders" or "finance" or "invoices" or "addresses" or "claims")return await Orders(s,kind=="claims"?q with{Status=q.Status??9}:q,ct);
        if(kind is "payments" or "lines" or "shipping")
        {
            if(q.OrderId is null)throw new IntegrationException("order_required","Önce sipariş listesinden bir kayıt seçin.",400);
            string operation;Dictionary<string,object?> args;
            if(kind=="shipping"){if(!(await soap.Contract(s,"SiparisServis",ct)).FilterFields("SelectSiparisKargoPaket").Contains("SiparisID"))throw new IntegrationException("unsupported_filter","Siparişe göre kargo paketi filtresi desteklenmiyor.",400);operation="SelectSiparisKargoPaket";args=Fields(("$filter",Fields(("SiparisID",q.OrderId),("SiparisKargoPaketID",-1),("KargoEntegrasyonID",-1),("KargoTakipNoDurum",-1))));}
            else{operation=kind=="payments"?"SelectSiparisOdeme":"SelectSiparisUrun";args=kind=="payments"?Fields(("SiparisId",q.OrderId),("OdemeId",0)):Fields(("SiparisId",q.OrderId),("IptalEdilmisUrunler",true));}
            var result=await soap.Call(s,"SiparisServis",operation,args,ct);
            if(kind=="payments"){var all=result.Elements().Select(p=>Payment(p)).ToArray();return new TicimaxPage<TicimaxPayment>(all.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,all.Length>(q.Page+1)*q.Size,all.Length);}
            if(kind=="shipping")return new TicimaxPage<TicimaxPackage>(result.Elements().Select(e=>new TicimaxPackage(Tx.S(e,"ID"),Tx.S(e,"SiparisID"),Tx.S(e,"KargoEntegrasyonTanim"),Tx.S(e,"KargoTakipNumarasi"),Tx.S(e,"KargoTakipLink"),Tx.S(e,"BarkodBilgisi"))).Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size);
            var rows=result.Elements().Select(e=>new{id=Tx.S(e,"ID"),name=Tx.S(e,"UrunAdi"),sku=Tx.S(e,"StokKodu"),barcode=Tx.S(e,"Barkod"),quantity=Tx.N(e,"Adet"),amount=Tx.N(e,"Tutar"),vat=Tx.N(e,"KdvOrani")}).ToArray();return new TicimaxPage<object>(rows.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,rows.Length>(q.Page+1)*q.Size,rows.Length);
        }
        if(kind is "catalog" or "brands" or "currencies" or "suppliers")
        {
            var (operation,idKey)=kind switch{"brands"=>("SelectMarka","markaID"),"currencies"=>("SelectParaBirimi","paraBirimiID"),"suppliers"=>("SelectTedarikci","tedarikciID"),_=>("SelectKategori","kategoriID")};var result=await soap.Call(s,"UrunServis",operation,Fields((idKey,0)),ct);
            var rows=result.Elements().Select(e=>new{id=Tx.S(e,"ID"),name=Tx.S(e,"Tanim"),parentId=Tx.S(e,"PID"),currency=Tx.S(e,"DovizKodu"),active=Tx.B(e,"Aktif")}).Where(e=>string.IsNullOrWhiteSpace(q.Search)||e.name.Contains(q.Search,StringComparison.OrdinalIgnoreCase)).ToArray();return new TicimaxPage<object>(rows.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,rows.Length>(q.Page+1)*q.Size,rows.Length);
        }
        throw new IntegrationException("not_available","Bu bölümün gerçek Ticimax işlemleri henüz tamamlanmadı.",501);
    }
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s,ProductQuery q,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(q.StockCode)&&string.IsNullOrWhiteSpace(q.Barcode))throw new IntegrationException("identifier_required","Varyant stoklarını karşılaştırmak için tam SKU veya barkod gerekli.",400);var query=new TicimaxQuery(q.StockCode??q.Barcode,q.StockCode!=null?"SKU":"BARCODE",Page:q.Page,Size:25);var page=await Cards(s,query,ct);
        var rows=page.Items.SelectMany(c=>c.Variants.Where(v=>q.StockCode!=null?v.Sku==q.StockCode:v.Barcode==q.Barcode).Select(v=>new MarketplaceProduct(v.Id,c.Id,c.Id,v.Sku,v.Barcode,c.Name,"",c.Category,c.Brand,null,null,null,v.Quantity is{} n&&n==decimal.Truncate(n)&&n<=int.MaxValue&&n>=0?(int)n:null,"",c.Active,"",c.Images,"unknown",[]))).ToArray();return new(rows,q.Page,25,null,null,page.HasMore?"page:"+(q.Page+1):null);
    }
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s,OrderQuery q,CancellationToken ct)
    {
        var page=await Orders(s,new(Search:q.OrderNumber,StartDate:q.StartDate,EndDate:q.EndDate,Page:q.Page),ct);return new(page.Items.Select(o=>new MarketplaceOrder("",o.Number,o.StatusName,o.Date,o.CustomerName,"",o.Total,null,o.Discount,o.Currency,"","",null,[],new("",o.TrackingNumber,"","","",null,null),new(o.InvoiceNumber,"Bildirilmedi","",false,"","","",null,[]))).ToArray(),q.Page,25,null,null,page.HasMore?"page:"+(q.Page+1):null);
    }
}
internal static class Tx
{
    public static XElement? F(XElement? e,string k)=>e?.Elements().FirstOrDefault(x=>x.Name.LocalName.Equals(k,StringComparison.OrdinalIgnoreCase));public static string S(XElement? e,string k)=>F(e,k)?.Value??"";public static decimal? N(XElement? e,string k)=>decimal.TryParse(S(e,k),NumberStyles.Any,CultureInfo.InvariantCulture,out var n)?n:null;public static bool? B(XElement? e,string k)=>bool.TryParse(S(e,k),out var b)?b:null;public static DateTimeOffset? D(XElement? e,string k)=>DateTimeOffset.TryParse(S(e,k),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)&&d.Year>1900?d:null;
}
