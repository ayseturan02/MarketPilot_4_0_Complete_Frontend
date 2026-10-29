29 Ekim 2026 sonuç: `npm.cmd run check` tek çalıştırmada 39 grubun tamamını geçirdi; derleme 0 hata, 0 uyarı. Son düzeltmeler ve kalan işlevsel kapsam: [entegrasyon incelemesi](ENTEGRASYON_INCELEMESI.md). Ham çıktı: [integration-review-check.log](artifacts/integration-review-check.log). Aşağıdaki eski sayımlar önceki kontrollerin tarihçesidir.

# Genel entegrasyon kontrolü — 29 Ekim 2026

Son hazırlık: veritabanı bağlanmadan merkezi rezervasyon, kısmi sipariş işlemleri, paket devri, veri/hakediş mutabakatı, bildirim/belge/ödeme aktarım taslakları ve izole geri yükleme provası eklendi. [Kullanım ve gerçek bağlantı sınırları](VERITABANSIZ_OPERASYON_HAZIRLIGI.md). Test kapsamı iki yeni HTTP grubu ile 39 gruba çıktı; n11 form düzeltmesi ve ilgili tekrar kontrolleri bu belgede açıklanır.

Güncelleme: İşletme bazında kayıt/servis ayrımı ve demo worker hata yalıtımı eklendi. Başarısız regresyon kontrolleri düzeltildi; iki yeni kontrolle birlikte toplam 37 grup tek genel çalıştırmada geçti. Ayrıntılar: [regresyon raporu](REGRESYON_TEST_RAPORU.md), [işletme ayrımı](ISLETME_AYRIMI.md).

Bu proje yerelde çalışan bir entegrasyon yönetim uygulamasıdır. Canlıya alınmadı; gerçek Nebim, WMS ve kargo hesapları bağlanmadı. Aşağıdaki kapsam kaynak kodu ve yerel testlerle değerlendirilmiştir. Bir ekranın bulunması, her pazaryerinde o ekranın gerçek API işleminin tamamlandığı anlamına gelmez.

| Alan | Bugünkü kapsam |
| --- | --- |
| Ürün | Nebim kaynaklı ürün, SKU, barkod, fiyat, stok ve HTTPS görseller; manuel yeni ürün oluşturma kapalı |
| Katalog | Mağaza eşleştirme, kategori/marka/özellik bağlama, filtreleme, sayfalama, toplu stok/fiyat önizleme ve gönderim kuyruğu |
| Mağaza | Trendyol, Hepsiburada, Amazon, n11, Boyner ve Ticimax adaptörleri; ayrı satıcı kimlik profilleri ve demo mağazaları |
| Senkronizasyon | Mağaza ve veri yönü seçimi, görev başına aralık, otomatik çalışma, manuel tetikleme ve kalıcı görev sonuçları |
| Sipariş | Mağazalardan okuma, ortak sipariş/kalem kayıtları, durum takibi, tekrar kayıt koruması ve ERP/WMS aktarımı |
| Depo | WMS stok okuma, sipariş gönderme, paket barkod/miktar doğrulama; stok sahibi ERP veya WMS seçimi |
| Kargo | Onaylı paket oluşturma kuyruğu, takip/etiket bağlantısı alma, mağazaya bildirim kuyruğu; uyumlu ara servis gerekli |
| İade / müşteri | Yerel ve demo iade, geri ödeme talebi ve soru yanıtlama akışları; gerçek kapsam kanala göre değişir |
| Belgeler | Fatura, iade belgesi ve etiket taslakları; resmî belge veya para transferi sayılmaz |
| Analiz | Ortak sipariş/katalog verisinden rapor, maliyet ve katkı hesabı; para birimleri ayrı tutulur |
| Kullanıcı | Yönetici, operatör, görüntüleyici; oturum, parola değişimi ve rol kontrolü |
| İşletim | Kalıcı kuyruk, yeniden başlatmada devam, belirsiz gönderim sonucunu sorgulama, sistem olayları ve şifreli yedek |

