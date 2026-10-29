# MarketPilot ortak veri sözleşmeleri — v1

Kod: `src/MarketPilot.Web/Contracts`. Bu katman ERP, WMS ve pazaryeri adaptörlerinin ortak dilidir. Yeni veritabanı yoktur: kayıtlar mevcut işletmenin `Data/demo-store.json` dosyasındaki `contracts` alanına yazılır. Kayıt, işlem makbuzu ve manuel eşleştirme kuyruğu aynı dosya kilidi/geri alma işlemiyle korunur. Diğer demo verileri silinmez veya kendiliğinden bu modellere dönüştürülmez.

Bu, adaptörlerin bağlanabileceği çalışan model/doğrulama/kayıt API'sidir. Mevcut sağlayıcı adaptörleri kendiliğinden bu sözleşmelere geçirilmiş değildir. Kaynak sistemin kimlikleri, birimleri ve sürüm sıralaması adaptörde açıkça dönüştürülmelidir. Yeni bir ürün oluşturma arayüzü eklenmedi.

## Modeller ve ilişkiler

| Model | Amaç / ilişki |
| --- | --- |
| `CanonicalProduct` | Giyim modeli: ürün adı, kategori, marka, durum. Ad kimlik değildir. |
| `ProductVariant` | Ürüne bağlı SKU, renk, beden, temel birim, tam sayı kuralı ve birim dönüşümleri. |
| `Barcode` | Bir varyantın belirli satış birimine ait barkod. Değer metindir; baştaki sıfırlar korunur. Bir varyantın birden fazla barkodu olabilir. |
| `ExternalProductMapping` | Kaynak sistem + satıcı hesabı + ilan kimliğini varyanta bağlar. Aynı varyantın birden çok ilanı/hesabı olabilir. |
| `Warehouse` | İşletme içi tekil depo kodu, ad ve aktiflik. |
| `StockPosition` | Varyant/depo için fiziksel stok ve güvenlik stoğu; birim içerir. Aynı çift için ikinci stok kaynağı açılamaz. |
| `StockReservation` | Sipariş, varyant ve depo için miktar, birim, durum ve isteğe bağlı son kullanım zamanı. |
| `AvailableToSell` | Depo bazında hesaplanan temel birimde satılabilir stok; ayrı yazılmaz. |
| `PriceList` | Geçerlilik tarihleri, varyant/birim/para birimi fiyatları ve KDV bilgisi. |
| `MarketplaceOrder` | Satıcı hesabına bağlı sipariş, durum, para birimi ve sipariş satırları. |
| `OrderLine` | Sipariş içindeki tekil satır ve yerel panel satır kimliği; varyant, SKU, barkod, miktar, birim ve fiyat. |
| `FulfillmentRequest` | Sipariş satırlarını belirli depoya gönderen hazırlama talebi. |
| `Shipment` | Hazırlama talebine ve aynı depoya bağlı paket/gönderi, kargo, takip ve etiket referansı. |
| `ReturnRequest` | Sipariş/depo, iade satırları, nedenler ve kabul bilgisi. |
| `InvoiceReference` | Siparişin belge numarası, türü, durumu, para birimi, tutarı ve isteğe bağlı HTTPS belge adresi. Resmî fatura düzenlemez. |
| `IntegrationEvent` | Sürümlü olay adı, varlık türü/kimliği ve JSON yükü. |
| `SyncCheckpoint` | Kaynak/hesap akışının opak cursor bilgisi, son kaynak sürümü ve başarı zamanı. |

`OrderLine` sipariş içine gömülüdür; `AvailableToSell` türetilir. Bu nedenle 17 model için 15 bağımsız kayıt kaynağı vardır.

## Ortak bağlam

Her bağımsız kayıtta `id` ve `context: RecordContext` bulunur:

