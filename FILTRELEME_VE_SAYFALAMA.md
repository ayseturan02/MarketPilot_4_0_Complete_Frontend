# Filtreleme ve büyük listeler

27 Ekim 2026.

- Tüm Ürünler, Stok ve Depolar ve Barkod Yönetimi: sunucuda ad/SKU/barkod araması, durum, kategori, kanal, stok ve fiyat aralıkları; 25/50/100 kayıtlık sayfalar.
- Yerel Siparişler: sipariş/müşteri araması, durum, kanal, tarih ve sıralama. Yerel Gönderiler: sipariş/takip araması, firma, durum ve tarih.
- Trendyol ürün/stok listesi: canlıda sağlayıcının desteklediği tam barkod/SKU ve yayın filtreleri. Demo ürün listesinde ayrıca ad, stok/fiyat aralığı ve sıralama.
- Trendyol sipariş/kargo listesi: numara, barkod, durum ve tarih. Geniş listede cursor tabanlı ilerleme; numara/barkodla dar sorguda normal sayfalama. Canlı tarih sorguları en fazla 14 günlük aralıkla yapılır.
- Demo iadeler, finans, faturalar, etiketler ve işlem geçmişi: sunucuda filtre, tarih ve sayfalama.
- Filtreler açıkça Uygula ile gönderilir; her tuşta ağ isteği yapılmaz. Filtreler mağaza/sekme bazında ayrıdır. Yeni filtre önceki sayfa/cursor bilgisini sıfırlar; eski sorgu yanıtı yeni filtreyi değiştirmez.
- Tarayıcı önbelleği sınırlıdır. Çok varyantlı bir sağlayıcı sayfası bile kullanıcıya seçilen satır sayısına bölünür. Ürün başına otomatik mağaza sorgusu yapılmaz; ayrıntı tıklanınca yüklenir.

## Doğrulama

- `dotnet run --project tests/MarketPilot.ListingChecks`: 100.000 ürün ve 100.000 sipariş üzerinde uzak kaydı bulma, stok/durum/tarih filtreleri, sıralama ve sayfa sınırı.
- `node tests/listing-protocol-checks.cjs`: ürün tokenları, sipariş cursor'ları, filtrede sıfırlama, varyant sayfaları ve önbellek sınırı; ağ erişimsiz.
- Chrome: demo/yerel filtreler, barkod/gönderi ekranları, mobil taşma; sahte canlı sağlayıcıyla yalnızca 25 kaydın yüklenmesi ve cursor ilerlemesi.

Bu kontroller üretim yük testi değildir. Mevcut yerel/demo veri deposu JSON ve bellekte sorgulama kullanır. Canlı Trendyol'un desteklemediği ürün adı, fiyat/stok aralığı, kategori bazında kapsamlı arama için ürünlerin indeksli bir veritabanına aktarılması gerekir. Çok kullanıcılı 100 bin kayıt işletimi için veritabanı indeksleri, kalıcı senkronizasyon, servis kota yönetimi ve gerçek yük testi ayrıca tamamlanmalıdır.

Sağlayıcı sınırları:
- [Ürün filtreleme](https://developers.trendyol.com/docs/ürün-filtreleme-onaylı-ürün-v2)
- [Sipariş akışı](https://developers.trendyol.com/docs/sipariş-paketlerini-akış-ile-çekme)
- [Sipariş sayfalama sınırları](https://developers.trendyol.com/docs/sipariş-paketlerini-çekme-getshipmentpackages)
