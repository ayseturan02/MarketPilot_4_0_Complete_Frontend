# MarketPilot 5.1 — Gerçek Trendyol entegrasyonu

.NET 8 uygulaması. Mevcut 39 ekran, beyaz tema ve yerel demo işlemleri korunur. Satış Kanalları ile Trendyol Merkezi gerçek Trendyol verilerini salt okunur görüntüleyebilir.

## Başlatma

```powershell
dotnet run --project src/MarketPilot.Web --urls "http://localhost:5080"
```

Tarayıcı: http://localhost:5080/#connections

`Mağaza ekle` ile Trendyol, `Gerçek API · Salt okunur` ve doğru API ortamını seçin. Sonra sırasıyla bağlantıyı test edin, mağaza bilgilerini açın, ürünleri veya siparişleri filtreleyerek okuyun. Bağlantı durumu, Seller ID, son işlem, kayıt sayısı ve temizlenmiş API hatası mağaza kartında gösterilir.

## API bilgilerini güvenli tanımlama

API anahtarlarını kaynak koduna, forma, sohbete veya `localStorage` alanına yazmayın. Seller ID, API Key ve API Secret bilgileri Trendyol Satıcı Paneli → Hesap Bilgilerim → Entegrasyon Bilgileri bölümünden alınır. Geliştirme ortamında .NET User Secrets kullanın:

```powershell
dotnet user-secrets set "Marketplaces:Trendyol:production:SellerId" "SELLER_ID" --project src/MarketPilot.Web
dotnet user-secrets set "Marketplaces:Trendyol:production:ApiKey" "API_KEY" --project src/MarketPilot.Web
dotnet user-secrets set "Marketplaces:Trendyol:production:ApiSecret" "API_SECRET" --project src/MarketPilot.Web
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project src/MarketPilot.Web --urls "http://localhost:5080"
```

Stage hesabı kullanılıyorsa `production` yerine `stage` yazın. User Secrets geliştirme içindir ve şifreli bir kasa değildir; sunucu dağıtımında işletim sisteminin veya barındırma sağlayıcısının sır yönetimini kullanın. Ortam değişkeni karşılıkları `Marketplaces__Trendyol__production__SellerId`, `Marketplaces__Trendyol__production__ApiKey` ve `Marketplaces__Trendyol__production__ApiSecret` şeklindedir.

Bu sürümde her Trendyol ortamı için tek sunucu kimlik profili vardır. Aynı ortama eklenen farklı mağaza adları aynı Seller ID ile bağlanır. Çoklu satıcı sır referansları kalıcı veritabanı aşamasında eklenecek.

## MarketPilot 5.1 kapsamı

- Bağlantı testi Product V2 üzerinde tek kayıtlık GET isteğiyle Seller ID, Basic Auth, User-Agent ve ortam erişimini doğrular.
- Mağaza bilgileri Seller ID, ortam, mağaza kodu ve kayıtlı sevkiyat/fatura/iade adreslerini gösterir. Trendyol’un bu servis için düşük hız sınırı nedeniyle başarılı sonuç bir saat önbellekte tutulur.
- Ürünler Product V2 onaylı ve onaysız ürün servislerinden okunur. Barkod, SKU, content/varyant/ürün ana ID, kategori, marka, liste/satış/müşteri fiyatı, stok, satış/onay durumu, red nedenleri ve güvenli HTTPS görselleri gösterilir.
- Siparişler Order V2 paket servisinden okunur. Müşteri, sipariş kalemleri, net/brüt tutar, indirim, durum, teslimat adresi, kargo firması/takip/paket ve ilişkili fatura bilgileri gösterilir.
- Ürünlerde barkod, SKU, onay grubu, durum ve sayfa boyutu; siparişlerde durum, sipariş no, barkod, tarih aralığı, sıralama ve sayfa boyutu filtrelenir. Ürünlerde `nextPageToken`, yüksek hacimli siparişlerde `/orders/stream` ve `nextCursor` ile 10.000 kayıt ötesine salt okunur devam edilebilir. Akış tarih aralığı en fazla 14 gündür.
- 400, 401, 403, 404, 408, 426, 429 ve 5xx cevapları anlaşılır hata mesajlarına çevrilir. Ham dış servis gövdesi kullanıcıya veya loga taşınmaz.
- Demo mod aynı ayrıntılı ekranları yerel örnek veriyle çalıştırır ve dış API çağrısı yapmaz.
- Hesapsız demo verileri Trendyol’un resmî Product V2, Order V2, adres ve cursor cevap alanlarına göre hazırlanmıştır. Trendyol’un canlı ve stage servisleri kimlik doğrulaması istediğinden bu mod resmî sunucuya ağ çağrısı yapmaz.
- Panel, stage ve production kimlik bilgilerinin hazır olup olmadığını yalnızca doğru/yanlış olarak gösterir; anahtar değerlerini istemciye göndermez.

