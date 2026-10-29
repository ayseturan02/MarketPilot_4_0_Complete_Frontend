namespace MarketPilot.Web.Enterprise;

public interface IErpCatalogProvider
{
    Task<ErpItem[]> ReadCatalogAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken);
}

public interface IWmsProvider
{
    Task<ErpItem[]> ReadStockAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken);
    Task<WmsReservationSnapshot[]> ReadReservationsAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken);
    Task<WmsDispatch[]> ReadDispatchesAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken);
}

public interface ICargoProvider
{
    Task<BridgeShipment[]> ReadShipmentsAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken);
}

public sealed class NebimBridgeProvider(EnterpriseBridge bridge) : IErpCatalogProvider
{
    public Task<ErpItem[]> ReadCatalogAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken) =>
        bridge.ReadAll<ErpItem>("erp", "catalog", warehouseCode, priceListCode, cancellationToken);
}

public sealed class WmsBridgeProvider(EnterpriseBridge bridge) : IWmsProvider
{
    public Task<ErpItem[]> ReadStockAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken) =>
        bridge.ReadAll<ErpItem>("wms", "stock", warehouseCode, priceListCode, cancellationToken);

    public Task<WmsReservationSnapshot[]> ReadReservationsAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken) =>
        bridge.ReadAll<WmsReservationSnapshot>("wms", "reservations", warehouseCode, priceListCode, cancellationToken);

    public Task<WmsDispatch[]> ReadDispatchesAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken) =>
        bridge.ReadAll<WmsDispatch>("wms", "dispatches", warehouseCode, priceListCode, cancellationToken);
}

public sealed class CargoBridgeProvider(EnterpriseBridge bridge) : ICargoProvider
{
    public Task<BridgeShipment[]> ReadShipmentsAsync(string warehouseCode, string priceListCode, CancellationToken cancellationToken) =>
        bridge.ReadAll<BridgeShipment>("cargo", "shipments", warehouseCode, priceListCode, cancellationToken);
}
