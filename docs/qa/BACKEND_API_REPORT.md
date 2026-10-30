# Backend ve API raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

Web build:0 hata/0 uyarı. Mevcut23 .NET paketinin tamamı geçti. Yeni21 API kontrolünde **20 PASS/1 FAIL**; tek beklenmeyen500 CredentialReference eksik System (MP-QA-003). Sonraki health200; süreç çökmedi. Gerçek bağlantılar yapılmadı.

## Katmanlar ve davranış

- LocalAccess önce kimlik/oturum/Origin/Host kurallarını uyguluyor; TenantBoundary kaynak/hedef tenant alanlarını recursive denetliyor; BusinessWorkspaces cookie işletmesine göre backend servislerini ve veri kökünü seçiyor.
- Rollerde genel middleware ve modül endpoint filter’ları birlikte var. Admin mutation; read-only/warehouse/operations için deny/allow matrisleri mevcut. Sadece frontend filtrelemesi değil.
- Body boyutu8MiB, header sayısı64/toplam32KiB, bağlantı256; tenant JSON depth32. Beklenen domain hataları400/403/404/409/501/503; global beklenmeyen hatalar maskeli500.
- Ürün POST/DELETE/PATCH405, mevcut ürün PUT/barcode409; eski endpointlerin kalması koruma amaçlı. ERP preview/apply admin; eski ürün oluşturma butonu kaldırılmış.
- Filtre/sayfa query modelleri page/size/sort/tarih/aralık doğruluyor. Örneğin ListingQuery1–100 size; iç servisler bazı tam snapshot ihtiyaçlarında int.MaxValue kullanıyor, bu kamu API’si limit desteği olarak genellenmemeli.
- StockOutbox, EnterpriseSync ve inbox farklı kalıcı iş akışları. HTTP202 hedefte işlendi demek değil; durum/makbuz sorgusu ayrılıyor. Yeniden başlatma/timeout/hata testleri mock ile geçti.
- response error biçimleri tek standarda sahip değil (string/nested object/boş403). accounts regresyonundaki parser kırılması bu tutarsızlığın test bakımına etkisini gösteriyor; API contract sürümleme önerilir.

## Yeni negatif kontroller

| Kontrol | Yöntem | HTTP | Sonuç | Cevap özeti |
| --- | --- | --- | --- | --- |
| negative products | POST | 405 | PASS | {"error":"Yeni ürün oluşturma kapalı. Ürünler Nebim V3 kaynağından alınır."} |
| negative catalog/products | POST | 405 | PASS | {"error":"Yeni ürün oluşturma kapalı. Ürünler Nebim V3 kaynağından alınır."} |
| negative security/connections/credentials | PUT | 500 | FAIL | {"error":"İşlem tamamlanamadı. Sistem olaylarını kontrol et."} |
| negative security/connections/erp | PUT | 400 | PASS | {"error":"Bağlantı/kimlik referansı geçersiz."} |
| negative security/connections/wms | PUT | 400 | PASS | {"error":"Bağlantı/kimlik referansı geçersiz."} |
| negative sync/enterprise/run | POST | 400 | PASS | {"error":"Eşitleme görev adı boş olamaz."} |
| negative sync/enterprise/integration/events | POST | 400 | PASS | {"error":"Merkezi olay kimliği, işletmesi, kaynağı veya hedefi geçersiz."} |
| negative sync/enterprise/erp-transfer/preview | POST | 400 | PASS | {"error":"ERP kaynak kimliği veya ayrı işlem türü geçersiz."} |
| negative sync/enterprise/channel-contracts/trendyol/products/preview | POST | 400 | PASS | {"error":"Ürün/hesap verisi eksik."} |
| negative sync/enterprise/channel-contracts/trendyol/orders/preview | POST | 400 | PASS | {"error":"Sipariş, hesap, kalem veya kargo yapısı geçersiz."} |
| negative erp/nebim/orders | POST | 501 | PASS | {"error":{"code":"NotSupported","message":"İstenen ERP işlemi bu sağlayıcı tarafından desteklenmiyor.","retryable":false}} |
| negative erp/nebim/events | POST | 501 | PASS | {"error":{"code":"NotSupported","message":"İstenen ERP işlemi bu sağlayıcı tarafından desteklenmiyor.","retryable":false}} |
| negative wms/fulfillment/orders | POST | 400 | PASS | {"error":"Pazaryeri sipariş kimliği, hesabı, para birimi, zamanı veya satırları geçersiz."} |
| negative wms/logistics/mock/events | POST | 400 | PASS | {"error":"WMS olay kimliği, miktarı veya durumu geçersiz."} |
| negative wms/inventory/policy | PUT | 400 | PASS | {"error":"Güvenlik stoğu yalnız bilinen depolar için 0-1.000.000 aralığında tanımlanabilir."} |
| negative pricing/preview | POST | 400 | PASS | {"error":"?r?n, ma?aza ve ilan kimli?i gerekli.","code":"pricing_blocked"} |
| negative settings | PUT | 400 | PASS |  |
| negative settings | PUT | 400 | PASS |  |
| foreign-origin | — | 403 | PASS |  |
| oversized-over-8MiB | — | 413 | PASS |  |
| health-after-invalid | — | 200 | PASS |  |

