# Veritabanı bağlanmadan hazırlanan operasyonlar

Veritabanı bağlantısı eklenmedi. Yeni kayıtlar mevcut işletmeye ait `demo-store.json` içinde, mevcut dosya kilidi ve geri alma mekanizmasıyla saklanır. Her işletmenin servisleri, kuyrukları ve yedekleri ayrıdır. Kullanım ekranı: Senkronizasyon Merkezi → **Stok ve sipariş kontrolleri**. Sipariş detayından da işlem taslağı hazırlanabilir.

## Stok rezervasyonu

Önce ortak eşitleme planını kaydedin. Kurallar sekmesinde kaynak stoğun **rezervasyonlar düşülmemiş fiziksel stok** olduğunu seçerek rezervasyonu açın. Kaynak zaten satılabilir stok veriyorsa bu özellik açılmaz; aynı rezervasyon iki kez düşülmez.

- Elle süreli stok ayrılabilir. Aynı işlem kimliği aynı rezervasyonu döndürür; farklı içerikle kullanılırsa reddedilir.
- Eşzamanlı rezervasyonlar ve yerel siparişler aynı kayıt kilidi içinde kontrol edilir. Ayrılmış stok başka siparişe verilemez.
- Rezervasyon kimliği yerel siparişe verilirse rezervasyon tüketilir ve stok çıkışı bir kez kaydedilir.
- Gerçek panel siparişleri okunduğunda bilinen Nebim ürünleri için rezervasyon oluşur. Gönderilmiş siparişin rezervasyonu kaynak stok onayına kadar tutulur.
- Bölünmüş eski paket ile yeni paketlerin aynı sipariş/ürün rezervasyonu iki kez sayılmaz. Eski paket aktif sipariş tutarından çıkarılır.
- Sevkten sonra gelen geç iptal veya iade bildirimi fiziksel stoğu kendiliğinden artırmaz.
- Kuyrukta bekleyen stok gönderimi rezervasyon sonrasında satılabilir miktarı aşıyorsa gönderim engellenir; barkod ve yalnız SKU ile yapılan işlemler kontrol edilir.
- Etkin stok planında rezervasyon değişikliği stok görevini yeniden tetikler. Kaynak onayı beklenirken artış engellenir; güvenli azaltma yapılabilir.

ERP kaynak bildirimi, gönderilmiş paketlerin stoktan işlendiğini ürün güncellemesiyle birlikte bildirebilir:

```json
{
  "requestId": "erp-snapshot-123",
  "items": [{"sku":"SKU-1","barcode":"BC-1","name":"Ürün","stock":21,"price":100,"currency":"TRY"}],
  "stockAcknowledgements": [{"storeId":"magaza-kimligi","source":"magazanin-kaynak-kimligi","packageId":"paket-kimligi"}]
}
```

Bildirim mevcut HMAC imzasıyla doğrulanır. Onay yalnızca aynı mağaza/kaynak/paketin, güncellenen ürünlerine ait stok onayı bekleyen rezervasyonlarını çözer. Stok sahibi WMS ise ERP stok onayı kullanılmaz; WMS stok satırındaki `stockAcknowledgements` alanı kullanılır. Sağlayıcı adaptörü bu onayı gerçek stok işlemi tamamlandıktan sonra üretmelidir.

## Kısmi işlemler ve paketler

- Kısmi iptal: Yerel, sevk edilmemiş siparişin seçilen miktarı onayla iptal edilir; stok bir kez geri eklenir. Fazla miktar veya değişmiş sipariş reddedilir.
- Kısmi iade: Yerel sevk edilmiş sipariş için talep açılır. Stok, **Depo kabulü** adımında benzersiz kabul/barkod referansı kaydedilince eklenir. Tekrar kabul aynı stoğu iki kez artırmaz.
- Paket bölme: Paketler siparişin kalan satırlarını tam karşılamalıdır. Eksik/fazla miktar reddedilir. Ekrandan iki paket, API'den 50 pakete kadar planlanabilir. Sipariş değişirse yerel plan geçersiz işaretlenir.
- Panel siparişleri için işlem taslağı ve onaylı gönderim kuyruğu hazırdır. Gerçek satır kimliği, mağaza kimliği, izin ve bağlantı eksikse gönderim yapılmaz. Panelin kesin sonucunu sağlayıcı adaptörü döndürür; taslak oluşturmak paneli değiştirmez.

## Veri ve finans mutabakatı

