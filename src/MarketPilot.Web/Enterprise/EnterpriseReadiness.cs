using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;

// Local inspection only: no vendor requests, credentials or remote configuration changes.
public static class EnterpriseReadiness
{
    public static object Inspect(EnterpriseSync sync, EnterpriseBridge bridge, IntegrationRegistry registry, IConfiguration config)
    {
        var plan=sync.Plan;
        var checks=new List<ReadinessCheck>();
        void Add(string code,bool ready,string message)=>checks.Add(new(code,ready?"ready":"pending",message));
        var selected=plan.Targets.Where(t=>t.Enabled).ToArray();
        Add("targets",selected.Length>0,"En az bir hedef mağaza seçilmeli.");
        Add("warehouse",!string.IsNullOrWhiteSpace(plan.WarehouseCode),"Nebim / WMS depo kodu belirlenmeli.");
        Add("price-list",!string.IsNullOrWhiteSpace(plan.PriceListCode),"Nebim fiyat listesi kodu belirlenmeli.");
        Add("erp-source",bridge.Configured("erp")||!string.IsNullOrWhiteSpace(config["Enterprise:Erp:InboundKey"]),"ERP okuma bağlantısı veya imzalı katalog bildirimi tanımlanmalı.");
        foreach(var schedule in plan.Schedules.Where(s=>s.Enabled))
        {
            var connector=schedule.Task switch {"catalog" or "erp-orders"=>"erp","wms-stock" or "wms-orders" or "wms-dispatches"=>"wms","cargo-create" or "shipments"=>"cargo",_=>null};
            if(connector!=null)Add("task:"+schedule.Task,schedule.Task is "erp-orders" or "wms-orders" or "cargo-create"?bridge.Writes(connector):bridge.Configured(connector),"Görev için bağlantı ve gerekiyorsa yazma izni: "+schedule.Task);
        }
        if(plan.StockAuthority=="wms")Add("wms-stock",plan.Schedules.Any(s=>s.Task=="wms-stock"&&s.Enabled),"Stok sahibi WMS ise WMS stok alımı plana dahil edilmeli.");
        if(plan.Schedules.Any(s=>s.Task=="cargo-create"&&s.Enabled))Add("packing",plan.Schedules.Any(s=>s.Task=="wms-dispatches"&&s.Enabled),"Kargo oluşturmadan önce WMS paket onayı alınmalı.");
        foreach(var target in selected)
        {
            if(!registry.Connections.TryGetValue(target.StoreId,out var store)||target.Source!=CatalogHub.Source(store))
            {Add("store:"+target.StoreId,false,"Mağaza kaynağı değişmiş veya silinmiş; hedef planı yenilenmeli.");continue;}
            if(store.Settings.Mode=="demo")
            {checks.Add(new("store:"+target.StoreId,"demo","Bu hedef örnek mağazadır; canlı bağlantı sayılmaz."));continue;}
            try
            {
                var scoped=CredentialProfiles.For(config,store.Settings);
                Add("credentials:"+store.Id,CredentialProfiles.Configured(scoped,store.Settings.Marketplace,store.Settings.Environment),"Mağazaya uygun sunucu kimlik profili tanımlanmalı.");
                if(target.Orders)Add("reads:"+store.Id,config.GetValue<bool>("Synchronization:IncludeRealStores"),"Gerçek sipariş okuma izni açılmalı.");
                if(target.Stock||target.Price)Add("writes:"+store.Id,config.GetValue<bool>("Synchronization:AllowRealWrites")&&CredentialProfiles.Writes(scoped,store.Settings),"Stok/fiyat için genel ve mağaza yazma izinleri açılmalı.");
                if(target.Images||target.Products||target.Orders&&plan.Schedules.Any(s=>s.Enabled&&s.Task=="shipments"))Add("channel-bridge:"+store.Id,bridge.Writes("channel-"+store.Id),"Görsel / ürün kartı / kargo bildirimi için mağazaya özel ara servis tanımlanmalı.");
            }
            catch(IntegrationException){Add("credentials:"+store.Id,false,"Mağaza kimlik profili eksik veya ortamla eşleşmiyor.");}
        }
        return new {mode="offline-inspection",liveVerified=false,ready=checks.All(c=>c.State=="ready"),automaticEnabled=plan.Enabled,checks};
    }
}
public sealed record ReadinessCheck(string Code,string State,string Message);