## Salt okunur güvenlik sınırı

Satış Kanalları ekranındaki Trendyol adaptörü yalnızca şu GET uçlarına çağrı yapar (yazma servisleri için aşağıdaki Trendyol API Merkezi bölümüne bakın):

- `/integration/product/sellers/{sellerId}/products/approved`
- `/integration/product/sellers/{sellerId}/products/unapproved`
- `/integration/order/sellers/{sellerId}/v2/orders`
- `/integration/order/sellers/{sellerId}/orders/stream`
- `/integration/sellers/{sellerId}/addresses`

Bu adaptörde fiyat, stok veya sipariş durumu değiştiren dış API metodu bulunmaz; bu tür servisler yalnızca Trendyol API Merkezi’nde, `AllowWrites` açıkken ve kullanıcı onayıyla gönderilir. `POST /api/integrations/preview/{marketplace}/stock-price` yalnızca yerel bir taslağı doğrular ve her zaman `sent:false` döndürür. Sonuçlar kalıcı veritabanına yazılmaz.

Uygulamada kullanıcı hesabı doğrulaması henüz olmadığı için entegrasyon uçları sadece yerel makineden ve aynı web kaynağından erişime açıktır. İnternete açık dağıtımdan önce kullanıcı yetkilendirmesi eklenmelidir.

## Trendyol API Merkezi (5.2)

Trendyol ekranında (`#trendyol`) tüm Trendyol entegrasyon servisleri 13 panelde listelenir. Her panelin kodu kendi klasöründedir; başka bir pazaryeri bağlanırken Trendyol koduna dokunulmaz.

```
src/MarketPilot.Web/Integrations/
  Contracts.cs, IntegrationEndpoints.cs   ortak sözleşmeler ve mağaza kayıtları
  Demo/  Hepsiburada/  N11/  Amazon/       her pazaryeri kendi klasöründe
  Trendyol/
    Core/            kimlik, ortam, hata dönüşümü, operasyon çalıştırıcı, katalog
    TrendyolAdapter.cs, TrendyolEndpoints.cs
    Products/ Catalog/ Inventory/ Orders/ Claims/ Invoices/ Labels/
    Questions/ Finance/ Webhooks/ Addresses/ Videos/ TestEnvironment/
src/MarketPilot.Web/wwwroot/assets/trendyol/   Trendyol API Merkezi arayüzü
```

Yeni bir Trendyol servisi eklemek için ilgili panel dosyasına (`Trendyol/<Panel>/Trendyol<Panel>Panel.cs`) bir `TrendyolOperation` satırı ekleyin; yeni panel için klasörü açıp `Trendyol/Core/TrendyolCatalog.cs` listesine ekleyin.

