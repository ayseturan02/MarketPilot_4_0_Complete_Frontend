# Sahte ERP ve webhook dayanıklılık testi — 29 Ekim 2026

`node tests/erp-webhook-resilience-http-checks.cjs`

Test, uygulamayı ayrı geçici kayıtlarla başlatır ve gerçek HTTP bağlantısı kullanan sahte ERP sunucusu oluşturur. Canlı ERP/pazaryeri anahtarı kullanılmaz; çalışan uygulamanın kayıtları değiştirilmez. Tarayıcıda görünen hata metinleri ayrıca doğrulanır.

| Senaryo | Doğrulanan sonuç |
| --- | --- |
| ERP aynı SKU'yu farklı barkodlarla iki kez döndürür | Tüm parti reddedilir; mevcut ürün kaydı değişmez. “Kaynak listesinde tekrarlayan SKU veya barkod var” nedeni senkronizasyon ekranında görünür. |
| ERP 200 başlığını gönderip yanıt gövdesini yarıda bırakır | İstek süre sınırında kesilir. “Harici servis zaman aşımına uğradı” nedeni ekranda görünür; eski kayıtlar korunur. |
| ERP yanıtı beklenirken sağlık isteği | `/api/health` 200 döndürür ve yanıt vermeye devam eder. Sonraki geçerli ERP okuması başarılıdır. |
| Webhook siparişinde aynı SKU'ya ait iki ayrı satır | Geçerli iki satır korunur. ERP ürün listesinde mükerrer kimlik ile siparişin ayrı satırları farklı kurallardır. |
| Aynı webhook yeniden gönderilir | Tek sipariş oluşur; yinelenen bildirim tekrar işlenmez. |
| Webhook işçisi siparişi kalıcı depoya yazamaz | Bildirim bağımsız kuyruğa kabul edilir. “Sipariş kalıcı depoya yazılamadı” nedeni webhook panelinde görünür; uygulama çalışır kalır. |
| Depolama sorunu kaldırılır | Hatalı iş otomatik yeniden denemeyle tamamlanır; sonraki sağlam iş de kaydedilir. |
| Gelen webhook HTTP isteği yarım gönderilip istemci zaman aşımıyla kesilir | Uygulama ayakta kalır ve sonraki geçerli bildirim işlenir. |

Webhook işçisi dış ERP HTTP isteği yapmaz; kalıcı bildirimleri yerel sipariş deposuna işler. Bu nedenle webhook tarafında kalıcı kayıt hatası ve gelen HTTP bağlantısının kesilmesi ayrı ayrı test edilir; dış ERP zaman aşımı işçiye aitmiş gibi sunulmaz.

ERP bağlantısında `Enterprise:Connectors:{bağlantı}:TimeoutSeconds` varsayılan 30 saniyedir; 1–120 saniye aralığına sınırlandırılır. Süre sınırı başlık ve yanıt gövdesini birlikte kapsar. Gönderim sonucunun belirsiz olduğu işler mevcut makbuz doğrulama akışını kullanır.

## Sonuç

Yeni HTTP/tarayıcı testi, mevcut `enterprise-sync-http-checks.cjs` ve `webhook-http-checks.cjs` kontrolleri geçti. Derleme 0 hata ve 0 uyarıyla tamamlandı. Yeni test `npm.cmd run check` tarafından otomatik çalıştırılır.

Hata anındaki ekran kayıtları:

- [ERP mükerrer SKU](artifacts/resilience/erp-duplicate-sku.png)
- [ERP zaman aşımı](artifacts/resilience/erp-timeout.png)
- [Webhook işçisi kayıt hatası](artifacts/resilience/worker-error.png)
