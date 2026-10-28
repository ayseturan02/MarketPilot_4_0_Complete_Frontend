# Ön yüz ve arka uç senkronizasyonu

28 Ekim 2026. Gerçek hesap/anahtar eklenmedi; gerçek mağazaya işlem gönderilmedi.

## Çalışan akış

Senkronizasyon ekranındaki **Demo Eşitle**, `POST /api/sync/demo` üzerinden merkezi stok taramasını başlatır. Bu düğme yalnız aktivite kaydı oluşturmaz ve katalog sayfasına yönlendirmez. Kaynak okunur, yeni satışlar işlenir, kanal işleri kalıcı kuyruğa alınır. Ekrandaki bekleyen/tamamlanan/hatalı sayıları `GET /api/sync/status` yanıtından gelir.

- Demo kanalları ve gerçek kaynaklar ayrı dosya/işlem kapsamları kullanır. Demo satış gerçek ERP'ye gönderilemez.
- Sipariş taraması tüm sayfaları/imleçleri okur. Tekrarlayan sayfa, eksik devam bilgisi veya okunamayan kaynakta dağıtım durur.
- Trendyol sipariş akışı son güncelleme tarihine göre okunur; sonraki turlar son taramadan 5 dakika geriye bakar. Sipariş tarihine göre filtreleyen diğer pazaryerlerinde her tur en az son 2 günü kapsar; böylece yeni siparişlerin iptali kaçmaz. Daha eski siparişlerin iptali bu pazaryerlerinde ayrıca izlenmelidir.
- Stok kaynağında tekrar eden SKU veya barkod varsa tur nedeniyle durur; uygulama ve diğer arka plan işleri çalışmaya devam eder.
- Gerçek stok kaynağı ilk açıldığında eski `central-stock.json` gerçek kaynakla kullanılmışsa yalnız gerçek mağazalara ait bildirilmemiş satışlar, izlenen sipariş satırları ve güvenlik stoğu taşınır; otomatik senkronizasyon kapalı başlar. Demo verisi taşınmaz.
- Eşleşmeyen satış işlenmiş sayılmaz; eşleşme sağlandığında yeniden denenir.
- Tekrar tarama satışları çoğaltmaz. Takip edilen iptal ve onaylı demo iadesi stoğa bir kez yansır.
- Demo kuyruk işleri tarayıcı kapalıyken de işlenir; kaydedilen stok ve işler sunucu yeniden başlatıldığında korunur.
- Açık katalog, mağaza, sipariş ve senkronizasyon ekranları sunucudaki sürüm değişimini izler. Form doldurulurken veya pencere açıkken ekran zorla yenilenmez.
- Gerçek stok gönderiminde HTTP kabulü başarı sayılmaz; gönderilen miktar mağazadan okunup doğrulanana kadar beklemede tutulur. Bu yol yalnız sahte taşıyıcılarla/kodla kontrol edildi; gerçek bağlantı testi yapılmadı.
- Geçersiz ürün güncellemesi hiçbir alanı değiştirmez. CSV hücreleri formül girişine karşı korunur.
- Tarayıcı taslakları kullanıcıya göre ayrıdır; depolama hatasında başarı bildirimi verilmez.

## Birbirinden farklı işlemler

**Merkezi katalog düzenleme** mağazaları kendiliğinden değiştirmez: hedef seçimi → önizleme → uygula → işlem geçmişi akışı kullanılır.

**Merkezi stok otomasyonu** seçili stok kaynağını esas alır ve güvenlik stoğunu düşerek kanallara dağıtır. Otomasyon açıkken kanaldaki elle girilmiş stok sonraki turda kaynakla eşitlenir. Bu beklenen davranıştır.

Fatura/finans gibi “tarayıcı taslağı” yazan ekranlar hâlâ taslak hazırlamadır; fatura kesme, muhasebe kaydı veya pazaryeri işlemi değildir. Bu taslaklar sunucu yedeğine dahil değildir.

## Doğrulama

- `dotnet run --project tests/MarketPilot.SynchronizationChecks`: sayfalama, hesap takma adları, mod ayrımı, eşleşme sonrası tekrar, iptal/iade, kaynak hatası, tekrar sayfa ve disk hatasında geri alma.
- `node tests/synchronization-http-checks.cjs`: tarayıcıdan eşitleme/satış, tarayıcı kapalıyken kanal güncellemesi, yeniden başlatma, açık ekranda stok yenileme, CSV, hesap ayrımı, salt okunur yetki ve mobil ekran.
- Mevcut katalog, oturum, webhook ve pazaryeri testleri korunur.

Kontroller derleme yaptığı için önce çalışan geliştirme sunucusunu durdurun; kontrollerden sonra yeniden başlatın.

Tüm kontroller: .NET 8 SDK ve Node 20+ kurulu iken `npm ci`, `npx playwright install chromium`, `npm run check`. Windows'ta varsayılan Chrome kullanılır; paketli Chromium için `PLAYWRIGHT_CHANNEL=chromium` ayarlanabilir. Başka bir mevcut Playwright kurulumunu kullanmak için `PLAYWRIGHT_MODULE` tanımlanabilir. Testler geçici veride çalışır; gerçek anahtarları kullanmaz.

GitHub Actions dosyası bu kontrolleri çalıştıracak şekilde eklendi; uzaktaki çalıştırma ancak değişiklikler GitHub'a gönderildikten sonra doğrulanabilir.

## Sonraki aşama

Gerçek satıcı yetkileri ve mağazaya özgü kimlik profilleri, gerçek ERP'nin satış/iptal miktarı sözleşmesi, gerçek batch sonuçları ve rate limit davranışları bağlantı kurulurken ayrıca test edilecek. JSON deposundan üretim veritabanına geçiş, uzak yedek ve tam geri yükleme ayrı işlerdir. Bu yerel testler canlı mağaza veya üretim yük testi yerine geçmez.
