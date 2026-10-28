# Otomatik sipariş senkronizasyonu ve kalıcı stok kuyruğu

28 Ekim 2026. Gerçek mağaza bilgisi girilmedi, gerçek abonelik açılmadı ve gerçek mağazaya yazma izni verilmedi.

## Kullanım

Senkronizasyon ekranındaki **Otomatik senkronizasyon** bölümünde zamanlanmış sipariş çekmeyi açın, aralığı seçin ve **Kaynak stoğunu kanallara dağıt** seçeneğini ayarlayın. Ayarlar sunucuda saklanır. İlk etkinleştirmede hemen, sonraki turlarda seçili aralıkta çalışır.

- Aralık 30–3600 saniyedir. Sipariş kontrol aralığı 1–14 gündür.
- Tarayıcı kapalıyken işler devam eder. Sunucu kapalıyken çalışmaz; yeniden açıldığında kayıtlı plan ve bekleyen işler devralınır.
- Yönetici otomasyonu değiştirebilir ve hatalı/engellenmiş gönderimi yeniden deneyebilir. Görüntüleyici sadece durumu okur.
- Otomasyonu kapatmak yeni periyodik taramaları durdurur. Daha önce kuyruğa kabul edilmiş işler iptal edilmez.
- Eski Merkezi Stok otomatik seçeneği ile yeni zamanlayıcı birlikte çalıştırılmaz; yeni otomasyon etkinleştirilirken eski zamanlayıcı kapatılır.

## Sipariş çekme

Her mağazanın sonraki çalışma zamanı, son başarılı zamanı ve hata sayısı saklanır. Sayfalar/imleçler tamamen okunur; tekrar eden sayfa veya eksik devam bilgisi başarılı tarama sayılmaz.

Gerçek okuma modu açıldığında okunan siparişler ortak sipariş deposuna kaydedilir. Webhook ve dönemsel okuma aynı mağaza/paket kimliğini kullanır; daha eski kaynak zamanı güncel kaydı ezmez. Demo siparişleri zaten ortak görünümün demo deposundan gelir.

Başarısız okuma önceki siparişleri korur ve artan beklemeyle yeniden denenir. Yeniden başlatma sonrası kaldığı zaman aralığı ve kontrol penceresi kullanılır. Bu, sağlayıcının sunmadığı tarih/filtre alanlarını veya sınırsız geçmiş taramasını varmış gibi göstermez.

## Kalıcı stok gönderimi

Merkezi stok artık doğrudan dış gönderim yapmaz. Önce **marketplace outbox** kaydı oluşturur, arka plan işçisi bu kaydı işler:

`queued → dispatching → waiting → completed`

Hata ve koruma durumları: `blocked`, `failed`, `uncertain`, `superseded`.

- İş dosyaya yazılmadan gönderilmez. Kuyruk, mağazanın kaynak ve satıcı kimliğine bağlanır; hesap değişirse eski iş yeni hesaba gönderilmez.
- Aynı ürünün hedefleri kalıcı sıra numarasıyla işlenir; eşit zaman damgaları hedef sırasını bozmaz.
- Aynı aktif hedef tekrar sıraya eklenmez. Yeni hedef, henüz gönderilmemiş eski hedefin yerini alır.
- Demo işinde kalıcı işlem kimliği kullanılır; yeniden başlatma aynı işi çoğaltmaz.
- Gerçek HTTP isteğinin kabul edilmesi tamamlanma sayılmaz. Mağazadan hedef stok okunup doğrulanır.
- Gönderim sırasında kapanan uygulama, gerçek isteği körlemesine yeniden göndermez; önce mevcut stok doğrulanır.
- 429 yanıtları artan beklemeyle sınırlı sayıda tekrar denenir. Yetki, uyumsuz adaptör ve doğrulanamayan sonuçlar görünür kalır.
- FBA ve n11depom gibi kanal tarafından yönetilen demo stokları değiştirilmez.
- Disk yazma hatasında bellek önceki kalıcı duruma döner.

Demo dağıtımı tüm mevcut demo kanallarını kapsar. Gerçek stok/fiyat taşıyıcıları altı kanalı kapsar; mağaza bazlı kimlik profilleri ve yazma izinleri kullanılır. Ayrıntılar ve canlı doğrulama sınırları: [MAGAZA_HESAPLARI_VE_YAZMA.md](MAGAZA_HESAPLARI_VE_YAZMA.md).

## Gerçek bağlantılar için kapalı izinler

Varsayılan olarak gerçek mağazalar zamanlayıcıya dahil edilmez ve gerçek yazma kapalıdır:

- `Synchronization__IncludeRealStores=false`
- `Synchronization__AllowRealWrites=false`

Gerçek bağlantı aşamasında ilgili sunucu izinleri ve sağlayıcının kendi `AllowWrites` ayarı ayrıca hazırlanacak. Arayüzden otomasyonu açmak bu izinleri açmaz. Gerçek hesapla teslimat/servis sonucu testi bu aşamada yapılmadı.

## Webhook alıcısı

`POST /hooks/orders/{endpointId}` alıcı yolu mevcuttur. Kimlik doğrulama → kalıcı inbox → arka planda ortak sipariş kaydı akışı kullanılır. Alıcı adresi, eksik kurulum ve bildirim geçmişi Senkronizasyon ve Trendyol genel bakış ekranındadır.

Gerçek HTTPS adresi/anahtar/mağaza eşleştirmesi bağlantı aşamasında yapılır. Ayrıntılar: [WEBHOOK_KURULUM.md](WEBHOOK_KURULUM.md).

## Dosyalar ve sınırlar

- `Data/synchronization.json`: otomasyon ayarları ve mağaza çalışma planları.
- `Data/marketplace-outbox.json`: kalıcı stok gönderim işleri ve sonuçları.
- `Data/order-webhook-inbox.json`: gelen bildirimler.
- Bu dosyalar şifreli yedek kapsamına dahildir.
- Dosya deposu tek uygulama süreci içindir. Çoklu sunucu ve yüksek hacimli üretim için veritabanı/dağıtık kuyruk gereklidir.

## Kontroller

- `dotnet run --project tests/MarketPilot.AutomationChecks`: sayfalı sipariş aktarımı, zaman planı, yeniden başlatma, hata beklemesi, kuyruk kalıcılığı, idempotans, gerçek yazma koruması, 429, stok doğrulaması ve disk hatası. Taşıyıcılar sahtedir.
- `node tests/automation-http-checks.cjs`: otomasyonu tarayıcıdan açar; tarayıcıyı kapatır; sunucuyu yeniden başlatır ve gerçek periyodik işçinin yeni demo satışı/stoğu elle tetikleme olmadan işlediğini doğrular.
- `node tests/webhook-http-checks.cjs`: kimlik doğrulamalı alıcıdan sipariş/gönderi listesine uçtan uca test.
- Tüm testler: geliştirme sunucusu kapalıyken `npm run check`.