## Bu kontrolde tamamlanan işler

- ERP SKU/barkodları eşleştirmeden önce normalize edilir; baştaki/sondaki boşluklar ikinci ürün oluşturamaz. Aynı kaynak listesindeki bu tür çakışmalar topluca reddedilir.
- Boş görev adı 500 yerine anlaşılır 400 yanıtıyla reddedilir.
- Seçilen stok kaynağından ilk başarılı okuma gelmeden mağazaya stok gönderimi engellenir.
- Merkezi eşitleme ekranına geçişte kaybolan yönetici webhook izleme paneli geri bağlandı; gelen sipariş bildirimleri ve tekrar işleme takibi korunur.
- Yönetici için dış bağlantı yapmadan eksik ayarları kontrol eden `/api/sync/enterprise/readiness` eklendi. Demo hedefi canlı hazır olarak göstermez, anahtar döndürmez.
- Kontrol çalıştırıcısı bir test hatasında kalan testleri atlamak yerine başarısızları topluca raporlar; testlerden canlı Enterprise bağlantı ayarları temizlenir.
- Eski ürün oluşturma ve otomasyon ekranını esas alan kontroller, ERP kaynaklı ürün ve ortak eşitleme planına uyarlanır.

## Sonradan yalnızca anahtar girilerek tamamlanamayacak noktalar

1. Nebim sürümü, WMS ve kargo firmasına göre yerel API adaptasyonu / uyumlu ara servis gerekir. Mevcut `/v1/...` sözleşmesi MarketPilot'a aittir; sağlayıcıların yerel API'si olduğu varsayılmamalıdır.
2. Her kanalın tüm ekranları aynı gerçek işlem kapsamına sahip değildir. Bazı iade, müşteri sorusu, finans, belge ve kargo operasyonları demo veya eksik gerçek adaptör yanıtı kullanır. Bunlar sağlayıcı sözleşmesiyle tamamlanmalıdır.
3. Resmî e-Fatura/e-Arşiv, muhasebe mutabakatı ve gerçek ödeme iadesi için ilgili hizmetlerin adaptörleri gerekir. Yerel taslaklar bu hizmetleri gerçekleştirmez.
4. Çok sunuculu kullanım, veritabanı işlemleri ve yüksek erişilebilirlik henüz yoktur. Seçili kaynak stoğu için merkezi rezervasyon eklendi; farklı depolar arasında otomatik sipariş dağıtımı ayrı kapsamdır. Mevcut kalıcılık tek süreç kilidiyle korunan JSON dosyalarıdır. Yerel denemeye uygundur; üretim ölçeği için ayrı çalışma gerekir.
5. Gerçek kimliklerle bağlantı, kategori eşleştirmesi, tam/kısmi iptal ve iade, birden fazla paket, kargo hatası ve yeniden deneme senaryoları uçtan uca kabul testinden geçmelidir. Yerel sahte servis testleri bunu kanıtlamaz.

Yerelde eksik ayarların bulunması normaldir. Hazırlık kontrolü gerçek bağlantı testi yapmaz ve sisteme canlı yeterlilik belgesi vermez. Bağlantı sözleşmesi ayrıntıları: [NEBIM_WMS_KARGO.md](NEBIM_WMS_KARGO.md).

## Doğrulama sonucu

Derleme 0 hata ve 0 uyarıyla tamamlandı. Toplam 13 .NET kontrol grubu ve 22 HTTP/ön yüz kontrol grubu çalıştırıldı. İlk taramada eski ürün oluşturma/ekran beklentileri, webhook paneli ve henüz yeniden derlenmemiş stok kontrolü nedeniyle başarısız olan altı grup, düzeltmeler ve son derlemeden sonra yeniden çalıştırıldı; altısı da geçti. ERP/WMS/kargo testi ayrıca onaylı paket oluşturma, takip/etiket okuma ve mağazaya kargo bildiriminin tam zincirini sahte servisle doğruladı. Canlı sağlayıcı hesabı kullanılmadı.
