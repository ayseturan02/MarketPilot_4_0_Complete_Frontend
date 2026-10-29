# Nebim V3 sağlayıcı katmanı

Bu kod **Nebim V3'ün doğrulanmış API veya SQL sözleşmesi değildir**. Taşıma yöntemi bilinmediği için Nebim endpoint'i, servis adı, SQL sorgusu, kimlik doğrulama alanı ya da yerel request/response şeması tanımlanmadı. Katmanın bütün işlemleri MarketPilot'un ortak modelleri ve yerel mock verileridir.

Kod: `src/MarketPilot.Web/Enterprise/Nebim`. `INebimErpProvider` entegrasyon sınırıdır. `ErpConnectionProfile` yalnızca profil/işletme kimliği, çalışma modu, açık/kapalı durumu ve mock yazma izni taşır. Bağlantı adresi veya şifre içermez. `ErpCapabilities.Read` ve `.Write` ayrı bayraklardır. `ErpSyncResult<T>` sonucu, correlation kimliğini, tamamlanma zamanını ve `IsMock` bilgisini döndürür. `ErpError` kod, güvenli mesaj ve tekrar denenebilirlik bilgisini taşır; domain reddi `ErpProviderException` üzerinden bildirilir.

## Desteklenen mock işlemleri

| Arayüz işlemi | Mock kapsamı |
| --- | --- |
| `ReadProductsAsync`, `ReadVariantsAsync` | Bir giyim modeli ve renk/beden varyantı |
| `ReadIdentifiersAsync` | Açık varyant kimliğiyle barkod ve SKU bilgisi |
| `ReadPriceListsAsync` | TRY perakende fiyat listesi |
| `ReadWarehousesAsync`, `ReadStockAsync` | Ana/outlet depoları, fiziksel stok, adet/kutu dönüşümü |
| `RegisterOrderAsync` | Fixture varyantına ait siparişi JSON'a bir kez kaydetme |
| `GetOrderStatusAsync` | Kaydedilmiş mock siparişin güncel durumu |
| `ReadDocumentReferencesAsync` | Kayıtlı siparişlere bağlı, `MOCK-` numaralı **taslak** ticari belge referansları |
| `ReadEventsAsync`, `HandleEventAsync` | `order.cancelled`, `order.returned`, `order.corrected`; tekrar ve kaynak sürümü kontrolü |
| `GetSyncStatusAsync` | Profil bazında son başarılı okuma/yazma ve ortak kataloğa tam aktarım zamanı |

Mock yazma varsayılan kapalıdır. Desteklenmeyen yetenek veya gerçek modda işlem `NotSupported` üretir (HTTP 501). Kapalı profil `Disabled`; izin uyuşmazlığı `WriteDisabled`; işletme uyuşmazlığı `TenantMismatch`; kayıt yoksa `NotFound`; farklı içerikle tekrar `IdempotencyConflict` veya `OrderConflict`; aynı sürümde farklı olay `VersionConflict` üretir. Desteklenen, henüz verisi olmayan okuma boş liste döndürebilir; bu durum desteklenmeyen işlem değildir.

Sipariş anahtarı işletme/profil/kayıt kimliğiyle ayrılır. İşlem makbuzları ayrıca kaynak ve satıcı hesabını içerir. Aynı içerikle tekrar ikinci sipariş/olay oluşturmaz. Eski olay sürümü `stale` döner; güncel durumu ezmez. JSON değişiklikleri mevcut `DemoStore.Modify` kilidi, atomik dosya yazımı ve hata durumunda geri alma ile korunur. Durum `demo-store.json/nebimMock` içinde saklanır; gerçek veritabanı yoktur.

Mock, kayıtlı örnek varyant ve TRY/adet siparişlerini kabul eder. Olay türleri ve durumları **uygulamanın test sözlüğüdür**, Nebim durum kodları değildir. İptal/iade/düzeltme mock sipariş durumunu ve olay geçmişini değiştirir; gerçek finansal belge, stok hareketi veya para iadesi oluşturmaz. Geri ödeme veya Nebim muhasebe davranışı taklit edilerek gerçekmiş gibi sunulmaz.

## MarketPilot ile bağlantı

Sağlayıcı kök ve işletmeye özel DI konteynerlerine `AddEnterprise()` üzerinden kaydedilir. İşletme kimliği imzalı oturumun seçtiği `Workspace:BusinessId` değerinden alınır; istemci işletme değiştiremez.

