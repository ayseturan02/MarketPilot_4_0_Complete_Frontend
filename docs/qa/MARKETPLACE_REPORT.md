# Altı pazaryeri raporu

30 Ekim 2026 · HEAD `b787731` · Windows / .NET SDK 8.0.427, runtime 8.0.33 / Node 24.22.1 / Chrome 156.0.8078.74

**Altı kanalın tamamı canlıya çıkış için NO-GO; mock/fixture testleri geçti.** Native okuma/yazma kodunun bulunması üretim hesabında doğrulandığı anlamına gelmez. Ortak mapper için ChannelContractChecks çıktısı239 kontrol PASS bildiriyor; bu yalnız o paketin kendi sayacı, bütün projenin test sayısı değil.

**Durumlar:** Tamamlandı = belirtilen yerel kapsam doğrulandı; Kısmi = akışın yalnız bir bölümü; Demo = mock/fixture; Eksik = mevcut değil; Hatalı = tekrar üretildi; Doğrulanmadı = test edilmedi. Tamamlandı, canlı sağlayıcı sertifikasyonu anlamına gelmez.

| İşlem | Trendyol | Hepsiburada | n11 | Amazon TR | Boyner | Ticimax |
| --- | --- | --- | --- | --- | --- | --- |
| Bağlantı / kimlik | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Ürün okuma / SKU-barkod / ilan mapper | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Gerçek ürün oluşturma | Engelli | Engelli | Engelli | Engelli | Engelli | Engelli |
| Stok outbox / yazma hazırlığı | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| ERP fiyat policy / mock yayın | Demo | Demo | Demo | Demo | Demo | Demo |
| Gerçek yeni fiyat aktarımı | Kapalı | Kapalı | Kapalı | Kapalı | Kapalı | Kapalı |
| Sipariş çekme | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Paket / native durum okuma | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| WMS→kanal sevk / durum yazma | Kısmi / Demo | Kısmi / Demo | Kısmi / Demo | Kısmi / Demo | Kısmi / Demo | Kısmi / Demo |
| İptal / iade read ve talep hazırlığı | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Doğrulanmış uçtan uca native refund | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı |
| Webhook alımı / inbox | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Rate limit / hata / retry | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Pagination / cursor / tarih mapper | Tamamlandı* | Tamamlandı* | Tamamlandı* | Tamamlandı* | Tamamlandı* | Tamamlandı* |
| Async receipt / idempotency | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi | Kısmi |
| Kanal yönetimli stok | Merchant sınırı | Merchant sınırı | n11depom koruması | FBA koruması | Merchant sınırı | Merchant sınırı |
| Mevcut native .NET fixture paketi | PASS | PASS | PASS | PASS | PASS | PASS |
| Mevcut kanal HTTP/UI paketi | PASS | PASS | PASS | PASS | PASS | PASS |
| Gerçek hesap / native API teyidi | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı | Doğrulanmadı |

*Tamamlandı yalnız mevcut mapper/fixture kapsamındaki filtre taşıma ve desteklenmeyen filtre reddi anlamında. Her native sunucunun güncel tarihte bütün alanları aynı semantikle desteklediği doğrulanmadı. Engelli/Kapalı hatalı eksik değil; ürün ana kaynağı ve gerçek fiyat gönderimi sınırına uygun güvenlik kararı.

## Kanal farkları