Nebim orders/events için501 hatalı başarı değildir: mock yazma capability varsayılan kapalı. İstek doğrulaması yazma izninden önce/sonra değişebilir; bu iki test yalnız desteklenmeyen işlemin sahte200 dönmediğini doğrular.

Tüm endpoint × rol × alan kombinasyonları test edilmedi. Tamper, tenant, yetkisiz, malformed, dev boyut, kaynak sahipliği ve servis kesintisi örnekleri mevcut paketlerde test edildi; aşağıdaki her route için bu kapsam otomatik ispatlanmış sayılmaz. Özellikle bütün metadata lookup’ları, dinamik Trendyol işlem kataloğunun her operasyonu, tüm finans mutasyonları ve tüm nested null kombinasyonları ayrı backlog’dur.

## Endpoint bildirim envanteri

Grup/prefix birleşimleri inventory.json groups içinde. Aşağıdaki path’ler kaynakta yazıldığı haliyle; gruba göreli path’ler tam URL değildir. Aynı deklarasyon farklı kanal/hesap/store parametreleriyle genişler. UI çağrısı görülmeyen endpointler otomatik “kullanılmıyor” ilan edilmedi: webhook, provider ve worker/dış tüketiciler mevcut.

| Yöntem | Bildirilen path | Dosya / satır |
| --- | --- | --- |
| GET | /api/shipments/search | [ListingFilters.cs:95](../../src/MarketPilot.Web/ListingFilters.cs#L95) |
| GET | /api/products/search | [ListingFilters.cs:96](../../src/MarketPilot.Web/ListingFilters.cs#L96) |
| GET | /api/orders/search | [ListingFilters.cs:97](../../src/MarketPilot.Web/ListingFilters.cs#L97) |
| GET | /api/health | [Program.cs:115](../../src/MarketPilot.Web/Program.cs#L115) |
| GET | /api/sync/status | [Program.cs:121](../../src/MarketPilot.Web/Program.cs#L121) |
| GET | /api/overview | [Program.cs:125](../../src/MarketPilot.Web/Program.cs#L125) |
| GET | /api/products | [Program.cs:126](../../src/MarketPilot.Web/Program.cs#L126) |
| DELETE | /api/products/{id} | [Program.cs:127](../../src/MarketPilot.Web/Program.cs#L127) |
| PATCH | /api/products/{id} | [Program.cs:128](../../src/MarketPilot.Web/Program.cs#L128) |
| POST | /api/products | [Program.cs:129](../../src/MarketPilot.Web/Program.cs#L129) |
| PUT | /api/products/{id} | [Program.cs:130](../../src/MarketPilot.Web/Program.cs#L130) |
| GET | /api/orders | [Program.cs:137](../../src/MarketPilot.Web/Program.cs#L137) |
| PUT | /api/orders/{id}/status | [Program.cs:138](../../src/MarketPilot.Web/Program.cs#L138) |
| GET | /api/connections | [Program.cs:149](../../src/MarketPilot.Web/Program.cs#L149) |
| PUT | /api/connections/{id} | [Program.cs:150](../../src/MarketPilot.Web/Program.cs#L150) |
| POST | /api/sync/demo | [Program.cs:153](../../src/MarketPilot.Web/Program.cs#L153) |
| GET | /api/activity | [Program.cs:160](../../src/MarketPilot.Web/Program.cs#L160) |
| GET | /api/settings | [Program.cs:161](../../src/MarketPilot.Web/Program.cs#L161) |
| PUT | /api/settings | [Program.cs:162](../../src/MarketPilot.Web/Program.cs#L162) |
| GET | /api/export/products.csv | [Program.cs:163](../../src/MarketPilot.Web/Program.cs#L163) |
| GET | /api/stock/movements | [Program.cs:167](../../src/MarketPilot.Web/Program.cs#L167) |
| POST | /api/stock/movements | [Program.cs:168](../../src/MarketPilot.Web/Program.cs#L168) |
| GET | /api/shipments | [Program.cs:169](../../src/MarketPilot.Web/Program.cs#L169) |
| POST | /api/shipments | [Program.cs:170](../../src/MarketPilot.Web/Program.cs#L170) |
| PUT | /api/shipments/{id}/status | [Program.cs:180](../../src/MarketPilot.Web/Program.cs#L180) |
| GET | /api/barcodes | [Program.cs:185](../../src/MarketPilot.Web/Program.cs#L185) |
| PUT | /api/products/{id}/barcode | [Program.cs:186](../../src/MarketPilot.Web/Program.cs#L186) |
| GET | /api/analytics/reports | [AnalyticsEndpoints.cs:24](../../src/MarketPilot.Web/Analytics/AnalyticsEndpoints.cs#L24) |
| GET | /api/analytics/profit | [AnalyticsEndpoints.cs:52](../../src/MarketPilot.Web/Analytics/AnalyticsEndpoints.cs#L52) |
| PUT | /api/analytics/costs/{id} | [AnalyticsEndpoints.cs:66](../../src/MarketPilot.Web/Analytics/AnalyticsEndpoints.cs#L66) |
| GET | /api/analytics/workspace | [AnalyticsEndpoints.cs:84](../../src/MarketPilot.Web/Analytics/AnalyticsEndpoints.cs#L84) |
| GET | /erp/status | [CatalogEndpoints.cs:35](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L35) |
| POST | /erp/preview | [CatalogEndpoints.cs:36](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L36) |
| POST | /erp/apply | [CatalogEndpoints.cs:37](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L37) |
| DELETE | /products/{id} | [CatalogEndpoints.cs:38](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L38) |
| PATCH | /products/{id} | [CatalogEndpoints.cs:39](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L39) |
| GET | /lookup | [CatalogEndpoints.cs:40](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L40) |
| GET | /products | [CatalogEndpoints.cs:41](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L41) |
| GET | /products/{id} | [CatalogEndpoints.cs:42](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L42) |
| POST | /products | [CatalogEndpoints.cs:43](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L43) |
| PUT | /products/{id} | [CatalogEndpoints.cs:44](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L44) |
| GET | /listings | [CatalogEndpoints.cs:45](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L45) |
| GET | /stores/{id}/candidates | [CatalogEndpoints.cs:46](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L46) |
| GET | /candidates/{token} | [CatalogEndpoints.cs:47](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L47) |
| POST | /import | [CatalogEndpoints.cs:48](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L48) |
| PUT | /mapping | [CatalogEndpoints.cs:55](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L55) |
| GET | /categories | [CatalogEndpoints.cs:56](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L56) |
| PUT | /categories | [CatalogEndpoints.cs:57](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L57) |
| POST | /preview | [CatalogEndpoints.cs:58](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L58) |
| POST | /publish | [CatalogEndpoints.cs:59](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L59) |
| GET | /jobs | [CatalogEndpoints.cs:60](../../src/MarketPilot.Web/Catalog/CatalogEndpoints.cs#L60) |
| GET | /schema | [CanonicalEndpoints.cs:18](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L18) |
| GET | /manual-mappings | [CanonicalEndpoints.cs:19](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L19) |
| POST | /manual-mappings/{id}/resolve | [CanonicalEndpoints.cs:20](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L20) |
| GET | /available-to-sell | [CanonicalEndpoints.cs:21](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L21) |
| GET | /{kind} | [CanonicalEndpoints.cs:22](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L22) |
| POST | /{kind} | [CanonicalEndpoints.cs:23](../../src/MarketPilot.Web/Contracts/CanonicalEndpoints.cs#L23) |
| GET | (grup kökü) | [EnterpriseEndpoints.cs:33](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L33) |
| GET | /integration/events | [EnterpriseEndpoints.cs:34](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L34) |
| POST | /integration/events | [EnterpriseEndpoints.cs:35](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L35) |
| POST | /integration/events/{id}/retry | [EnterpriseEndpoints.cs:36](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L36) |
| GET | /erp-transfer/policy | [EnterpriseEndpoints.cs:37](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L37) |
| PUT | /erp-transfer/policy | [EnterpriseEndpoints.cs:38](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L38) |
| POST | /erp-transfer/preview | [EnterpriseEndpoints.cs:39](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L39) |
| GET | /channel-contracts | [EnterpriseEndpoints.cs:40](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L40) |
| POST | /channel-contracts/{channel}/products/preview | [EnterpriseEndpoints.cs:41](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L41) |
| POST | /channel-contracts/{channel}/orders/preview | [EnterpriseEndpoints.cs:43](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L43) |
| GET | /readiness | [EnterpriseEndpoints.cs:45](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L45) |
| PUT | /plan | [EnterpriseEndpoints.cs:47](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L47) |
| POST | /run | [EnterpriseEndpoints.cs:48](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L48) |
| POST | /jobs/{id}/retry | [EnterpriseEndpoints.cs:49](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L49) |
| POST | /hooks/erp/catalog | [EnterpriseEndpoints.cs:50](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L50) |
| POST | /hooks/businesses/{businessId}/erp/catalog | [EnterpriseEndpoints.cs:51](../../src/MarketPilot.Web/Enterprise/EnterpriseEndpoints.cs#L51) |
| GET | (grup kökü) | [NebimEndpoints.cs:24](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L24) |
| GET | /products | [NebimEndpoints.cs:25](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L25) |
| GET | /variants | [NebimEndpoints.cs:26](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L26) |
| GET | /identifiers | [NebimEndpoints.cs:27](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L27) |
| GET | /prices | [NebimEndpoints.cs:28](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L28) |
| GET | /warehouses | [NebimEndpoints.cs:29](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L29) |
| GET | /stock | [NebimEndpoints.cs:30](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L30) |
| POST | /orders | [NebimEndpoints.cs:31](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L31) |
| GET | /orders/{id} | [NebimEndpoints.cs:32](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L32) |
| GET | /documents | [NebimEndpoints.cs:33](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L33) |
| GET | /events | [NebimEndpoints.cs:34](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L34) |
| POST | /events | [NebimEndpoints.cs:35](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L35) |
| GET | /sync-status | [NebimEndpoints.cs:36](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L36) |
| POST | /sync | [NebimEndpoints.cs:37](../../src/MarketPilot.Web/Enterprise/Nebim/NebimEndpoints.cs#L37) |
| GET | /readiness | [IntegrationEndpoints.cs:79](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L79) |
| GET | /credential-profiles | [IntegrationEndpoints.cs:95](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L95) |
| GET | /connections | [IntegrationEndpoints.cs:96](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L96) |
| POST | /connections | [IntegrationEndpoints.cs:97](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L97) |
| POST | /connections/{id}/{operation} | [IntegrationEndpoints.cs:123](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L123) |
| POST | /connections/{id}/stock-price | [IntegrationEndpoints.cs:192](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L192) |
| POST | /preview/{marketplace}/stock-price | [IntegrationEndpoints.cs:210](../../src/MarketPilot.Web/Integrations/IntegrationEndpoints.cs#L210) |
| GET | /amazon/connections/{id}/search/{kind} | [AmazonEndpoints.cs:12](../../src/MarketPilot.Web/Integrations/Amazon/AmazonEndpoints.cs#L12) |
| GET | /boyner/connections/{id}/search/{kind} | [BoynerEndpoints.cs:8](../../src/MarketPilot.Web/Integrations/Boyner/BoynerEndpoints.cs#L8) |
| GET | (grup kökü) | [CentralStockService.cs:944](../../src/MarketPilot.Web/Integrations/CentralStock/CentralStockService.cs#L944) |
| POST | /run | [CentralStockService.cs:945](../../src/MarketPilot.Web/Integrations/CentralStock/CentralStockService.cs#L945) |
| POST | /settings | [CentralStockService.cs:946](../../src/MarketPilot.Web/Integrations/CentralStock/CentralStockService.cs#L946) |
| POST | /unmatched/ignore | [CentralStockService.cs:960](../../src/MarketPilot.Web/Integrations/CentralStock/CentralStockService.cs#L960) |
| POST | /demo-sale | [CentralStockService.cs:972](../../src/MarketPilot.Web/Integrations/CentralStock/CentralStockService.cs#L972) |
| GET | /demo/connections/{id}/search/{kind} | [DemoWorkspace.cs:282](../../src/MarketPilot.Web/Integrations/Demo/DemoWorkspace.cs#L282) |
| GET | /demo/connections/{id}/workspace | [DemoWorkspace.cs:289](../../src/MarketPilot.Web/Integrations/Demo/DemoWorkspace.cs#L289) |
| POST | /demo/connections/{id}/actions | [DemoWorkspace.cs:295](../../src/MarketPilot.Web/Integrations/Demo/DemoWorkspace.cs#L295) |
| GET | /hepsiburada/connections/{id}/search/{kind} | [HepsiburadaEndpoints.cs:13](../../src/MarketPilot.Web/Integrations/Hepsiburada/HepsiburadaEndpoints.cs#L13) |
| GET | /n11/connections/{id}/search/{kind} | [N11Endpoints.cs:9](../../src/MarketPilot.Web/Integrations/N11/N11Endpoints.cs#L9) |
| GET | /ticimax/connections/{id}/search/{kind} | [TicimaxEndpoints.cs:8](../../src/MarketPilot.Web/Integrations/Ticimax/TicimaxEndpoints.cs#L8) |
| GET | /operations | [TrendyolEndpoints.cs:22](../../src/MarketPilot.Web/Integrations/Trendyol/TrendyolEndpoints.cs#L22) |
| POST | /connections/{id}/operations/{key} | [TrendyolEndpoints.cs:32](../../src/MarketPilot.Web/Integrations/Trendyol/TrendyolEndpoints.cs#L32) |
| GET | /api/access/status | [LocalAccess.cs:249](../../src/MarketPilot.Web/Local/LocalAccess.cs#L249) |
| POST | /api/access/setup | [LocalAccess.cs:250](../../src/MarketPilot.Web/Local/LocalAccess.cs#L250) |
| POST | /api/access/login | [LocalAccess.cs:262](../../src/MarketPilot.Web/Local/LocalAccess.cs#L262) |
| POST | /api/access/password | [LocalAccess.cs:270](../../src/MarketPilot.Web/Local/LocalAccess.cs#L270) |
| POST | /api/access/logout | [LocalAccess.cs:275](../../src/MarketPilot.Web/Local/LocalAccess.cs#L275) |
| GET | /api/access/users | [LocalAccess.cs:276](../../src/MarketPilot.Web/Local/LocalAccess.cs#L276) |
| POST | /api/access/users | [LocalAccess.cs:277](../../src/MarketPilot.Web/Local/LocalAccess.cs#L277) |
| POST | /api/access/users/{id} | [LocalAccess.cs:284](../../src/MarketPilot.Web/Local/LocalAccess.cs#L284) |
| GET | /api/system/status | [LocalReliability.cs:224](../../src/MarketPilot.Web/Local/LocalReliability.cs#L224) |
| GET | /api/system/events | [LocalReliability.cs:225](../../src/MarketPilot.Web/Local/LocalReliability.cs#L225) |
| POST | /api/system/backups/restore-check | [LocalReliability.cs:226](../../src/MarketPilot.Web/Local/LocalReliability.cs#L226) |
| GET | /api/system/backups | [LocalReliability.cs:230](../../src/MarketPilot.Web/Local/LocalReliability.cs#L230) |
| POST | /api/system/backups | [LocalReliability.cs:231](../../src/MarketPilot.Web/Local/LocalReliability.cs#L231) |
| POST | /api/system/backups/verify | [LocalReliability.cs:232](../../src/MarketPilot.Web/Local/LocalReliability.cs#L232) |
| GET | /api/system/backups/{name} | [LocalReliability.cs:233](../../src/MarketPilot.Web/Local/LocalReliability.cs#L233) |
| GET | /api/tenancy | [TenantBoundary.cs:51](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L51) |
| GET | /api/access/memberships | [TenantBoundary.cs:52](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L52) |
| POST | /api/tenancy/tenants | [TenantBoundary.cs:53](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L53) |
| POST | /api/tenancy/memberships | [TenantBoundary.cs:58](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L58) |
| POST | /api/access/switch-tenant | [TenantBoundary.cs:64](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L64) |
| GET | (grup kökü) | [TenantBoundary.cs:78](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L78) |
| PUT | /credentials | [TenantBoundary.cs:79](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L79) |
| PUT | /erp | [TenantBoundary.cs:80](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L80) |
| PUT | /wms | [TenantBoundary.cs:81](../../src/MarketPilot.Web/Local/TenantBoundary.cs#L81) |
| GET | (grup kökü) | [PricingEndpoints.cs:15](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L15) |
| POST | /source/refresh | [PricingEndpoints.cs:16](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L16) |
| PUT | /policy | [PricingEndpoints.cs:17](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L17) |
| PUT | /rules | [PricingEndpoints.cs:18](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L18) |
| POST | /preview | [PricingEndpoints.cs:19](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L19) |
| POST | /proposals/{id}/approve | [PricingEndpoints.cs:20](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L20) |
| POST | /proposals/{id}/publish | [PricingEndpoints.cs:21](../../src/MarketPilot.Web/Pricing/PricingEndpoints.cs#L21) |
| GET | /api/sync/automation | [SynchronizationEndpoints.cs:15](../../src/MarketPilot.Web/Synchronization/SynchronizationEndpoints.cs#L15) |
| POST | /api/sync/automation | [SynchronizationEndpoints.cs:16](../../src/MarketPilot.Web/Synchronization/SynchronizationEndpoints.cs#L16) |
| POST | /api/sync/outbox/{id}/retry | [SynchronizationEndpoints.cs:27](../../src/MarketPilot.Web/Synchronization/SynchronizationEndpoints.cs#L27) |
| METHODS | /hooks/orders/{endpointId} | [OrderWebhookEndpoints.cs:19](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L19) |
| POST | /hooks/orders/{endpointId} | [OrderWebhookEndpoints.cs:24](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L24) |
| POST | /hooks/businesses/{businessId}/orders/{endpointId} | [OrderWebhookEndpoints.cs:25](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L25) |
| GET | /api/operations/revision | [OrderWebhookEndpoints.cs:26](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L26) |
| GET | /api/system/webhooks | [OrderWebhookEndpoints.cs:27](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L27) |
| POST | /api/system/webhooks/{id}/retry | [OrderWebhookEndpoints.cs:28](../../src/MarketPilot.Web/Webhooks/OrderWebhookEndpoints.cs#L28) |
| GET | (grup kökü) | [WmsEndpoints.cs:69](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L69) |
| GET | /logistics | [WmsEndpoints.cs:71](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L71) |
| GET | /logistics/policies | [WmsEndpoints.cs:72](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L72) |
| PUT | /logistics/mapping | [WmsEndpoints.cs:73](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L73) |
| PUT | /logistics/label-policy | [WmsEndpoints.cs:74](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L74) |
| POST | /logistics/mock/events | [WmsEndpoints.cs:75](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L75) |
| POST | /logistics/mock/scenarios | [WmsEndpoints.cs:76](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L76) |
| GET | /inventory | [WmsEndpoints.cs:77](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L77) |
| GET | /inventory/movements | [WmsEndpoints.cs:79](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L79) |
| GET | /inventory/history | [WmsEndpoints.cs:81](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L81) |
| PUT | /inventory/policy | [WmsEndpoints.cs:83](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L83) |
| GET | /warehouses | [WmsEndpoints.cs:86](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L86) |
| GET | /warehouses/{warehouseId}/stock | [WmsEndpoints.cs:88](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L88) |
| GET | /warehouses/{warehouseId}/stock-movements | [WmsEndpoints.cs:90](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L90) |
| POST | /fulfillment-orders | [WmsEndpoints.cs:93](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L93) |
| GET | /fulfillment-orders/{id}/acceptance | [WmsEndpoints.cs:96](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L96) |
| GET | /fulfillment-orders/{id}/reservation | [WmsEndpoints.cs:99](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L99) |
| GET | /fulfillment-orders/{id}/picking | [WmsEndpoints.cs:102](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L102) |
| GET | /fulfillment-orders/{id}/packing | [WmsEndpoints.cs:105](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L105) |
| GET | /fulfillment-orders/{id}/shipment | [WmsEndpoints.cs:108](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L108) |
| GET | /fulfillment-orders/{id}/eligibility | [WmsEndpoints.cs:111](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L111) |
| GET | /fulfillment/policy | [WmsEndpoints.cs:119](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L119) |
| PUT | /fulfillment/policy | [WmsEndpoints.cs:121](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L121) |
| GET | /fulfillment/workflows | [WmsEndpoints.cs:125](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L125) |
| GET | /fulfillment/workflows/{id} | [WmsEndpoints.cs:127](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L127) |
| POST | /fulfillment/orders | [WmsEndpoints.cs:131](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L131) |
| POST | /fulfillment/workflows/{id}/advance | [WmsEndpoints.cs:136](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L136) |
| POST | /fulfillment/workflows/{id}/retry-validation | [WmsEndpoints.cs:141](../../src/MarketPilot.Web/Wms/WmsEndpoints.cs#L141) |
| GET | (grup kökü) | [OperationalEndpoints.cs:15](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L15) |
| PUT | /policy | [OperationalEndpoints.cs:16](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L16) |
| POST | /scan | [OperationalEndpoints.cs:17](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L17) |
| POST | /settlements | [OperationalEndpoints.cs:18](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L18) |
| POST | /holds | [OperationalEndpoints.cs:19](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L19) |
| POST | /holds/{id}/release | [OperationalEndpoints.cs:20](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L20) |
| POST | /requests | [OperationalEndpoints.cs:21](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L21) |
| POST | /requests/{id}/approve | [OperationalEndpoints.cs:22](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L22) |
| POST | /requests/{id}/cancel | [OperationalEndpoints.cs:23](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L23) |
| POST | /requests/{id}/receive | [OperationalEndpoints.cs:24](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L24) |
| POST | /deliveries/{id}/retry | [OperationalEndpoints.cs:25](../../src/MarketPilot.Web/Workspace/OperationalEndpoints.cs#L25) |
| GET | /views/{module} | [WorkspaceEndpoints.cs:16](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L16) |
| GET | /orders/{id} | [WorkspaceEndpoints.cs:17](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L17) |
| POST | /orders | [WorkspaceEndpoints.cs:18](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L18) |
| POST | /records | [WorkspaceEndpoints.cs:19](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L19) |
| POST | /views/{module}/{id}/actions | [WorkspaceEndpoints.cs:20](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L20) |
| GET | /options/{kind} | [WorkspaceEndpoints.cs:21](../../src/MarketPilot.Web/Workspace/WorkspaceEndpoints.cs#L21) |
