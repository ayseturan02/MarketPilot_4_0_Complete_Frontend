# WMS sevkiyat, kargo ve iade alışverişi

Bu modül yalnız mock olay kabul eder. Gerçek WMS webhooks, taşıyıcı, ERP veya pazaryeri bağlantısı kurmaz. Gönderiler (`#shipping`), Kargo Takip (`#tracking`), Etiketler (`#labels`) ve İadeler (`#returns`) ekranlarında kurgusal senaryolar ve paket bazlı geçmiş gösterilir. Senaryolar kullanıcı düğmeye bastığında yüklenir.

## Ortak olay modeli

`WmsLogisticsEvent`: event ID, paket bazlı sıra numarası, shipment/package/order/warehouse kimlikleri, kanal, taşıyıcı, takip numarası, durum, paket miktarı, sipariş miktarı, olay zamanı, sevk zamanı ve WMS yeniden satış onayı. Paketler kanal + shipment ID + package ID ile ayrılır; aynı sipariş farklı paketlere ve depolara ayrılabilir. Bu sürüm miktarları paket bazında taşır; SKU bazında stok muhasebesi yapmaz.

Akış: `received → picking → packed → shipped/partially-shipped → delivered/delivery-failed`. Başarısız teslimat yeniden sevke veya iade kabulüne gider. Teslim edilen sipariş de iade kabulüne ilerleyebilir. İade: `return-accepted → quality-check → quality-passed/quarantined → restocked` (yalnız quality-passed kolundan). Karantina ve restocked terminaldir. Sevkte takip ve sevk zamanı zorunludur. Kısmi sevk miktarı sipariş miktarından küçük olmalıdır.

Aynı event ID ve aynı içerik tekrar geldiğinde geçmişe eklenmez. Farklı içerik, atlanan sıra, zaman gerilemesi, kimlik değişikliği, miktar değişikliği veya geçersiz geçiş reddedilir. Kaydı olmayan paket received/1 ile başlamalıdır; eksik geçmiş inceleme gerektirir.

## Onay ve yetki

Kanal eşlemeleri `(channel, wmsState, channelState, approved)` ile tanımlanır. Onay yoksa `blocked-unmapped` gösterilir; WMS shipped otomatik olarak kanal shipped anlamına gelmez. Eşlemeler yalnız yerel gösterimdir; dışarı gönderim yapılmaz.

Etiket politikası `(channel, authority, approved)` içerir. authority `wms`, `carrier` veya `marketplace` olabilir. Onaylı politikada etiket ilgili kaynaktan beklenir; URL, barkod, PDF veya sahte etiket üretilmez.

Satılabilir iade miktarı yalnız quality-passed sonrasında restocked ve WmsRestockApproved=true olduğunda gösterilir. Bu alan bilgi amaçlıdır; fiziksel/satılabilir stok veya rezervasyon değişmez. Gerçek stok WMS stok görünümünden gelmeye devam eder; çift stok artışı yapılmaz.

## API ve kalıcılık

WMS uçlarının mevcut hesap/yönetici kontrolü uygulanır.

- `GET /api/wms/logistics`: paketler ve geçmişleri.
- `GET /api/wms/logistics/policies`: eşlemeler, etiket yetkileri ve geçiş tablosu.
- `PUT /api/wms/logistics/mapping`: kanal eşlemesi.
- `PUT /api/wms/logistics/label-policy`: etiket yetkisi.
- `POST /api/wms/logistics/mock/events`: mock ortak olay kabulü.
- `POST /api/wms/logistics/mock/scenarios`: teslimat, kısmi sevk/teslimat başarısızlığı/onaylı iade ve karantina senaryoları; yeniden yükleme çoğaltmaz.

Veriler işletmenin uygulama içerik kökündeki `Data/wms-logistics.json` dosyasına atomik değiştirme ile kaydedilir. Dosya hatasında bellek önceki duruma alınır. Tek süreçli yerel kullanım içindir; çoklu sunucu/veritabanı inbox çözümü değildir.

Doğrulama: `dotnet run --project tests/MarketPilot.WmsLogisticsChecks`. Tüm durum çiftleri (izin verilen ve reddedilen), olay tekrarları, sıralama, miktar, çoklu paket/depo, kanal onayı, üç etiket yetkilisi, iade onayı ve yeniden başlatma kalıcılığı denetlenir.

HTTP ve tarayıcı: `node tests/wms-logistics-http-checks.cjs`. Geçici içerik kökünde mock senaryo yükleme, dört ekran, politika onayı, karantina/iade miktarı ve geçersiz API geçişi kontrol edilir; çalışma alanının verileri değiştirilmez.
