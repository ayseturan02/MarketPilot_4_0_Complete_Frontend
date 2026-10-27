# Boyner Merkezi — 27 Ekim 2026

Boyner'in herkese açık resmî `OpenAPI 3.0.3 / v1.0.0` sözleşmesi incelenerek ayrı Boyner adaptörü, menüsü ve işletme ekranı eklendi. Gerçek hesap bağlanmadı ve gerçek pazaryeri yazması yapılmadı.

## Ekranlar ve demo

12 bölüm: Genel bakış, Ürünler, Siparişler, Stok ve fiyat, Kargo, İadeler, Finans, Marka ve kategori, Fatura ve garanti, Etiketler, Adresler, İşlem geçmişi.

- 8 ürün, 6 sipariş, örnek iadeler, kayıtlı değişiklikler ve kuyruk işleri.
- SKU/barkod kopyalama, açıklama/özellik/varyant detayı, komisyon/KDV ve sevkiyat süresi.
- Sipariş/paket ayrımı, kalemler, kargo, fatura bağlantısı ve paket geçmişi.
- Marka listesi, kategori arama ve zorunlu/varyant özelliklerini inceleme.
- Sevkiyat/iade adresleri; örnek paket kartları gerçek etiket olarak sunulmaz.
- Demo stok/fiyat değişikliği, sipariş ilerletme, örnek fatura, iade onayı ve başarısız işi tekrar deneme; tümü mevcut yerel kalıcı kuyruk üzerinde çalışır.
- Mağazalar/pazaryerleri birbirinden ayrıdır. Eski mağazaların verileri taşınmadı/değiştirilmedi; Boyner için ayrı demo oluşturulur.

## Gerçek hesap için yazılmış okumalar

| İş | Resmî servis | Uygulama |
| --- | --- | --- |
| Bağlantı ve mağaza adresleri | `GET /sapigw/suppliers/addresses?supplierId=...` | Satıcı, sevkiyat, iade adreslerini normalize eder |
| Ürün/stok/fiyat | `GET /sapigw/suppliers/{supplierId}/products` | Barkod/SKU/model, onay/arşiv/kısıtlama/satış durumu ve tarih filtresi |
| Siparişler | `GET /sapigw/suppliers/{supplierid}/orders` | Sipariş numarası, paket durumu, tarih; doğru `OrderByField` / `OrderByDirection` alanları |
| Askıdaki paketler | Aynı servis, `status=Awaiting` | Normal paketlerdeki `shipmentPackageStatus` filtresinden ayrıdır |
| İadeler | `GET /sapigw/suppliers/{supplierid}/v2/claims` | Paket ve iç içe iade kalemleri korunur; kalemler orderLineItemId ile eşleştirilir |
| Markalar | `GET /sapigw/brands` | Native büyük harfli `Page`/`Size` parametreleri |
| Kategoriler | `GET /sapigw/product-categories` | Tam ağaç sunucuda önbelleğe alınır; arama/yaprak sayfalaması sunucudadır |
| Zorunlu özellikler | `GET /sapigw/product-categories/{categoriId}/attributes` | Zorunlu, varyant, özel değer ve seçenek bilgileri |
| Toplu işlem sonucu | `GET /sapigw/suppliers/{supplierId}/products/batch-requests/{batchRequestId}` | Ürün bazında başarı/hata; sonucun COMPLETED olması her ürünün başarılı olduğu anlamına gelmez |

Bu okumalar gerçek satıcı hesabında doğrulanmış değildir. Resmî şemaya göre hazırlanmış yerel sahte HTTP yanıtlarıyla test edilmiştir.

## Boyner'e özgü ayrıntılar

