# ERP sipariş hazırlığı ve merkezi orkestrasyon

Bu katman yalnız mock hedeflerle çalışır. Nebim native endpoint'i, HTTP isteği, SQL'i veya doğrulanmış gibi sunulan Nebim yazma gövdesi üretilmez. `ErpTransferPlan` MarketPilot iç modelidir. Altı kanalın mevcut kimlik doğrulama ve güvenlik kodları korunur; yeni ortak mapper'lar mevcut adaptör çıktılarını kullanır.

Uygulama DI mock WMS sağlayıcısının kabul makbuzları da aynı EnterpriseSync deposunda saklanır. Böylece WMS isteği kabul ettikten sonra yanıt kaybolsa ve uygulama yeniden başlasa bile kabul aynı external request ID ile sorgulanabilir. Doğrudan oluşturulan bağımsız test sağlayıcıları isteğe bağlı olarak yalnız bellekte çalışabilir. Merkezi workflow işaretlendiğinde manuel fulfillment ilerletmesi ERP hazırlık doğrulamasını/kabul referansını atlayamaz.

## Tek kalıcı iş altyapısı

`IntegrationOrchestrator`, olayları mevcut `EnterpriseState.Jobs` listesine `integration-event` olarak ekler. Aynı `EnterpriseSyncWorker` / `Deliver` döngüsü işler; ikinci kuyruk, worker veya iş dosyası yoktur. ERP mock makbuzları ve onaylı aktarım politikaları aynı `Data/enterprise-sync.json` içinde saklanır. İş deposu tek süreçli yerel kullanım içindir; çoklu sunucu veritabanı kuyruğu değildir.

- `OrderSynchronization`, siparişleri kalıcı işe almadan başarılı checkpoint yazmaz.
- Webhook inbox, doğrulanmış siparişi operasyon görünümüne ve merkezi işe aktarmadan processed/ignored olmaz. Depolama hatasında inbox tekrar denenir. İki dosya arasında dağıtık transaction yoktur; değişmeyen olay kimliği yeniden oynatmayı güvenli kılar.
- Stok hedefi mevcut `StockOutbox`'a aynı request ID ile delege edilir. Merkezi iş yalnız gerçek outbox sonucu completed olduğunda tamamlanır. Yeni fiyat gönderim yolu açılmaz; mevcut ERP fiyat onayı gerekir.
- Eski `erp-orders` ve `wms-orders` görevleri de ham sipariş snapshot'larını orkestratöre yönlendirir. Eski bridge sipariş yazma yolu kullanılmaz. Önceden kalmış legacy erp-order/wms-order işleri yeni hazırlık değerlendirmesi için bloke edilir.

Her işte `IntegrationEvent` altında TenantId, AccountId, EventId, CorrelationId, EventType, SourceSystem, SourceVersion, TargetSystem, OccurredAt bulunur. Aynı işin State, ProcessedAt, Result, Error ve ExternalReference alanları işlem sonucunu içerir. CorrelationId alt olaylara taşınır. Hesap kaynağı değişmişse kaynak olayı çalıştırılmaz. Kaynak sürümü bilinmiyorsa (0) yazma bloke olur; tahmini sürüm üretilmez.

Aynı tenant/hesap/event ID farklı içerikle kabul edilmez. Eski sürüm kalıcı olarak bloke kaydedilir. Aynı sürümde farklı stok verisi uzlaştırma gerektirir. İşlem ortasında kapanan sending işi uncertain açılır. Belirsiz ERP sonucu önce kaynak işlem anahtarıyla sorgulanır; kayıt bulunamaması kör yeniden gönderim yetkisi değildir. WMS belirsizlikleri mevcut fulfillment external request ID sorgusuyla çözülür. Tamamlanmış merkezi işlerin idempotency kayıtları otomatik budanmaz; 10.000 iş sınırında kaynak checkpoint ilerletilmez.

## Olay yönlendirmesi

