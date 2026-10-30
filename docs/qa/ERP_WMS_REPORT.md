# Nebim V3 ve WMS denetimi

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**Her iki gerçek sağlayıcı da hazır değil.** Doğrulanmış Nebim transport/servis/SQL/yazma sözleşmesi ve seçilmiş WMS sözleşmesi yok. Gerçek bağlantı denenmedi; native endpoint/alan uydurulmadı. Uygulamadaki /api/erp/nebim route’ları MarketPilot uygulama route’larıdır, Nebim API URL’leri değildir.

## Ortak veri modeli

Contracts/CanonicalModels.cs:5–30 içinde17 model var: CanonicalProduct, ProductVariant, Barcode, ExternalProductMapping, Warehouse, StockPosition, StockReservation, AvailableToSell, PriceList, MarketplaceOrder, OrderLine, FulfillmentRequest, Shipment, ReturnRequest, InvoiceReference, IntegrationEvent ve SyncCheckpoint. RecordContext tenant/source/account/correlation/idempotency/source version/kaynak-alım zamanlarını taşıyor. WarehouseId gerekli depo modellerinde; OrderLine üst sipariş bağlamı içinde gömülü, ATS türetiliyor.

CanonicalChecks PASS: aynı SKU/farklı barkod (açık aynı varyant), farklı varyantta aynı barkod ve belirsiz SKU manuel kuyruk, çok depo, çok ilan/hesap, BOX↔EA ve CM↔M, decimal/whole units, eksik kaynak kimliği, değişmiş/eşit/eski sürüm, replay/checkpoint ve concurrency/rollback/restart. Ada göre otomatik kimlik yok. Bu katmanın başarılı atomik rezervasyonu ayrı fulfillment iş akışının kapasite garantisi değildir.

## Nebim

| Alan | Durum / kanıt |
| --- | --- |
| Arayüz / profile / capabilities / result / error | INebimErpProvider, ErpConnectionProfile, ayrı read/write flags, ErpSyncResult ve ErpError mevcut. NebimEndpoints.cs:13 yalnız MockNebimProvider kaydı. |
| Ürün ve varyant / barkod / SKU / fiyat / depo-stok / belge | Mock okumalar var. Sürüm ve son sync checkpoint takip ediliyor. |
| Yazma / event / order status | AllowMockWrites ayrı; default kapalı501. Native mod NotSupported, sessiz200 değil. |
| ERP salt okunur katalog | CatalogHub/Store/Source ve ErpCatalogReadModel ile preview/apply; marka/kategori/açıklama/onaylı görsel; mapping korunması ve missing-source review testleri PASS. |
| Fiyat | Yetkili listeler, kanal eşlemesi, KDV tabanı, açık kurallar, komisyon/masraf, alt/üst limit, kampanya önceliği, approval/versiyon/expiry; unknown null, yanlış currency blocked. RealPriceWrites kapalı. |
| Sipariş aktarım hazırlığı | ErpOrderTransferService: satış kanalı/cari/varyant/KDV/tutar/kampanya, source identity/idempotency, kabul referansı/sorgu, ayrı create/correct/cancel/return. Mock accepted/rejected/pending/timeout/duplicate ve mutabakat senaryoları PASS. |
| Gerçek adaptör | Eksik / beklemede: Contracts.cs:17 açık TODO. Mevcut EnterpriseBridge özel MarketPilot bridge sözleşmesidir; native Nebim olarak isimlendirilmemeli. |

## WMS

Bağımsız Wms/IWmsProvider profile/capability/warehouse/stock/movement/reservation/fulfillment/shipment modelleri mevcut. Mock deterministik veri, gecikme/hata, kabul/rezervasyon/toplama/paket/sevk/uygunluk sorguları; native provider kapalı. Enterprise namespace altındaki eski IWmsProvider ayrı özel bridge okuma sözleşmesi olarak yaşamaya devam ediyor.

