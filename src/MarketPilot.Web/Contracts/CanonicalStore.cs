using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Contracts;

// Uses DemoStore.Modify: records, receipts and review entries commit/rollback together.
public sealed class CanonicalStore(DemoStore db,IConfiguration configuration)
{
    public string TenantId=>configuration["Workspace:BusinessId"]??"default";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static readonly IReadOnlyDictionary<string,Type> Types=new Dictionary<string,Type>(StringComparer.Ordinal)
    {
        ["products"]=typeof(CanonicalProduct),["variants"]=typeof(ProductVariant),["barcodes"]=typeof(Barcode),
        ["mappings"]=typeof(ExternalProductMapping),["warehouses"]=typeof(Warehouse),["stock"]=typeof(StockPosition),
        ["reservations"]=typeof(StockReservation),["price-lists"]=typeof(PriceList),["orders"]=typeof(MarketplaceOrder),
        ["fulfillments"]=typeof(FulfillmentRequest),["shipments"]=typeof(Shipment),["returns"]=typeof(ReturnRequest),
        ["invoices"]=typeof(InvoiceReference),["events"]=typeof(IntegrationEvent),["checkpoints"]=typeof(SyncCheckpoint)
    };
    private static IntegrationException Error(string message,int status=400)=>new("canonical_contract",message,status);
    private static bool Present(string? value,int max=200)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value==value.Trim();
    private static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid,string message){if(!valid)throw Error(message);}
    private static string Key(params string?[] values)=>System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    private static string Fingerprint(CanonicalRecord value)
    {
        var node=JsonSerializer.SerializeToNode(value,value.GetType(),Json)!;
        var context=node["context"]!.AsObject();context.Remove("receivedAt");context.Remove("correlationId");context.Remove("idempotencyKey");
        using var buffer=new MemoryStream();using(var writer=new Utf8JsonWriter(buffer))WriteCanonical(JsonSerializer.SerializeToElement(node),writer);
        return System.Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
    private static void WriteCanonical(JsonElement value,Utf8JsonWriter writer)
    {
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();foreach(var p in value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)){writer.WritePropertyName(p.Name);WriteCanonical(p.Value,writer);}writer.WriteEndObject();break;
            case JsonValueKind.Array:
                writer.WriteStartArray();foreach(var item in value.EnumerateArray())WriteCanonical(item,writer);writer.WriteEndArray();break;
            case JsonValueKind.Number when value.TryGetDecimal(out var number):
                writer.WriteRawValue(number.ToString("G29",System.Globalization.CultureInfo.InvariantCulture));break;
            default:value.WriteTo(writer);break;
        }
    }
    public static CanonicalRecord Parse(string kind,JsonElement payload)
    {
        if(!Types.TryGetValue(kind,out var type))throw Error("Bilinmeyen ortak veri türü.",404);
        try{return (CanonicalRecord?)payload.Deserialize(type,Json)??throw Error("Kayıt boş olamaz.");}
        catch(JsonException){throw Error("Ortak veri sözleşmesinin alan tipleri geçersiz.");}
    }
    private IEnumerable<T> Rows<T>(ContractState state) where T:CanonicalRecord=>state.Records.Values.Select(e=>e.Deserialize<StoredRecord>(Json)!).Where(e=>Types.GetValueOrDefault(e.Kind)==typeof(T)).Select(e=>(T)Parse(e.Kind,e.Payload)).Where(r=>r.Context.TenantId==TenantId);
    private T Find<T>(ContractState state,string id) where T:CanonicalRecord=>Rows<T>(state).SingleOrDefault(r=>r.Id==id)??throw new IntegrationException("canonical_missing_reference","Bağlı kayıt bulunamadı: "+typeof(T).Name+" / "+id,409);
    private static string Account(RecordContext c)=>c.ExternalAccountId??"";
    private static bool SameSource(RecordContext a,RecordContext b)=>a.TenantId==b.TenantId&&a.SourceSystem==b.SourceSystem&&Account(a)==Account(b)&&a.SourceRecordId==b.SourceRecordId;
    private static bool Active(StockReservation r)=>r.Status=="held"&&(!r.ExpiresAt.HasValue||r.ExpiresAt>DateTimeOffset.UtcNow);
    private static bool Currency(string? value)=>value?.Length==3&&value.All(c=>c is >= 'A' and <= 'Z');
    private decimal Convert(ContractState state,string variantId,decimal quantity,string unit)
    {
        var v=Find<ProductVariant>(state,variantId);
        Need(Present(unit,30),"Birim kodu gerekli.");
        var factor=unit==v.BaseUnit?1:v.Conversions.SingleOrDefault(c=>c.UnitCode==unit)?.BaseUnits??throw Error("Birim dönüşümü tanımlı değil.");
        decimal result;try{result=checked(quantity*factor);}catch(OverflowException){throw Error("Birim dönüşüm miktarı sınırı aşıyor.");}
        Need(result>=0&&result<=1000000000000m&&(!v.WholeUnitsOnly||decimal.Truncate(result)==result),"Stok miktarı veya tam sayı birim kuralı geçersiz.");return result;
    }
    public AvailableToSell Available(string variantId,string warehouseId)=>db.CatalogRead(s=>Calculate(s.Contracts,variantId,warehouseId));
    private AvailableToSell Calculate(ContractState state,string variantId,string warehouseId,string? except=null)
    {
        var v=Find<ProductVariant>(state,variantId);var warehouse=Find<Warehouse>(state,warehouseId);
        var positions=Rows<StockPosition>(state).Where(p=>p.VariantId==variantId&&p.WarehouseId==warehouseId).ToArray();
        var onHand=positions.Sum(p=>Convert(state,variantId,p.Quantity,p.UnitCode));
        var safety=positions.Sum(p=>Convert(state,variantId,p.SafetyStock,p.UnitCode));
        var reserved=Rows<StockReservation>(state).Where(r=>r.Id!=except&&r.VariantId==variantId&&r.WarehouseId==warehouseId&&Active(r)).Sum(r=>Convert(state,variantId,r.Quantity,r.UnitCode));
        return new(TenantId,variantId,warehouseId,v.BaseUnit,positions.Length==0?null:onHand,reserved,positions.Length==0?null:safety,!warehouse.Active?0:positions.Length==0?null:Math.Max(0,onHand-reserved-safety),DateTimeOffset.UtcNow);
    }
    public ContractResult Apply(string kind,JsonElement payload,long? expectedVersion=null)=>Apply(Parse(kind,payload),expectedVersion);
    public ContractResult Apply(CanonicalRecord record,long? expectedVersion=null)
    {
        var kind=Types.SingleOrDefault(p=>p.Value==record.GetType()).Key??throw Error("Bilinmeyen model.");
        Need(record.Context!=null&&Present(record.Id)&&record.Context.SourceVersion>0,"Kayıt kimliği, bağlam ve pozitif kaynak sürümü gerekli.");
        var c=record.Context;
        if(c.TenantId!=TenantId)throw Error("Başka işletmenin verisine erişim reddedildi.",403);
        Need(Present(c.SourceSystem,100)&&Present(c.CorrelationId)&&Present(c.IdempotencyKey)&&c.OccurredAt!=default&&c.ReceivedAt!=default&&c.ReceivedAt>=c.OccurredAt,"Kaynak, ilişkilendirme/tekrar işlem kimliği ve zaman damgaları gerekli.");
        Need(c.ExternalAccountId==null||Present(c.ExternalAccountId),"Satıcı hesap kimliği geçersiz.");
        var fingerprint=Fingerprint(record);var key=Key(TenantId,kind,record.Id);var receiptKey=Key(TenantId,c.SourceSystem,Account(c),kind,c.IdempotencyKey);
        return db.Modify(s=>{
            var state=s.Contracts;
            if(state.Receipts.TryGetValue(receiptKey,out var receipt))
            {
                if(receipt.Fingerprint!=fingerprint)throw Error("İşlem anahtarı farklı veriyle tekrar kullanıldı.",409);
                return new ContractResult(receipt.State=="applied"?"duplicate":receipt.State,receipt.RecordId,receipt.QueueId);
            }
            Need(state.Receipts.Count<100000,"Ortak veri işlem kayıt sınırı dolu.");
            CanonicalRecord? old=state.Records.TryGetValue(key,out var stored)?Parse(kind,stored.Deserialize<StoredRecord>(Json)!.Payload):null;
            string? reason=!Present(c.SourceRecordId)?"missing-source-identity":null;
            if(old!=null)
            {
                if(!SameSource(old.Context,c))reason="changed-source-identity";
                else if(c.SourceVersion<old.Context.SourceVersion)return Commit("stale");
                else if(c.SourceVersion==old.Context.SourceVersion){if(Fingerprint(old)!=fingerprint)throw Error("Aynı kaynak sürümü farklı veri içeriyor.",409);return Commit("duplicate");}
            }
            if(expectedVersion.HasValue&&(old?.Context.SourceVersion??0)!=expectedVersion)throw Error("Ürün/kayıt sürümü değişmiş; güncel kaydı tekrar okuyun.",409);
            if(reason==null&&state.Records.Values.Select(v=>v.Deserialize<StoredRecord>(Json)!).Where(v=>v.Kind==kind).Select(v=>Parse(kind,v.Payload)).Any(r=>r.Id!=record.Id&&SameSource(r.Context,c)))reason="source-identity-already-bound";
            if(reason==null)try{reason=Validate(state,record);}catch(IntegrationException e)when(e.Code=="canonical_missing_reference"){reason="missing-referenced-identity";}
            if(reason!=null)
            {
                Need(state.ManualMappings.Count<10000,"Manuel eşleştirme kuyruğu dolu.");
                var id=Guid.NewGuid().ToString("N");state.ManualMappings.Add(new(id,kind,record.Id,c,reason,JsonSerializer.SerializeToElement(record,record.GetType(),Json),DateTimeOffset.UtcNow));
                return Commit("manual-mapping",id);
            }
            Need(old!=null||state.Records.Count<100000,"Ortak veri kayıt sınırı dolu.");
            state.Records[key]=JsonSerializer.SerializeToElement(new StoredRecord(kind,JsonSerializer.SerializeToElement(record,record.GetType(),Json)),Json);
            return Commit("applied");
            ContractResult Commit(string status,string? queueId=null){state.Receipts[receiptKey]=new(fingerprint,status,record.Id,queueId);return new(status,record.Id,queueId);}
        });
    }
    private string? Validate(ContractState s,CanonicalRecord record)
    {
        switch(record)
        {
            case CanonicalProduct p:
                Need(Present(p.Name,300)&&Present(p.Category)&&Present(p.Brand)&&p.Status is "active" or "inactive" or "archived","Ürün adı, kategori, marka veya durum geçersiz.");break;
            case ProductVariant v:
                Find<CanonicalProduct>(s,v.ProductId);Need(Present(v.Sku,100)&&Present(v.Color,100)&&Present(v.Size,100)&&Present(v.BaseUnit,30)&&v.Conversions!=null&&v.Conversions.Count<=50&&v.Status is "active" or "inactive" or "archived","Varyant alanları geçersiz.");
                Need(v.Conversions.All(u=>u!=null&&Present(u.UnitCode,30)&&u.UnitCode!=v.BaseUnit&&u.BaseUnits>0&&u.BaseUnits<=1000000)&&v.Conversions.Select(u=>u.UnitCode).Distinct().Count()==v.Conversions.Count,"Birim dönüşümleri pozitif ve tekil olmalı.");
                if(Rows<ProductVariant>(s).Any(r=>r.Id!=v.Id&&r.Sku.Equals(v.Sku,StringComparison.OrdinalIgnoreCase)))return "sku-shared-by-variants";
                // Existing stock must stay representable after a unit policy edit.
                var prior=Rows<ProductVariant>(s).FirstOrDefault(r=>r.Id==v.Id);
                if(prior!=null&&(prior.BaseUnit!=v.BaseUnit||prior.WholeUnitsOnly!=v.WholeUnitsOnly||!prior.Conversions.SequenceEqual(v.Conversions))&&
                    (Rows<StockPosition>(s).Any(p=>p.VariantId==v.Id)||Rows<StockReservation>(s).Any(p=>p.VariantId==v.Id)))throw Error("Stok geçmişi varken birim politikası doğrudan değiştirilemez.",409);
                break;
            case Barcode b:
                Need(Present(b.Value,120)&&Present(b.Scheme,30),"Barkod değeri ve türü gerekli.");Convert(s,b.VariantId,1,b.UnitCode);
                if(Rows<Barcode>(s).Any(r=>r.Id==b.Id&&(r.VariantId!=b.VariantId||r.UnitCode!=b.UnitCode)))return "barcode-target-changed";
                if(Rows<Barcode>(s).Any(r=>r.Id!=b.Id&&r.Value==b.Value&&(r.VariantId!=b.VariantId||r.UnitCode!=b.UnitCode)))return "barcode-shared-by-variants";break;
            case ExternalProductMapping m:
                Find<ProductVariant>(s,m.VariantId);Need(Present(m.ExternalListingId)&&Present(m.Context.ExternalAccountId),"İlan ve satıcı hesap kimliği gerekli.");
                if(Rows<ExternalProductMapping>(s).Any(r=>r.Id==m.Id&&(r.VariantId!=m.VariantId||r.ExternalListingId!=m.ExternalListingId)))return "listing-target-changed";
                if(Rows<ExternalProductMapping>(s).Any(r=>r.Id!=m.Id&&r.Context.SourceSystem==m.Context.SourceSystem&&r.Context.ExternalAccountId==m.Context.ExternalAccountId&&r.ExternalListingId==m.ExternalListingId))return "listing-already-bound";
                if(!string.IsNullOrWhiteSpace(m.ExternalBarcode)&&Rows<Barcode>(s).Any(b=>b.Value==m.ExternalBarcode&&b.VariantId!=m.VariantId))return "listing-barcode-conflict";break;
            case Warehouse w:
                Need(Present(w.Code,50)&&Present(w.Name),"Depo kodu ve adı gerekli.");Need(!Rows<Warehouse>(s).Any(r=>r.Id!=w.Id&&r.Code==w.Code),"Depo kodu tekrar ediyor.");break;
            case StockPosition p:
                Find<Warehouse>(s,p.WarehouseId);Convert(s,p.VariantId,p.Quantity,p.UnitCode);Convert(s,p.VariantId,p.SafetyStock,p.UnitCode);
                Need(!Rows<StockPosition>(s).Any(r=>r.Id==p.Id&&(r.VariantId!=p.VariantId||r.WarehouseId!=p.WarehouseId)),"Stok kaydı başka depo/varyanta taşınamaz.");
                Need(!Rows<StockPosition>(s).Any(r=>r.Id!=p.Id&&r.VariantId==p.VariantId&&r.WarehouseId==p.WarehouseId),"Aynı depo/varyant için ikinci fiziksel stok kaynağı açılamaz.");break;
            case StockReservation r:
                Find<Warehouse>(s,r.WarehouseId);var qty=Convert(s,r.VariantId,r.Quantity,r.UnitCode);Need(qty>0&&Present(r.OrderId)&&r.Status is "held" or "released" or "consumed" or "expired","Rezervasyon miktarı, siparişi veya durumu geçersiz.");
                var previous=Rows<StockReservation>(s).FirstOrDefault(p=>p.Id==r.Id);
                Need(previous==null||previous.Status=="held"||r.Status==previous.Status,"Kapanan rezervasyon yeniden açılamaz.");
                Need(previous==null||previous.VariantId==r.VariantId&&previous.WarehouseId==r.WarehouseId&&previous.OrderId==r.OrderId,"Rezervasyon başka sipariş/depo/varyanta taşınamaz.");
                if(Active(r))Need(qty<=Calculate(s,r.VariantId,r.WarehouseId,r.Id).Quantity,"Yeterli satılabilir stok yok.");break;
            case PriceList p:
                Need(Present(p.Name)&&p.ValidFrom!=default&&(!p.ValidTo.HasValue||p.ValidTo>p.ValidFrom)&&p.Entries!=null&&p.Entries.Count is >0 and <=10000,"Fiyat listesi veya geçerlilik aralığı geçersiz.");
                foreach(var e in p.Entries){Need(e!=null&&e.Amount>=0&&e.Amount<=100000000&&Currency(e.Currency)&&e.VatRate is null or >=0 and <=100,"Fiyat veya para birimi geçersiz.");Convert(s,e.VariantId,1,e.UnitCode);}Need(p.Entries.Select(e=>(e.VariantId,e.Currency,e.UnitCode)).Distinct().Count()==p.Entries.Count,"Tekrar fiyat satırı.");break;
            case MarketplaceOrder o:
                Need(Present(o.Context.ExternalAccountId)&&Present(o.OrderNumber)&&Currency(o.Currency)&&o.OrderedAt!=default&&o.Status is "new" or "preparing" or "shipped" or "delivered" or "cancelled" or "returned","Sipariş üst bilgisi geçersiz.");
                Need(o.Lines!=null&&o.Lines.Count is >0 and <=1000&&o.Lines.All(l=>l!=null)&&o.Lines.Select(l=>l.Id).Distinct().Count()==o.Lines.Count,"Sipariş satırları tekil olmalı.");
                foreach(var l in o.Lines){Need(l!=null&&Present(l.Id)&&Present(l.ExternalLineId)&&l.Quantity>0&&l.UnitPrice>=0&&l.UnitPrice<=100000000&&l.Currency==o.Currency,"Sipariş satırı geçersiz.");Convert(s,l.VariantId,l.Quantity,l.UnitCode);if(Present(l.Barcode)&&!Rows<Barcode>(s).Any(b=>b.Value==l.Barcode&&b.VariantId==l.VariantId))return "order-line-barcode-unmapped";}break;
            case FulfillmentRequest f:
                var order=Find<MarketplaceOrder>(s,f.OrderId);Find<Warehouse>(s,f.WarehouseId);Need(f.Status is "requested" or "accepted" or "packed" or "cancelled" or "completed","Depo talep durumu geçersiz.");
                ValidateLines(s,order,f.Lines);break;
            case Shipment sh:
                var fulfillment=Find<FulfillmentRequest>(s,sh.FulfillmentRequestId);Need(fulfillment.WarehouseId==sh.WarehouseId&&Present(sh.Carrier)&&SafeUrl(sh.LabelUrl)&&sh.Status is "prepared" or "shipped" or "delivered" or "cancelled","Gönderi bilgileri geçersiz.");
                ValidateLines(s,Find<MarketplaceOrder>(s,fulfillment.OrderId),sh.Lines);break;
            case ReturnRequest r:
                Find<Warehouse>(s,r.WarehouseId);Need(r.Status is "requested" or "approved" or "received" or "rejected" or "completed","İade durumu geçersiz.");
                Need(r.Lines!=null&&r.Lines.All(l=>l!=null&&Present(l.Reason,500)),"İade nedeni gerekli.");ValidateLines(s,Find<MarketplaceOrder>(s,r.OrderId),r.Lines.Select(l=>new FulfillmentLine(l.OrderLineId,l.VariantId,l.Quantity,l.UnitCode)).ToArray());break;
            case InvoiceReference i:
                var invoiceOrder=Find<MarketplaceOrder>(s,i.OrderId);Need(Present(i.Number)&&i.Currency==invoiceOrder.Currency&&i.Amount>=0&&SafeUrl(i.DocumentUrl)&&i.DocumentType is "invoice" or "credit-note"&&i.Status is "draft" or "issued" or "cancelled","Belge referansı geçersiz.");break;
            case IntegrationEvent e:
                Need(Present(e.EventType)&&Present(e.EntityType)&&Present(e.EntityId)&&e.SchemaVersion==1&&e.Payload.ValueKind==JsonValueKind.Object&&e.Payload.GetRawText().Length<=100000,"Olay şeması geçersiz.");break;
            case SyncCheckpoint cp:
                Need(Present(cp.Stream)&&(cp.Cursor==null||cp.Cursor.Length<=4000)&&cp.LastSourceVersion>=0,"Kontrol noktası geçersiz.");var old=Rows<SyncCheckpoint>(s).FirstOrDefault(r=>r.Id==cp.Id);Need(old==null||cp.Stream==old.Stream&&cp.LastSourceVersion>=old.LastSourceVersion,"Kontrol noktası geriye alınamaz.");break;
        }
        return null;
    }
    private void ValidateLines(ContractState state,MarketplaceOrder order,IReadOnlyList<FulfillmentLine> lines)
    {
        Need(lines!=null&&lines.Count is >0 and <=1000&&lines.All(l=>l!=null)&&lines.Select(l=>l.OrderLineId).Distinct().Count()==lines.Count,"İşlem satırları geçersiz.");
        foreach(var line in lines){var source=order.Lines.SingleOrDefault(l=>l.Id==line.OrderLineId);Need(source!=null&&source.VariantId==line.VariantId&&line.Quantity>0,"Sipariş satır kimliği uyuşmuyor.");Need(Convert(state,line.VariantId,line.Quantity,line.UnitCode)<=Convert(state,source!.VariantId,source.Quantity,source.UnitCode),"İşlem miktarı sipariş miktarını aşıyor.");}
    }
    private static bool SafeUrl(string? url)=>string.IsNullOrEmpty(url)||Uri.TryCreate(url,UriKind.Absolute,out var value)&&value.Scheme=="https"&&value.UserInfo.Length==0;
    public object Read(string kind,int page=0,int size=25)
    {
        Need(Types.ContainsKey(kind)&&page>=0&&size is >=1 and <=100&&(long)page*size<=int.MaxValue,"Liste veya sayfa geçersiz.");
        return db.CatalogRead(s=>{var rows=s.Contracts.Records.Values.Select(v=>v.Deserialize<StoredRecord>(Json)!).Where(r=>r.Kind==kind&&Parse(kind,r.Payload).Context.TenantId==TenantId).OrderBy(r=>Parse(kind,r.Payload).Id).ToArray();return new{items=rows.Skip(page*size).Take(size).Select(r=>r.Payload.Clone()).ToArray(),totalElements=rows.Length,page,size};});
    }
    public MappingReview[] Reviews()=>db.CatalogRead(s=>s.Contracts.ManualMappings.Where(r=>r.Context.TenantId==TenantId).TakeLast(100).Reverse().ToArray());
    public MappingReview Resolve(string queueId,string variantId)=>db.Modify(s=>{
        var index=s.Contracts.ManualMappings.FindIndex(r=>r.Id==queueId&&r.Context.TenantId==TenantId);if(index<0)throw Error("Eşleştirme kuyruğu kaydı bulunamadı.",404);
        var review=s.Contracts.ManualMappings[index];Find<ProductVariant>(s.Contracts,variantId);
        if(review.State=="resolved"){if(review.ResolvedVariantId!=variantId)throw Error("Kayıt daha önce başka varyanta eşleştirildi.",409);return review;}
        // A review resolution is an explicit decision, not permission to overwrite ERP master data.
        var resolved=review with{State="resolved",ResolvedVariantId=variantId};s.Contracts.ManualMappings[index]=resolved;return resolved;
    });
    private sealed record StoredRecord(string Kind,JsonElement Payload);
}