- `GET /api/integrations/trendyol/operations` panel ve servis kataloğunu döndürür.
- `POST /api/integrations/trendyol/connections/{id}/operations/{key}` gövdesi `{ path, query, body, form, files, confirm }` biçimindedir.
- Demo mağazada hiçbir istek Trendyol’a gitmez; hazırlanan URL ve gövde önizlenir.
- Okuma servisleri gerçek mağazada doğrudan çalışır. Yazma servisleri (stok/fiyat, paket statüsü, fatura, iade onayı, webhook vb.) varsayılan olarak **kapalıdır** ve yalnızca önizleme döner. Açmak için ilgili ortamda sunucu ayarı tanımlayın; arayüz her yazma isteğinden önce ayrıca onay ister:

```powershell
dotnet user-secrets set "Marketplaces:Trendyol:stage:AllowWrites" "true" --project src/MarketPilot.Web
```

- Yalnızca stage servisleri (test siparişi, test statüsü, test sorusu) production mağazasında reddedilir.
- Yol parametreleri doğrulanır ve kodlanır; Trendyol 4xx hatalarında yalnızca `message` alanları gösterilir, ham yanıt gövdesi istemciye taşınmaz.

## Merkezi stok

Satış Kanalları ekranındaki **Merkezi Stok** paneli fazla satışı (overselling) önler. Stok bilgisinin sahibi harici stok uygulamanızdır (ERP, depo programı vb.). Her çalıştırmada:

1. Stok uygulamasından güncel stok okunur.
2. Bağlı tüm mağazalardaki yeni satışlar bulunur ve merkezi stoktan düşülür. Bir mağaza ilk kez izlendiğinde mevcut siparişleri geçmiş kabul edilir ve düşülmez.
3. Satış stok uygulamasına bildirilir. Aynı satış tekrar gönderilirse aynı `Idempotency-Key` ile gider.
4. Kalan miktar, eksi **güvenlik stoğu**, diğer tüm kanallara gönderilir.

Stok uygulaması tanımlı değilse yerleşik **demo stok uygulaması** kullanılır ve dış çağrı yapılmaz. Panelden demo mağazada satış oluşturup diğer kanallarda stoğun düştüğünü izleyebilirsiniz. Otomatik senkronizasyon ve aralık (30–3600 sn) panelden açılır.

Gerçek stok uygulamasını bağlamak için:

```powershell
dotnet user-secrets set "Marketplaces:StockSource:BaseUrl" "https://erp.ornek.com/api" --project src/MarketPilot.Web
dotnet user-secrets set "Marketplaces:StockSource:ApiKey" "API_KEY" --project src/MarketPilot.Web
# İsteğe bağlı: StockPath (varsayılan /stock), SalesPath (/stock/sales), ApiKeyHeader (X-Api-Key; "Authorization" verilirse Bearer gönderilir)
# Satışları stok uygulamasına bildirmek için:
dotnet user-secrets set "Marketplaces:StockSource:AllowWrites" "true" --project src/MarketPilot.Web
```

Yerel ağdaki bir uygulama HTTPS desteklemiyorsa `AllowHttp=true` verilebilir. Bu durumda API anahtarı şifresiz gider. Beklenen sözleşme:

- `GET {BaseUrl}{StockPath}[?cursor=…]`: `[...]` ya da `{ "items": [{ "sku", "barcode", "name", "quantity" }], "nextCursor": "…" }`. `stockCode`, `title`, `stock` ve `available` alan adları da kabul edilir.
- `POST {BaseUrl}{SalesPath}`: `{ requestId, channel, store, orderNumber, sku, barcode, quantity }`. Bu çağrı yalnızca `AllowWrites=true` iken yapılır. Kapalıyken satışlar kaynak güncellenene kadar yerelde düşülmeye devam eder.

Ürünler önce SKU'ya, SKU yoksa barkoda göre eşleşir. Stok gönderimi demo mağazalarda ve `AllowWrites` açık gerçek Trendyol mağazasında yapılır. Diğer gerçek pazaryerleri henüz salt okunurdur. Panelde "stok elle eşitlenmeli" olarak görünürler. Amazon FBA ve n11depom stoğunu pazaryeri yönettiği için bu ürünler atlanır. Durum `Data/central-stock.json` dosyasında tutulur ve şifreli yedeğe dahildir.

