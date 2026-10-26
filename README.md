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

Trendyol istemcisi yalnızca şu GET uçlarına çağrı yapar:

- `/integration/product/sellers/{sellerId}/products/approved`
- `/integration/product/sellers/{sellerId}/products/unapproved`
- `/integration/order/sellers/{sellerId}/v2/orders`
- `/integration/order/sellers/{sellerId}/orders/stream`
- `/integration/sellers/{sellerId}/addresses`

Fiyat, stok veya sipariş durumu değiştiren dış API metodu bulunmaz. `POST /api/integrations/preview/{marketplace}/stock-price` yalnızca yerel bir taslağı doğrular ve her zaman `sent:false` döndürür. Sonuçlar kalıcı veritabanına yazılmaz.

Uygulamada kullanıcı hesabı doğrulaması henüz olmadığı için entegrasyon uçları sadece yerel makineden ve aynı web kaynağından erişime açıktır. İnternete açık dağıtımdan önce kullanıcı yetkilendirmesi eklenmelidir.

## Doğrulama

```powershell
dotnet build src/MarketPilot.Web
dotnet run --project tests/MarketPilot.IntegrationChecks
node tests/frontend-checks.cjs
```

Kontroller; Product V2 ayrıntıları ve filtreleri, Order V2 müşteri/kargo/fatura dönüşümü, satıcı adresleri, sayfalama, kimlik başlıkları, eksik alanlar, 401/429 temizleme, eksik yapılandırma, demo ekranları ve 39 menünün korunmasını kapsar. Gerçek Trendyol hesabı, yalnızca yerel User Secrets tanımlandıktan sonra kullanıcı tarafından test edilebilir.

Resmî sözleşmeler: [Yetkilendirme](https://developers.trendyol.com/docs/2-authorization), [Product V2](https://developers.trendyol.com/docs/ürün-filtreleme-onaylı-ürün-v2), [Order V2](https://developers.trendyol.com/docs/sipariş-paketlerini-çekme-getshipmentpackages), [satıcı adresleri](https://developers.trendyol.com/reference/getsuppliersaddresses).
