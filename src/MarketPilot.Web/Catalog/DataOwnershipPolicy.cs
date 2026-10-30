using System.Text.Json.Serialization;

namespace MarketPilot.Web.Catalog;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceSystem
{
    NebimV3,
    Wms,
    Marketplace,
    Cargo,
    MarketPilot,
    Unassigned
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncDirection
{
    NebimToMarketPilotToMarketplace,
    ConfiguredStockSourceToMarketPilotToMarketplace,
    WmsToMarketPilot,
    NebimIntegratedWmsToMarketPilot,
    MarketPilotDerivedToMarketplace,
    MarketplaceToMarketPilotToErp,
    MarketplaceToMarketPilotToErpThenWms,
    WmsToMarketPilotToCargoToMarketplace,
    MarketplaceToErpToMarketPilot
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictResolution
{
    AuthoritativeSourceWins,
    ConfiguredStockAuthorityWins,
    WmsReservationAuthorityWins,
    MarketPilotCalculates,
    MarketplaceSourceWins,
    ErpThenWms,
    WmsOwnsFulfillmentAndCargoReportsTracking,
    ErpOwnsCommercialDocument,
    RejectUntilReconciled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OwnershipField
{
    ProductCard,
    Variant,
    Barcode,
    Sku,
    Category,
    Brand,
    Price,
    PhysicalStock,
    Reservation,
    SellableStock,
    Order,
    Shipment,
    Return,
    Invoice
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockValueState
{
    NotProvided,
    Unknown,
    Known
}

public sealed record StockValue(StockValueState State, decimal? Quantity)
{
    public static StockValue NotProvided { get; } = new(StockValueState.NotProvided, null);
    public static StockValue Unknown { get; } = new(StockValueState.Unknown, null);
    public static StockValue Known(decimal quantity) => new(StockValueState.Known, quantity);

    public bool IsValid() => State switch
    {
        StockValueState.NotProvided or StockValueState.Unknown => Quantity is null,
        StockValueState.Known => Quantity is >= 0,
        _ => false
    };
}

public sealed record ExternalReference(
    SourceSystem System,
    string EntityType,
    string ExternalId,
    long? SourceVersion,
    DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset ObservedAt,
    string ContentHash,
    string? WarehouseCode = null);

public sealed record DataOwnershipPolicy(
    OwnershipField Field,
    string DisplayName,
    SourceSystem Owner,
    SourceSystem[] Readers,
    SourceSystem[] Writers,
    SyncDirection Direction,
    ConflictResolution ConflictResolution,
    string VersionOrTime,
    string DuplicateBehavior,
    string MissingDataBehavior,
    string UserStatus)
{
    private static readonly SourceSystem[] ProductReaders =
        [SourceSystem.NebimV3, SourceSystem.MarketPilot, SourceSystem.Marketplace, SourceSystem.Wms];
    private static readonly DataOwnershipPolicy[] Policies =
    [
        new(OwnershipField.ProductCard, "Ürün kartı", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP kaynak sürümü veya güncelleme zamanı", "Aynı kaynak sürümü/hash tekrarında no-op", "Eksik alan mevcut değeri silmez; açık null yalnız alan bilinmiyorsa saklanır", "ERP'den alındı / kaynak güncellemesi bekliyor"),
        new(OwnershipField.Variant, "Varyant", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP varyant kimliği ve kaynak sürümü/zamanı", "Varyant dış kimliği ve hash ile tekilleştirilir", "Eksik varyant mevcut varyantı silmez", "ERP varyantı eşleştirildi / eşleşme bekliyor"),
        new(OwnershipField.Barcode, "Barkod", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP ürün/varyant dış kimliği ve kaynak sürümü/zamanı", "Aynı dış kimlik/hash tekrarında no-op; çakışan barkod bloke edilir", "Eksik veya boş barkod yeni değer olarak kabul edilmez", "ERP barkodu doğrulandı / çakışma bloke edildi"),
        new(OwnershipField.Sku, "SKU", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP ürün/varyant dış kimliği ve kaynak sürümü/zamanı", "Aynı dış kimlik/hash tekrarında no-op; çakışan SKU bloke edilir", "Eksik veya boş SKU yeni değer olarak kabul edilmez", "ERP SKU'su doğrulandı / çakışma bloke edildi"),
        new(OwnershipField.Category, "Kategori", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP kaynak sürümü veya güncelleme zamanı", "Aynı sürüm/hash tekrarında no-op", "Eksik kategori mevcut değeri korur; pazar yeri kategorisi ERP alanını yazamaz", "ERP kategorisi alındı / pazar yeri eşlemesi bekliyor"),
        new(OwnershipField.Brand, "Marka", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP kaynak sürümü veya güncelleme zamanı", "Aynı sürüm/hash tekrarında no-op", "Eksik marka mevcut değeri korur", "ERP markası alındı"),
        new(OwnershipField.Price, "Fiyat", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.NebimToMarketPilotToMarketplace, ConflictResolution.AuthoritativeSourceWins, "ERP fiyat listesi + kaynak sürümü/zamanı", "Aynı fiyat, para birimi, KDV ve sürüm/hash tekrarında no-op", "Fiyat yoksa bilinmiyor olarak kalır; para birimi ve KDV tahmin edilmez", "ERP fiyatı alındı / fiyat, para birimi veya KDV bilinmiyor"),
        new(OwnershipField.PhysicalStock, "Fiziksel stok", SourceSystem.NebimV3, ProductReaders, [SourceSystem.NebimV3], SyncDirection.ConfiguredStockSourceToMarketPilotToMarketplace, ConflictResolution.ConfiguredStockAuthorityWins, "Seçili stok kaynağının depo + kaynak sürümü/zamanı", "Kaynak/hash tekrarında no-op; eski veya sırasız farklı snapshot bloke edilir", "NotProvided mevcut değeri korur; Unknown bilinmiyor olur; Known(0) gerçek sıfırdır", "ERP/WMS kaynağı doğrulandı / stok bilinmiyor / çakışma bloke edildi"),
        new(OwnershipField.Reservation, "Rezervasyon", SourceSystem.Wms, [SourceSystem.Wms, SourceSystem.NebimV3, SourceSystem.MarketPilot], [SourceSystem.Wms], SyncDirection.WmsToMarketPilot, ConflictResolution.WmsReservationAuthorityWins, "WMS rezervasyon kimliği, sürümü ve güncelleme zamanı", "Rezervasyon dış kimliği/idempotency anahtarı tekilleştirilir", "Eksik rezervasyon sıfır kabul edilmez; önceki rezervasyon korunur", "WMS rezervasyonu alındı / teyit bekliyor"),
        new(OwnershipField.SellableStock, "Satılabilir stok", SourceSystem.MarketPilot, [SourceSystem.MarketPilot, SourceSystem.Marketplace, SourceSystem.NebimV3, SourceSystem.Wms], [SourceSystem.MarketPilot], SyncDirection.MarketPilotDerivedToMarketplace, ConflictResolution.MarketPilotCalculates, "Fiziksel stok + WMS rezervasyon revizyonu + güvenlik stoğu", "Aynı kaynak revizyonu ve hesaplama girdisi tekrarında no-op", "Girdi bilinmiyorsa bilinmiyor kalır; sıfıra/stoğa varsayılan yapılmaz", "Hesaplandı / kaynak verisi eksik / kanal gönderimi bekliyor"),
        new(OwnershipField.Order, "Sipariş", SourceSystem.Marketplace, [SourceSystem.Marketplace, SourceSystem.MarketPilot, SourceSystem.NebimV3, SourceSystem.Wms], [SourceSystem.Marketplace, SourceSystem.NebimV3, SourceSystem.Wms], SyncDirection.MarketplaceToMarketPilotToErpThenWms, ConflictResolution.ErpThenWms, "Pazaryeri sipariş dış kimliği + kaynak sürümü/zamanı", "Kaynak hesap + sipariş dış kimliği + olay kimliğiyle idempotent", "Eksik/çelişkili satır veya ürün eşleşmesi bloke edilir; sipariş silinmez", "Alındı / ERP onayı bekliyor / WMS'ye hazır / bloke"),
        new(OwnershipField.Shipment, "Gönderi", SourceSystem.Wms, [SourceSystem.Wms, SourceSystem.Cargo, SourceSystem.Marketplace, SourceSystem.MarketPilot], [SourceSystem.Wms, SourceSystem.Cargo], SyncDirection.WmsToMarketPilotToCargoToMarketplace, ConflictResolution.WmsOwnsFulfillmentAndCargoReportsTracking, "Sipariş + paket dış kimliği ve WMS/kargo kaynak sürümü/zamanı", "Paket/kargo dış kimliği ve idempotency anahtarıyla tekilleştirilir", "Eksik paket doğrulaması gönderi/kargo oluşturmaz", "Paket doğrulanıyor / kargo takibi bekleniyor / bloke"),
        new(OwnershipField.Return, "İade", SourceSystem.Marketplace, [SourceSystem.Marketplace, SourceSystem.Wms, SourceSystem.NebimV3, SourceSystem.MarketPilot], [SourceSystem.Marketplace, SourceSystem.Wms, SourceSystem.NebimV3], SyncDirection.MarketplaceToErpToMarketPilot, ConflictResolution.ErpThenWms, "İade dış kimliği + marketplace, ERP ve WMS kaynak sürümleri/zamanları", "İade dış kimliği ve olay kimliğiyle idempotent; stok iade kabulü ayrı olaydır", "Eksik kabul/ürün/kalite kontrol bilgisi stok artırmaz", "İade talebi / WMS incelemesi / ERP alacak onayı bekliyor"),
        new(OwnershipField.Invoice, "Fatura", SourceSystem.NebimV3, [SourceSystem.NebimV3, SourceSystem.Marketplace, SourceSystem.MarketPilot], [SourceSystem.NebimV3, SourceSystem.Marketplace], SyncDirection.MarketplaceToErpToMarketPilot, ConflictResolution.ErpOwnsCommercialDocument, "ERP belge dış kimliği, belge numarası ve kaynak sürümü/zamanı", "ERP belge kimliği + sipariş/iadeye bağlı idempotency anahtarı", "Eksik tutar, para birimi veya KDV doldurulmaz; belge oluşturma/aktarım bloke edilir", "ERP belgesi alındı / alanlar eksik / eşleşme bekliyor")
    ];

    public static IReadOnlyList<DataOwnershipPolicy> All => Policies;

    public static DataOwnershipPolicy For(OwnershipField field, SourceSystem stockAuthority = SourceSystem.NebimV3, bool nebimIntegratedWms = false)
    {
        var policy = Policies.Single(p => p.Field == field);
        if (field == OwnershipField.PhysicalStock)
            return policy with { Owner = stockAuthority, Writers = [stockAuthority] };
        if (field == OwnershipField.Reservation && nebimIntegratedWms)
            return policy with { Owner = SourceSystem.NebimV3, Writers = [SourceSystem.NebimV3], Direction = SyncDirection.NebimIntegratedWmsToMarketPilot };
        return policy;
    }

    public static bool IsWriter(OwnershipField field, SourceSystem source, SourceSystem stockAuthority = SourceSystem.NebimV3, bool nebimIntegratedWms = false) =>
        For(field, stockAuthority, nebimIntegratedWms).Writers.Contains(source);

    public static string StatusFor(OwnershipField field, StockValueState? stockState = null, SourceSystem stockAuthority = SourceSystem.NebimV3) =>
        field == OwnershipField.PhysicalStock && stockState == StockValueState.Unknown
            ? $"{For(field, stockAuthority).Owner} stok bilinmiyor"
            : For(field, stockAuthority).UserStatus;

    public static OwnershipDecision Resolve(OwnershipField field, SourceSystem incomingSource, SourceSystem stockAuthority,
        ExternalReference? current, ExternalReference incoming, bool nebimIntegratedWms = false)
    {
        var policy = For(field, stockAuthority, nebimIntegratedWms);
        if (!policy.Writers.Contains(incomingSource) || incoming.System != incomingSource)
            return OwnershipDecision.NotOwner;
        if (current is null)
            return OwnershipDecision.Accept;
        if (current.System != incoming.System)
            return OwnershipDecision.Accept;

        if (current.SourceVersion.HasValue && incoming.SourceVersion.HasValue)
        {
            if (incoming.SourceVersion < current.SourceVersion) return OwnershipDecision.Stale;
            if (incoming.SourceVersion == current.SourceVersion)
                return current.ContentHash == incoming.ContentHash ? OwnershipDecision.Duplicate : OwnershipDecision.Conflict;
            return current.ContentHash == incoming.ContentHash ? OwnershipDecision.Duplicate : OwnershipDecision.Accept;
        }

        if (current.SourceUpdatedAt.HasValue && incoming.SourceUpdatedAt.HasValue)
        {
            if (incoming.SourceUpdatedAt < current.SourceUpdatedAt) return OwnershipDecision.Stale;
            if (incoming.SourceUpdatedAt == current.SourceUpdatedAt)
                return current.ContentHash == incoming.ContentHash ? OwnershipDecision.Duplicate : OwnershipDecision.Conflict;
            return current.ContentHash == incoming.ContentHash ? OwnershipDecision.Duplicate : OwnershipDecision.Accept;
        }

        return current.ContentHash == incoming.ContentHash ? OwnershipDecision.Duplicate : OwnershipDecision.Conflict;
    }
}

public enum OwnershipDecision
{
    Accept,
    Duplicate,
    Stale,
    Conflict,
    NotOwner
}