Manuel veya belirlenen aralıkla son okunan panel değerleri karşılaştırılır. Stok, fiyat, para birimi, eşleştirme, eski veri, stok yetersizliği, uzayan kuyruklar ve webhook/aktarımı hataları bulunur. Bu tarama kendiliğinden bütün panelleri yeniden okumaz ve farkları otomatik düzeltmez; eski veriler ayrıca işaretlenir. Güncel panel okuması mevcut kanal ekranlarından/eşitleme akışlarından yapılır.

Hakediş kaydında sipariş tutarı ile komisyon, kargo, diğer kesinti ve iade kullanılarak beklenen net hesaplanır. Kaydedilen ödeme ile fark gösterilir. Farklı para birimleri birleştirilmez; aynı finans referansının tekrar girilmesi çift kayıt oluşturmaz. Bu ekran kayıt karşılaştırmasıdır; banka tahsilatı veya ödeme transferi değildir.

## Belge, ödeme ve dış bildirim bağlantısı

Fatura gönderim taslağı, geri ödeme talebi ve operasyon bildirimi için ayrı onay/aktarım kayıtları hazırdır. Taslaklar dış servise gönderilmez. Onayda bağlantı ve yazma izinleri kontrol edilir; sonuç doğrulanmadan tamamlandı sayılmaz. Yanıt alınamayan gönderim aynı kimlikle makbuz sorgusuna geçer.

Sunucuda `Enterprise:Connectors:documents`, `payments`, `notification` veya `channel-{storeId}` için BaseUrl, ApiKey ve AllowWrites tanımlanır. İşletmelerde bunlar `Businesses:{businessId}:Enterprise:Connectors:...` altındadır. Anahtarlar arayüze gönderilmez.

Mevcut MarketPilot ara servis sözleşmesine eklenen yollar:

| POST yolu | Kullanım |
| --- | --- |
| /v1/order-adjustments | Panel iptal/iade talebi |
| /v1/fulfillment-splits | Panel paket bölme talebi |
| /v1/documents | Belge gönderim talebi |
| /v1/refunds | Geri ödeme talebi |
| /v1/notifications | Operasyon uyarısı |

Gövde `{ idempotencyKey, payload }` biçimindedir. Mevcut `/v1/receipts/{id}` sözleşmesi kullanılır. `completed` ancak gerçek sağlayıcı işlemi tamamlandığında ve servis referansı oluştuğunda verilmelidir. Bunlar sağlayıcıların yerel API yolları değildir. Nebim, WMS, kargo, e-belge, ödeme ve bildirim sağlayıcısının gerçek adaptasyonu kendi sözleşmesine göre tamamlanmalıdır. Bu çalışma canlı e-Fatura düzenlemez, gerçek para iadesi veya e-posta göndermez.

Bildirimler varsayılan kapalıdır. Aynı bulguların tekrar taranması aynı bildirimi tekrar oluşturmaz; sorun çözülüp yeniden oluşursa yeni bildirim üretilebilir. Bildirim kanalının kendi hatası tekrar bildirim üreterek kuyruk döngüsü oluşturmaz.

## Geri yükleme kontrolü

Yönetici, yedek oluşturup izole geri yükleme provası çalıştırabilir. Arşiv önce şifre, hash, dosya adı ve boyut denetiminden geçer; ayrı geçici alana çıkarılıp ürün/sipariş/mağaza/kuyruk kayıtları tekrar okunur. Mevcut işletme verileri üzerine yazılmaz. Bu özellik canlı kayıtları değiştiren bir geri yükleme komutu değildir; gerçek aktivasyon sunucu durdurularak ayrıca planlanmalıdır.

## Kontroller

`node tests/operational-preparation-http-checks.cjs` eşzamanlı rezervasyon, kaynak onayı, paket devri, geç iptal, kısmi iptal/iade, kuyruktaki eski stok hedefinin engellenmesi, hakediş, bildirim tekrarı, belirsiz belge gönderimi, yeniden başlatma, yedek provası, yetkiler ve yedi arayüz sekmesini kontrol eder. Bütün dış servisler yerel sahte sunucudur. Test `npm.cmd run check` kapsamındadır.

Doğrulama sonucu: derleme 0 hata/0 uyarıyla geçti. Genel çalıştırmada 38 grup başarılı oldu; n11 mağaza formunun gecikmiş veri yanıtıyla sıfırlanması yakalandı ve düzeltildi. n11 kontrolü geciktirilmiş HTTP yanıtını zorunlu olarak üreten yeni senaryoyla, ön yüz kontrolü de ayrıca tekrar çalıştırılarak geçti. Toplam kapsam 13 .NET grubu + 24 HTTP/ön yüz grubu + 2 işletme/worker denetimi = 39 gruptur.