Kontrol: `node tests/central-stock-http-checks.cjs`.

## Doğrulama

```powershell
dotnet build src/MarketPilot.Web
dotnet run --project tests/MarketPilot.IntegrationChecks
node tests/frontend-checks.cjs
```

Kontroller; Product V2 ayrıntıları ve filtreleri, Order V2 müşteri/kargo/fatura dönüşümü, satıcı adresleri, sayfalama, kimlik başlıkları, eksik alanlar, 401/429 temizleme, eksik yapılandırma, demo ekranları ve 39 menünün korunmasını kapsar. Gerçek Trendyol hesabı, yalnızca yerel User Secrets tanımlandıktan sonra kullanıcı tarafından test edilebilir.

Resmî sözleşmeler: [Yetkilendirme](https://developers.trendyol.com/docs/2-authorization), [Product V2](https://developers.trendyol.com/docs/ürün-filtreleme-onaylı-ürün-v2), [Order V2](https://developers.trendyol.com/docs/sipariş-paketlerini-çekme-getshipmentpackages), [satıcı adresleri](https://developers.trendyol.com/reference/getsuppliersaddresses).

## Çalışan demo mağaza senaryoları — 27 Ekim 2026

Trendyol Merkezi'nde ana demo mağaza ve Outlet demo mağazası vardır. Her mağaza ayrı örnek stoklarla çalışır: 8 tekstil ürünü/varyantı, 6 sipariş, 2 iade, 3 müşteri sorusu ve finans kayıtları.

Deneme sırası:
1. Ürünler > Detay: özellikler, varyantlar, red nedeni ve mağaza stokları.
2. Stok ve fiyat > Düzenle: değişikliği kaydet; İşlem geçmişinde bekliyor/tamamlandı durumunu izle.
3. Siparişler > Detay: hazırlama, demo fatura, demo kargo ve teslimat adımlarını ilerlet.
4. Müşteri soruları > Yanıtla; İadeler > İncele ve onayla.
5. İşlem geçmişi > örnek başarısız işlemi yeniden dene.
6. Fatura ve garanti / Etiketler: geçersiz örnek belgeleri önizle.

Mağaza kayıtları `Data/integration-connections.json`, demo stok/sipariş/işlem verileri `Data/integration-demo.json` dosyasında saklanır. Bu dizin Git dışında kalır. JSON kalıcılığı, üretim veritabanı veya çok kullanıcılı işlem altyapısı değildir. Demo işlemleri ayrı uçlardan yürür; gerçek mağazaya uygulanamaz. Demo işleri artık sunucu açıkken arka plan servisiyle tamamlanır; tarayıcı veya okuma isteği gerekmez. Sunucu yeniden başladığında kayıtlı bekleyen işler devam eder. Bu, gerçek pazaryeri yazma kuyruğu değildir. Örnek belgeler gerçek fatura veya kargo etiketi olarak kullanılamaz.

Gerçek mağaza bağlantısı, ayrı satıcı kimlik profilleri, kimlik doğrulama/yetkilendirme, kalıcı üretim iş kuyruğu ve gerçek uçtan uca testler hâlâ tamamlanmalıdır.

Kontrol: `dotnet run --project tests/MarketPilot.DemoChecks`.

Yerel giriş/roller, arka plan kuyruğu ve şifreli yedek kurulumu: [YEREL_GUVENLIK_VE_YEDEK.md](YEREL_GUVENLIK_VE_YEDEK.md).


## Ortak katalog ve mağaza akışı

Ürün & Katalog ekranları ortak backend kayıtlarına bağlandı. Mağaza eşleştirmeleri, stok/fiyat önizleme ve demo iş sırası, genel sipariş/gönderi bağlantıları ve sınırlar için [ortak katalog raporunu](ORTAK_KATALOG_KAPSAM.md) okuyun. Gerçek mağazaya yazma kapalıdır; veritabanı bu aşamada kurulmadı.