- `tenantId`: imzalı kullanıcı oturumunun işletmesiyle aynı olmalı. İstek gövdesi işletmeyi değiştiremez; uyuşmazlık 403 döner.
- `sourceSystem`, `sourceRecordId`: kaydın kaynağı ve o kaynak içindeki kararlı kimliği. Eksik kaynak kayıt kimliği yazılmaz; manuel kuyruğa alınır.
- `externalAccountId`: pazaryeri sipariş ve ilan eşleştirmelerinde zorunludur; diğer kayıtlarda isteğe bağlıdır. Kimlik alanları ayrıştırıcı birleştirme yerine yapısal olarak hashlenir.
- `correlationId`: aynı iş akışını takip eder. `idempotencyKey`: aynı işlemin güvenli tekrarı içindir; farklı veriyle kullanımı 409 döner.
- `sourceVersion`: ilgili kaynak kaydı için pozitif ve artan `long`. Kaynak opak ETag kullanıyorsa adaptör bunu karşılaştırılabilir sürüme çevirmeli; ETag metinleri rastgele sıralanmamalıdır.
- `occurredAt`, `receivedAt`: zaman dilimi içeren zaman damgalarıdır. Alınma zamanı kaynak olay zamanından önce olamaz. İşlem sırası zaman damgasından çıkarılmaz; sürüm kullanılır.

Depoyla ilgili kayıtlarda ayrıca `warehouseId` vardır. Satıcı hesapları ve kaynak kayıt kimlikleri işletmeler arasında ortak kabul edilmez.

## Eşleştirme ve sürüm kuralları

Ürün adı, açıklaması veya benzer metin üzerinden eşleştirme yapılmaz. İstekler açık ürün/varyant referanslarını taşır. Bir kaynak kimliği farklı kayda bağlanırsa, aynı SKU farklı varyantta kullanılırsa veya bir barkod farklı varyant/birimle gelirse işlem `manual-mapping` döner. Aynı varyantın farklı barkodları açık referansla kabul edilir. Bir satıcının aynı ilanı başka varyanta bağlanamaz; farklı satıcılar aynı dış ilan kimliğini bağımsız kullanabilir.

Manuel kuyruk girdisi özgün yükü ve nedeni saklar. Yönetici `resolve` ile hedef varyantı seçebilir; bu karar ERP ana verisini otomatik üzerine yazmaz ve çakışan barkodu kendiliğinden taşımaz. Adaptör/operatör karara göre düzeltilmiş ve güvenilir kimlikli kaydı yeni işlem anahtarıyla göndermelidir.

İşlem makbuzu kapsamı `(tenant, source, external account, resource kind, idempotency key)` olur. Tekrarın karşılaştırılmasında alınma zamanı, correlation ve işlem anahtarı iş içeriği sayılmaz. JSON nesne anahtar sırası typed modele dönüştürülür.

- Daha yeni sürüm: uygulanır.
- Daha eski sürüm: `stale`; veri değişmez.
- Aynı sürüm, aynı içerik: `duplicate`; ikinci kayıt oluşmaz.
- Aynı sürüm, farklı içerik: 409.
- `expectedVersion` verilmiş ve güncel sürüm değişmiş: 409. Yeni kayıt için beklenen sürüm 0'dır.
- Tekrar gelen aynı işlem: önceki sonuç döner; ikinci kuyruk girdisi veya rezervasyon oluşmaz.

`SyncCheckpoint.lastSourceVersion` ve akış adı geriye/değişik akışa taşınamaz. Checkpoint yalnızca adaptör ilgili sayfayı başarıyla işledikten sonra yazılmalıdır; bu API uzak sayfa çekimini kendisi yapmaz.

## Birimler ve stok

Miktarlar ve fiyatlar `decimal` kullanır. Örnek: `baseUnit=EA`, `BOX → 12`; 2 kutu = 24 adet. `wholeUnitsOnly=true` ise temel birime dönüşmüş miktar tam sayı olmalıdır. Metre gibi bölünebilir birimlerde `false` kullanılır: `CM → 0.01`, 125.5 cm = 1.255 m. Tanımsız veya negatif dönüşüm reddedilir. Stok geçmişi bulunan varyantın birim politikası doğrudan değiştirilemez.

