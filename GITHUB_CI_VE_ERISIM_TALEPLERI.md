# GitHub CI ve erişim talepleri

`main` dalına doğrudan gönderim yerine PR kullanılır. Yönetici dahil `checks` kontrolünün GitHub Actions (app ID 15368) tarafından başarılı üretilmesi ve dalın güncel olması gerekir. Force-push ve dal silme kapalıdır; PR konuşmaları çözümlenmelidir. Ayrı insan onayı sayısı sıfırdır: yetkili kişi CI geçince PR'ı birleştirebilir.

Workflow: [.github/workflows/checks.yml](.github/workflows/checks.yml). Node 22, .NET 8 ve Chromium ile `npm run check` çalıştırılır. Gerçek pazaryeri kimlik bilgileri test ortamına taşınmaz. Yerel doğrulama: `npm run check`.

Arka plan işçileri beklenmeyen hataları kaydeder ve çalışmaya devam eder; yalnız uygulama kapanışına ait iptal döngüyü sonlandırır. Demo kuyruğunun hata denetim kaydı da aynı korumayı kullanır. Regresyon kontrolleri bozuk kuyruk verisi ve yedek XML'iyle işçilerin açık kaldığını, kapanışın hatasız olduğunu ve CSV'nin tr-TR/de-DE/en-US ortamlarında aynı sayısal sütunları ürettiğini sınar.

Açılan erişim talepleri:

- [#2 Trendyol stage hesabı ve test yetkileri](https://github.com/ayseturan02/MarketPilot_4_0_Complete_Frontend/issues/2)
- [#3 ERP / merkezi stok API erişimi ve sözleşmesi](https://github.com/ayseturan02/MarketPilot_4_0_Complete_Frontend/issues/3)
- [#4 Sunucu, alan adı ve HTTPS staging yayını](https://github.com/ayseturan02/MarketPilot_4_0_Complete_Frontend/issues/4)

Talepler depo sahibine atanmıştır. Taleplerin açılması erişimlerin teslim edildiği anlamına gelmez; henüz hesap/anahtar/sunucu sağlanması beklenir. Sağlayıcı parolaları, API anahtarları ve SSH anahtarları açık issue veya kaynak kodunda paylaşılmaz.