| Olay | Desteklenen güvenli davranış |
|---|---|
| MarketplaceOrderReceived → erp | ERP sipariş hazırlığı, mock kabul sorgusu ve mutabakat |
| MarketplaceOrderReceived → wms | İşletmenin onaylı FulfillmentWorkflowPolicy sırası; ERP adımı varsa ErpOrderTransferService kabulü mevcut workflow'a aktarılır, ikinci ERP kaydı yapılmaz |
| ErpOrderAccepted | Doğrulanmış mock kabul ve tutar mutabakatı sonrası audit olayı |
| WmsOrderAccepted → wms | Kabulden sonra rezervasyon sorgusu |
| StockReserved → wms | Toplama sorgusu |
| PickCompleted → wms | Paketleme sorgusu |
| PackageCompleted → wms | Sevk/takip ve mevcut mock güncelleme akışı |
| ShipmentDispatched | Kısmi sevk dahil gerçekleşen paketlerin audit olayı |
| StockChanged → marketplace | Yalnız demo hedefe mevcut StockOutbox üzerinden stok gönderimi |
| ProductChanged → catalog | ERP kodlarını mevcut ilan kimliğine eşleyen yayın hazırlığı; ürün oluşturmaz/yayınlamaz |
| PriceChanged | Mevcut ERP fiyat onayı gerektirir; otomatik yazma bloke |
| OrderCancelled / ReturnAccepted → erp | Ayrı cancel / return işlemi, doğrulanmış ERP kabul referansı gerekir |

On iki olay türünün tümü `audit` hedefinde yalnız gözlem olarak kaydedilebilir; bu hedef bir dış yazma başarısı anlamına gelmez. Diğer doğrulanmamış olay/hedef çiftleri başarı döndürmek yerine bloke edilir. WMS tarafından yönetilen/kargo kanalı stoklu Amazon FBA ve n11depom siparişleri merchant WMS akışına gönderilmez.

İşletme fulfillment politikası onaylıysa otomatik sipariş alımı o workflow'u kullanır; değilse ERP hazırlık değerlendirmesine alınır. ERP→WMS ve WMS→ERP sıraları desteklenir. WMS kabulü işi ile toplama/paketleme/sevk işleri ayrıdır. Planın standalone modu artık ERP görevinin sabit olarak önce etkin olmasını gerektirmez. Nebim-integrated topolojide doğrudan WMS görev kısıtı korunur.

## ErpOrderTransferService

Hesap bazlı onaylı politika satış kanalı kodunu, cari stratejisini (`fixed-account` veya `existing-customer`), mevcut müşteri eşlemelerini, izin verilen para birimlerini/KDV oranlarını ve SKU+barkod→ERP ürün/varyant kodlarını içerir. Yeni cari otomatik açılmaz. Belirsiz/çoklu varyant eşlemesi yazmayı bloke eder.

Bu sürümde yalnız açıkça onaylanmış **KDV dahil birim fiyat** esası desteklenir. Kalem fiyatı, miktarı, brüt tutarı, para birimi ve KDV'si bilinmelidir. Kalem brüt toplamı sipariş brüt tutarına, brüt eksi indirim pazaryeri net tutarına eşit olmalıdır. Eksik veri sıfıra çevrilmez; vergi/indirim tahmini yapılmaz. Kaynak sipariş snapshot'ı, indirim ve sağlanan Promotions bilgileri planda korunur. Kanal bu ayrıntıları sağlamıyorsa uydurulmaz.

`create`, `correct`, `cancel`, `return` ayrı idempotency anahtarlarına sahiptir. Create anahtarı tenant+hesap+kanal+kaynak sipariş/paket kimliğinden türetilir; farklı event ID aynı siparişi çoğaltmaz. Farklı içerikle aynı create anahtarı müdahale gerektirir. Kabul edilmiş siparişin düzeltme/iptal/iadesi aynı kaynak kimliğinin ERP kabul referansını ister. Kesin reddedilmiş kayıt için kullanıcı yeni bir correct işlemi açabilir: `RejectedRequestKey` gerçek rejected makbuzuna bağlanmalıdır. İptal ve iade bu istisnayı kullanamaz.

`IErpOrderTransferProvider.QueryBySourceIdentityAsync` belirsiz sonucu sorgulama sözleşmesidir. Yeni işte bile önce mevcut makbuz aranır. Accepted ancak kabul referansı, aynı para birimi ve sıfır tutar farkı varsa completed sayılır. Pending bekler, rejected kullanıcı müdahalesi ister, farklı ERP tutarı reconciliation-required olur. HTTP transport başarısı hiçbir yerde ERP kaydı olarak yorumlanmaz.