`ATS = max(0, fiziksel stok − etkin rezervasyon − güvenlik stoğu)`. Her depo ayrı hesaplanır. Henüz stok kaydı yoksa fiziksel/satılabilir stok `null` döner; bilinmeyen stok sıfır gibi gösterilmez ve rezerve edilemez. Pasif depo için ATS sıfırdır. Rezervasyon kapasitesi dosya kilidi içinde kontrol edilir. Süresi dolan veya `released/consumed/expired` rezervasyonlar satılabilir miktardan düşülmez. Fiziksel stok zaten rezervasyonları düşmüş stok olmamalıdır; adaptör bu ayrımı korumalıdır.

Bu modeller ek bir stok defteri çalıştırmaz; sözleşme rezervasyonları mevcut operasyon rezervasyonlarını otomatik çiftlemez. Eski stok/rezervasyon akışını bu sözleşmeye geçiren adaptör tek yetkili yolu seçmelidir. Fatura, sevkiyat ve iade modellerini kaydetmek gerçek para/stoğa kendiliğinden hareket yazmaz. Sipariş satırı kimliği ve tek talep içindeki miktar üst sınırı doğrulanır; tam sevkiyat/iade yaşam döngüsü iş kuralları ayrı operasyon katmanının sorumluluğudur.

## HTTP kullanımı

Yerel kök: `/api/contracts`. Oturum açıldığında okuma işletmeye, yazma yöneticilere aittir; mevcut oturum/Origin korumaları geçerlidir.

| İstek | İşlem |
| --- | --- |
| `GET /schema` | Şema sürümü ve kaynak adları |
| `POST /{kind}?expectedVersion=1` | Typed doğrulama, tekrar/sürüm kontrolü ve atomik kayıt |
| `GET /{kind}?page=0&size=25` | İşletmeye ait sayfalı kayıtlar; en fazla 100 satır |
| `GET /available-to-sell?variantId=v1&warehouseId=w1` | Depo bazında hesaplanan ATS |
| `GET /manual-mappings` | Son 100 manuel inceleme girdisi |
| `POST /manual-mappings/{id}/resolve` | `{ "variantId": "v1" }` ile açık yönetici kararı |

Örnek ürün yükü (`POST /products`):

```json
{
  "id": "product-1",
  "context": {
    "tenantId": "default",
    "sourceSystem": "nebim-v3",
    "sourceRecordId": "NEBIM-ITEM-001",
    "externalAccountId": null,
    "correlationId": "catalog-pull-001",
    "idempotencyKey": "item-001-version-1",
    "sourceVersion": 1,
    "occurredAt": "2026-10-29T09:00:00Z",
    "receivedAt": "2026-10-29T09:00:01Z"
  },
  "name": "Pamuklu Oversize Tişört",
  "category": "Giyim / Üst Giyim / Tişört",
  "brand": "İnternet Tekstil",
  "status": "active"
}
```

## Testler

`dotnet run --project tests/MarketPilot.CanonicalChecks` verilen 11 senaryoyu ve ek olarak eşzamanlı rezervasyon, aynı sürümde içerik çatışması, geçersiz kayıtta geri alma, checkpoint gerilemesi, bütün modellerin kayıt/okuma yolunu ve yeniden başlatmayı doğrular.

`node tests/canonical-http-checks.cjs` HTTP sözleşmesini, eksik kimlik kuyruğunu, hatalı girdileri, oturum/rol kontrollerini, iki işletmede aynı kayıt kimliğini ve kalıcılığı test eder. Her iki grup da `npm.cmd run check` içine otomatik dahil edilir. Testler geçici JSON klasörlerini kullanır; gerçek API/veritabanı yoktur.