MarketPilot'un **uygulama içi** yolları `/api/erp/nebim` altındadır. Bunlar Nebim endpoint'leri değildir:

- `GET /`: profil, yetenekler ve `nativeAdapter=technical-contract-pending`.
- `GET /products`, `/variants`, `/identifiers`, `/prices`, `/warehouses`, `/stock`, `/documents`, `/events`, `/sync-status`.
- `POST /orders`: ortak `MarketplaceOrder`; `GET /orders/{id}`: mock durum.
- `POST /events`: ortak `IntegrationEvent`.
- `POST /sync`: ürün → varyant → barkod → depo → stok → fiyat sırasıyla ortak JSON sözleşme deposuna aktarım. Okumalar uygulanmadan önce tamamlanır; desteklenmeyen okuma yarım katalog aktarımı başlatmaz. Uygulama sırasında hata/manuel eşleştirme olursa tam başarı checkpoint'i yazılmaz. Kayıt bazındaki kalıcılık sayesinde tekrar güvenlidir; bütün aktarım tek büyük transaction değildir.

Yazma yolları, hesap kurulmuşsa yönetici gerektirir; okumalar işletme kapsamında ve oturuma tabidir. `POST /sync` yalnızca ortak sözleşme alanını günceller. Mevcut pazaryeri gönderim kuyruğuna veya canlı sisteme otomatik dağıtım yapmaz.

Yerel mock ayarları:

```json
{
  "Enterprise": {
    "Nebim": {
      "Mode": "mock",
      "Enabled": true,
      "AllowMockWrites": false
    }
  }
}
```

`Mode=real` gerçek adaptör açmaz; yetenekleri kapatır ve çağrıları açıkça reddeder. Mock yazma testleri izole geçici yapılandırmada `AllowMockWrites=true` kullanır. Ana uygulamanın yazma ayarı değiştirilmez.

Mevcut `IErpCatalogProvider` / `EnterpriseBridge` özel MarketPilot ara servis sözleşmesi ayrı korunur. Eski `NebimBridgeProvider` adı, yerel Nebim adaptörü sanılmaması için `MarketPilotErpBridgeProvider` yapıldı. Özel köprü yolları Nebim dokümantasyonu değildir. Yeni sağlayıcı eski köprüye veya herhangi bir HTTP/SQL istemcisine çağrı yapmaz.

## Açık TODO / gerçek adaptör backlog'u

1. Kullanılacak Nebim kurulumu, modüller ve desteklenen sürüm doğrulansın.
2. Yetkili teknik dokümanla taşıma yöntemi belirlensin: API, servis veya onaylı başka entegrasyon yöntemi. SQL varsayılmasın.
3. Kimlik doğrulama, yetkiler, şirket/mağaza/depo kapsamı ve gizli bilgi yönetimi doğrulansın.
4. Kaynak kimlikleri, varyant/SKU/barkod ilişkileri, birimler, fiyat listeleri, vergi ve stok sahipliği eşleştirilsin.
5. Sayfalama, değişiklik takibi, silinme işaretleri, zaman dilimleri, sürüm/cursor ve limitler doğrulansın.
6. Sipariş kayıt, mükerrer kontrol, belirsiz yazma sonucu sorgusu, iptal/iade/düzeltme ve ticari belge semantiği doğrulansın.
7. Gerçek yetenek matrisi ve hata/yeniden deneme kuralları belgelensin; mock fixture sözlüğü yerel Nebim alanları gibi kullanılmasın.
8. Bundan sonra ayrı gerçek `INebimErpProvider` uygulaması ve izinli test ortamında kabul testleri geliştirilsin. Şu aşamada gerçekleştirilmedi.

## Doğrulama

`dotnet run --project tests/MarketPilot.NebimChecks` bütün arayüz akışlarını, yetenek reddini, kapalı/gerçek modları, JSON kalıcılığını, profil/işletme ayrımını, tekrar/sürüm kontrolünü ve ortak sözleşmelere aktarımı doğrular.

`node tests/nebim-http-checks.cjs` uygulama yollarını, varsayılan salt okumayı, yalnız mock yazmayı, oturum/rol/işletme ayrımını, checkpoint'i, yeniden başlatmayı ve gerçek modun 501 vermesini doğrular. Her iki grup `npm.cmd run check` tarafından otomatik keşfedilir. Hiçbir test gerçek Nebim bağlantısı kullanmaz.