WmsInventoryService depo bakiyelerini ayrı tutuyor. Fiziksel/rezervasyon/hasarlı/bloke/iade kontrol/ayrılmış/transfer ve kaynak versiyon/zamanı; işletme onaylı deduction policy, warehouse-account atama, güvenlik stoğu/kanal limitleri; FBA/n11depom ve unknown/negatif koruması; eski/çelişkili kaynak incelemesi testleri PASS. Ancak kaynak yaşına göre stok yayınını otomatik kapatan açık işletme tazelik SLA’sı ve tüm kanallara uygulanan tek ortak rezervasyon otoritesi henüz üretim seviyesi değil.

FulfillmentWorkflowPolicy ERP/WMS sırasını belirliyor; belirsiz işlem blocked/uncertain, external request ID/idempotency, kabul≠işlendi, kısmi sevk/çok paket/çok depo, depo reddi ve timeout sorgulama var. **MP-QA-001: satılabilir30 için iki bağımsız sipariş60 tahsis alıyor ve iki WMS kabulü geliyor.** Bu yüzden stok/fulfillment tamamlandı denemez. Test gerçek iki siparişin paralel alınması ve provider kabulüyle tekrarlandı.

WmsLogistics olayları sevkiyat/paket/carrier/tracking/toplama/paketleme/sevk zamanı/kısmi sevk/başarısız teslimat/iade kabul/kalite/yeniden satış bilgisini temsil ediyor. Kanal durum eşlemesi ve WMS/carrier/marketplace etiket yetkisi ayrı. Etiket dosyası uydurulmuyor; WMS restock onayı olmadan satılabilir iade işaretlenmiyor, fiziksel stok doğrudan artırılmıyor. Mock UI/API durum geçişleri PASS; gerçek carrier ve kanal teyidi yok.

## Merkezi orkestrasyon

12 event adı tanımlı: ProductChanged, PriceChanged, StockChanged, MarketplaceOrderReceived, ErpOrderAccepted, WmsOrderAccepted, StockReserved, PickCompleted, PackageCompleted, ShipmentDispatched, OrderCancelled, ReturnAccepted. Tenant/hesap/EventId/CorrelationId/source version/source-target/zaman/sonuç/hata mevcut. Poller ve webhook başarı/checkpoint öncesi EnterpriseSync içine kaydediyor. Retry belirsizde sorgu yoluna gidiyor; duplicate/stale/eşit versiyon conflict ve manuel müdahale testleri var.

Event adının bulunması her hedef kombinasyonunun işlendiği anlamına gelmez. IntegrationOrchestrator.ProcessAsync desteklenen event×target dallarını yürütüyor, doğrulanmamış kombinasyonu bloke ediyor. ERP/WMS erişilemezliğinde mock sipariş/iş kaybı test edilmediği söylenemez: orchestrator HTTP restart/recovery geçti; ancak gerçek dağıtık DB/outbox atomikliği ve bütün kesinti noktaları doğrulanmadı.

## Açık işler ve çıkış kriteri

1. MP-QA-001 ve MP-QA-002 kapanmalı; siparişler/depolar arası ortak kapasite, restart ve iade/iptalde doğru serbest bırakma testi.
2. Yetkili Nebim ve WMS teknik sözleşmesi, anonymized gerçek fixture ve her yetenek için doğrulanmış mapper.
3. Provider integration’ın credential-reference yaşam döngüsü, accepted/processed/receipt, rate limit/date/cursor/version semantiği onaylanmalı.
4. Tek stok otoritesi, freshness/uzlaştırma, kanal yayınından önce policy/onay ve bağımlı süreçlerin veri akışı birleştirilmeli.
5. Native belge referansı/iptal/iade/düzeltme ve mutabakat muhasebe uzmanıyla doğrulanmalı; üretim yazmaları bu fazda kapalı kalmalı.

Kanıt: NebimChecks, ErpReadModelChecks, PricingChecks, WmsChecks, WmsLogisticsChecks, ErpTransferChecks, OrchestratorChecks logları ve ilgili HTTP logları; [evidence/invariants-probe.json](evidence/invariants-probe.json) / [evidence/backup-probe.json](evidence/backup-probe.json).