- Host yalnız `https://merchantapi.boyner.com.tr` olarak sabittir.
- Resmî security scheme açıklamasındaki `supplierId:apiKey:apiSecretKey` üçlüsü Base64 Basic değeri olarak kullanılır; User-Agent belge örneğindeki `supplierId SelfIntegration` biçimindedir. Kimlik doğrulama biçimi gerçek hesapla ayrıca doğrulanmalıdır; sahte yanıt testi üretim yetkisini kanıtlamaz.
- Doğrulanmış ayrı bir test API hostu bulunmadığı için uydurulmadı. Ön üretim dokümanı da aynı API hostunu gösterir; yerel demo bunun yerine kullanılır.
- Belgedeki `/kargo-sirketleri-listesi` bir **referans tablosudur**, çağrılabilir bir API değildir. Adaptör bu adrese istek göndermez.
- Ürün onayının false olması ret olarak yorumlanmaz; onaysız durumu ayrı gösterilir. Ret nedeni alanı bildirilmediyse metin uydurulmaz.
- Paket ID'si ile packageNumber korunur. Belge örneklerinde saniye biçiminde gelen tarihlerin yanlışlıkla 1970 olarak gösterilmemesi için saniye/milisaniye okuma desteklenir. Sorgu tarihleri milisaniye gönderilir.
- Yanlış/taşan örnek parasal değerler sıfır yerine bilinmiyor olarak tutulur. TCKN istemci DTO'suna taşınmaz.
- Finans ekranı sipariş tutarlarını gösterir; hakediş, ödeme veya mutabakat entegrasyonu olarak sunulmaz.

## Sayfalama ve güvenlik

Sabit 25 kayıtlık listeler; sayfa boyutu/alt-üst stok-fiyat filtreleri yok. Ürün sayfa sınırı 2500'dür; daha büyük sorgular daraltılmalıdır. Marka servisi sayfa toplamı vermediğinde kesin toplam uydurulmaz. Kategori ağacı 15 dakika önbelleğe alınır, özellik değerlerinde ilk 100 seçenek ve toplam sayısı gösterilir.

Sunucuda `Marketplaces:Boyner:production:SupplierId`, `ApiKey`, `ApiSecretKey` gerekir. Anahtarlar tarayıcı formunda, sohbette veya Git'te paylaşılmaz. Her ortam için tek sunucu hesabı vardır; farklı mağaza adları bağımsız kimlik bilgileri oluşturmaz. Yönlendirme kapalı, istek süresi 45 saniye, yanıt boyutu 16 MB ile sınırlıdır. Ham sağlayıcı hata gövdesi kullanıcıya verilmez. Mevcut yerel oturum/rol koruması yeni uçlarda geçerlidir.

## Henüz tamamlanmayan gerçek işler

Ürün oluşturma/güncelleme/silme, stok-fiyat gönderimi, sürekli batch takibi, sipariş hazırlama/iptal/bölme, taşıyıcı değiştirme/takip bildirme, iade kabul/ret/oluşturma, fatura bağlantısı gönderme, etiket oluşturma ve PDF/ZPL indirme tamamlanmadı. Boyner stok/fiyat belgesi fiyatların sıfır/null gönderilmemesini ve liste/satış fiyatlarını ayrı yönetmeyi gerektirir; bu sözleşme incelendi ama gerçek yazma uçları açılmadı.

Etiket sorgusu deliveryCode ve format ister; belge başarılı yanıtı ayrıntılı dosya şeması vermediği için gelen veri PDF/ZPL varsayılarak tarayıcıya aktarılmadı. Demodaki paket kartları geçerli belge değildir.

## Doğrulama

- `dotnet run --project tests/MarketPilot.BoynerChecks -p:BuildProjectReferences=false`: resmî OpenAPI'ye uygun yanıtlar, auth/User-Agent, sayfalama, paket kimlikleri/tarihler, iç içe iadeler, başarısız batch kalemleri, marka/kategori/özellikler ve hata güvenliği.
- `node tests/boyner-http-checks.cjs`: ayrı geçici veri kökü/portta 12 bölüm, barkod filtresi, demo işlemler, kategori özellikleri, adres/etiket örnekleri, mağaza izolasyonu, roller ve mobil görünüm. Ana verileri değiştirmez.
- Mevcut ön yüz/ortak stok/filtre ve pazaryeri kontrolleri.

Kaynak: [Boyner resmî pazaryeri API belgesi](https://merchant.boyner.com.tr/api/docs/render). Sayfadaki gömülü OpenAPI sözleşmesinin tüm işlem listesi incelendi; yalnız yukarıda belirtilen okumalar uygulanmıştır.