Mock senaryolar: success, rejected, pending (iki sorgudan sonra kabul), timeout (commit sonrası yanıt kaybı), unavailable (kesinleşmeyen sonuç), mismatch (ERP tutar farkı). Gerçek provider enjeksiyonu IsMock=false ise yazma bloke edilir.

## Altı kanalın ortak sözleşmeleri

`ChannelContracts` Trendyol, Hepsiburada, n11, Amazon, Boyner ve Ticimax için ayrı capability/durum profili ve mapper sunar. Matris mevcut ortak adaptör yüzeyini belgeler; native API'nin bütün yeteneklerini var saymaz. Ürün/ilan/content/main ID, SKU, barkod, ERP varyant bağlantısı, nullable fiyat/KDV, ham sipariş/paket durumları, takip, kampanya, indirim, fulfillment sahibi ve sayfalama bilgileri korunur. Bilinmeyen durum canonical başarıya çevrilmez.

Amazon'un tüm paketleri ortak sipariş snapshot'ında korunur. Ortak yüzeyde paket bilgisi sağlanmayan kanala paket ID/durumu uydurulmaz. Amazon FBA ve n11 N11/depo sahipliği ortak modelde işaretlenir. Async accepted ve processed ayrı makbuz alanlarıdır. Native hata kodu ve HTTP durumu korunur; gizli ham yanıtlar hata modeline alınmaz. Unsup­ported işlem 501 verir. Yeni ürün oluşturma her altı profilde de desteklenmez. Eşleme yalnız **var olan ilan** için yayın hazırlığıdır.

## Yerel API

Mevcut `/api/sync/enterprise` yönetici yazma yetkisi ve uygulama erişim sınırları uygulanır.

- `GET/POST /integration/events`: kalıcı olay işleri / olay kabulü (202 yalnız kuyruğa kabul).
- `POST /integration/events/{jobId}/retry`: doğrulama veya güvenli durum sorgusunu yeniden başlatır. Kesin red/mutabakat farkı ve eski/çelişkili sürüm kör retry alamaz.
- `GET /erp-transfer/policy?accountId=...`, `PUT /erp-transfer/policy`: işletme/hesap politikası.
- `POST /erp-transfer/preview`: ErpTransferRequest→mock plan; hedefe yazmaz.
- `GET /channel-contracts`: altı capability matrix.
- `POST /channel-contracts/{channel}/products/preview`: AccountId+Product→mevcut ilan eşleme hazırlığı.
- `POST /channel-contracts/{channel}/orders/preview`: AccountId+Order→ortak sipariş modeli.

Senkronizasyon Merkezi mevcut kalıcı iş tablosunda ERP reddini, mutabakat gereksinimini, hata nedenini ve doğrulanmış dış referansı gösterir. Kaynak siparişler ERP/WMS erişilemezken iş payload'ında korunur.

## Testler

- `node tests/integration-orchestrator-http-checks.cjs`: izole içerik kökü; ERP senaryoları, düzeltme/iptal/iade, kesin red sonrası müdahale, iki ERP/WMS sırası, WMS lifecycle, StockOutbox, poller alımı, metadata, idempotency, eski sürüm ve yeniden başlatma.
- `dotnet run --project tests/MarketPilot.ChannelContractChecks`: loopback sahte HTTP sunucusu; altı ortak mapper, kimlikler, KDV, kampanya/paketler, cursor/tarih/hesap, async makbuz ve unsupported kontrolleri.
- Mevcut IntegrationChecks (Trendyol), HepsiburadaChecks, N11Checks, AmazonChecks, BoynerChecks, TicimaxChecks: native fixture HTTP transportları; kimlik doğrulama, güvenlik, sayfalama, hata ve desteklenmeyen filtreler.
- Mevcut webhook, enterprise-sync, synchronization ve WMS testleri: geriye dönük sınırlar ve ortak kalıcı altyapı.

Testler canlı pazaryeri/Nebim/WMS API'sine bağlanmaz. Loopback HTTP sunucusu ve fixture transportları kullanılır; uygulamanın gerçek işletme verilerine test senaryosu yazılmaz.