- **Trendyol:** product/content/main ID+SKU+barkod; packageId. Ürün nextPageToken/order nextCursor ile bounded tarih taraması. Birçok ayrı native panel ve operation catalog mevcut (ürün, sipariş, iade, fatura, etiket, finans, webhook vb.); bu gelişmiş araçlar merkezi ERP/WMS workflow ile aynı kapsamda değildir. createV1/createV2 endpointte405; eski metadata tanımı native ürün oluşturma yetkisi sayılmaz. Batch kabulü receipt ile ayrılıyor. IntegrationChecks, trendyol-workflows ve ChannelContractChecks PASS.
- **Hepsiburada:** listingId/HB SKU, merchantSku ve barkod ayrı; order-line kimliği paket numarasından ayrılmalı. Güncel adapterde paket taraması var; fixture tests repeat/order-package identity ve çift satış düşmeme kontrollerini geçti. Paket sayfalaması≤10, header toplamı,24saat paket ve31gün sipariş pencereleri kodun mevcut sözleşmesi. Bunlar bu denetimde sağlayıcıdan yeniden onaylanmadı. Gerçek anonim paket yanıtıyla nihai kimlik teyidi canlı faz kapısı olmaya devam ediyor.
- **n11:** product ID/seller SKU, bazı barkod filtreleri desteklenmiyor ve açık reddediliyor. n11depom stock provider-managed; task kabulü uygulanma değil. Merchant stok yazımı öncesi sender doğrulanıyor. İade/finans okuması ve tam native mutation desteği birbirine eşit kabul edilmedi.
- **Amazon TR:** ASIN/content ve seller SKU; barkod bulunmayabilir. nextToken ve tarih gecikme kısıtları; native paketler korunuyor. FBA/AMAZON/AFN stok yazımı engelli; çok konumlu merchant stokta supply-source eşlemesi olmadan yazılmıyor. Eksik VAT/gross/discount/currency tahmin edilmiyor. SP-API/LWA ve native fixture kodu var; gerçek token/izin/veri erişimi denenmedi.
- **Boyner:** supplier/product/content ID+SKU+barkod; page/date sınırları, nullable KDV ve native alanların korunması. Yazma kodunda mağaza rate aralığı/gate ve batch receipt kontrolü var. Stok isteğinin mevcut fiyat alanlarıyla birlikte native gönderilmesi kapsamı ve güncel vendor sözleşmesi canlı öncesi ayrıca onaylanmalı.
- **Ticimax:** tenant’a özel HTTPS SOAP hedefi, WSDL metot/response parsing ve XML güvenliği; kart/varyant ID+SKU+barkod. Para birimi kaynaktan gelmezse uydurulmuyor; orderNumber için sahte paket ID yok. page:N token, native durum kodu ve tarih filtreleri var. Native metot/yetki sürümü müşteri kurulumuyla doğrulanmalı; demo kargo/iade/finans ekranı native mutation desteği değildir.

## Ortak riskler

Webhook route/inbox ve sağlayıcı imza politikalarının uygulamada bulunması her kanalın native webhook protokolüyle onaylandığı anlamına gelmiyor. API tarafından desteklenmeyen işlem501/409 veya review durumuna gidiyor; bütün kanallarda aynı durum adı varsayılmıyor. 429/5xx/timeout/retry testleri fixture düzeyinde; sağlayıcı kotası/production throttling fiilen ölçülmedi. Asenkron kabul, job reference ve işlenmişlik durumu alanları ayrılmıştır; gerçek receipt süreleri yok.

Account/tenant/credential profile ayrımı backend testlerinde doğrulandı. accounts HTTP paketinin FAIL’i viewer credential metadata erişimi beklemesinden; güvenli403 doğru, test yanlış. StockOutbox gerçek yazmaları açıkça konfigürasyona bağlı; gerçek fiyat ise tüm kanallarda hâlâ kapalı. Test sırasında teknik egress engeli + sahte kimlik + loopback HTTP fixture kullanıldı.

Canlı fazda her kanal için sıra: salt okunur sandbox/account doğrulama → anonim payload contract → cursor/date/retry/permission matrisi → az ürünle guardrail ve receipt doğrulama → stok sevk/iptal/iade mutabakatı → kontrollü yazma onayı. Bu denetim bu adımların hiçbirini canlıda yapmadı.

Kanıt: ChannelContracts.cs:31–81 ve kanal mapper; ilgili adaptör/endpoint kaynakları; AccountWriteChecks / ChannelContractChecks / altı kanal .NET logları; amazon/hepsiburada/n11/boyner/ticimax HTTP logları ve trendyol-workflows; [ana test tablosu](ALL_TEST_RESULTS.md).
